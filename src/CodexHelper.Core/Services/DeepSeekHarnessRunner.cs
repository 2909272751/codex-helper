using System.Net.WebSockets;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>DeepSeek Harness 任务的结构化状态（映射 task/session/workspace/URL）。</summary>
public sealed record HarnessTaskStatus(
    string TaskId,
    string ProjectRoot,
    string TaskDirectory,
    string State,
    string Message,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime StartedUtc,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime UpdatedUtc,
    int HostProcessId,
    string WebUrl,
    string? SessionId = null,
    string? ProjectSessionId = null,
    string Executor = "DeepSeek Harness",
    string? Model = null,
    int Steps = 0,
    long UncachedInputTokens = 0,
    long CacheReadTokens = 0,
    long OutputTokens = 0,
    string? ContractFingerprint = null,
    string? RootCauseKey = null,
    string ExecutionMode = "codex-contract",
    string PermissionMode = "danger-full-access",
    string ExecutionStrength = "standard",
    string SessionState = "unknown",
    string? Stage = null,
    int ToolCallCount = 0,
    int ReasoningEventCount = 0,
    int NoProgressWarnings = 0,
    string? StateSource = null,
    string? EventTransport = null,
    string? EventTransportError = null)
{
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "starting", StringComparison.OrdinalIgnoreCase);
}

/// <summary>ISO 8601 UTC 序列化器（标准 JSON）。</summary>
public sealed class HarnessUtcConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTimeOffset.Parse(reader.GetString() ?? string.Empty, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// Harness 任务中继 runner（rc.6 原生协议）：接收绝对项目根目录和唯一任务目录，
/// 任务正文只从文件读取（绝不进入命令行/提示）；通过 HTTP RPC 创建标准编码会话
/// （cwd=项目根、agentPreset=standard），通过 WebSocket /api/events.mux 监听会话运行/
/// 完成/失败并把真实 sessionId 写入任务状态；用户停止通过 session.cancel。
/// 事件按 seq 去重（同连接重复帧/重连重放帧不推进状态、不重复计数）；每次 turn/start 重置
/// step 级退化检测窗口（WS 与 HTTP 轮询一致），检测文本/工具调用退化并立即 session.cancel
/// 写 failed（等待 GPT 接管，绝不自动重提）；重复只读调用检测跨 assistant step 不累计，
/// 真实写入或新 workerCheck 后重置有效进展；工具调用按一次调用聚合，参数分片绝不误计数。
/// 成功结束（turn/end completed）只进入 awaiting-gpt 候选：必须通过 EXECUTION_REPORT.md
/// 完成门禁校验（归属/时效/结构），未通过写 failed 说明原因，绝不显示为可验收完成。
/// 接回同一合同的运行中会话前必须 session.list 核验存在且 running=true；不同 taskId 或
/// 不同合同指纹强制创建新 Session（不做跨合同的项目会话亲和复用）；同项目其他运行合同
/// 返回 busy（判断忙碌不得以复用旧会话实现）。同任务并发启动单飞。
/// 合同执行前复用 EnsureWebHostReadyAsync（Host 未运行时自动启动并等待健康检查），
/// 随后探测中继（提交/事件流/取消三项）并提交；任何一项未确认都诚实降级，绝不虚报成功。
/// 不再使用 dsh run &lt;taskDirectory&gt; 进程路径，不存在会虚报成功的回退。
/// </summary>
public sealed class DeepSeekHarnessRunner
{
    public const string TaskLeaseFileName = ".codex-helper-harness.lock";
    /// <summary>项目级跨进程租约文件名（ex ProjectRoot/.codex-helper/.codex-helper-project.lock）。
    /// 与任务目录级租约不同，它在整个 projectRoot 层级原子占位同项目并发启动，进程崩溃时由
    /// DeleteOnClose 自动删除释放，绝不永久阻塞。</summary>
    public const string ProjectLeaseFileName = ".codex-helper-project.lock";
    private readonly AppPaths paths;
    private readonly string taskRegistry;
    private readonly Dictionary<string, ActiveHarnessTask> live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<HarnessTaskStatus>> inflightStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    private readonly object writeSync = new();

    private sealed record ActiveHarnessTask(CancellationTokenSource Stop, string SessionId);

    /// <summary>Host 基地址（默认 http://127.0.0.1:3080）。</summary>
    public string WebUrl { get; init; } = DeepSeekHarnessVersions.WebHostDefaultUrl;
    /// <summary>RPC 客户端工厂（默认指向 WebUrl；测试可注入指向假 Host）。</summary>
    public Func<HarnessRpcClient>? RpcClientFactory { get; init; }
    /// <summary>中继能力探测（默认 rc.6 真实探测；测试可注入确定实现）。</summary>
    public IDeepSeekHarnessRelay? RelayProbe { get; init; }
    /// <summary>事件流 WebSocket 工厂（默认 ClientWebSocket；测试可注入）。</summary>
    public Func<ClientWebSocket>? EventSocketFactory { get; init; }
    /// <summary>可选 Node 路径；为空时从当前机器的 DSH Node 候选中解析。</summary>
    public string? NodeExecutablePath { get; init; }
    /// <summary>事件流断开后的最大重连次数（默认 3）。</summary>
    public int MaxEventReconnects { get; init; } = 3;
    /// <summary>事件流重连间隔（默认 500 毫秒）。</summary>
    public TimeSpan EventReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>事件流重连耗尽后的 HTTP 增量回退轮询间隔（默认 2 秒；测试可注入缩短）。</summary>
    public TimeSpan HttpPollInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>单次事件流连接在"连续无任何新帧"时允许等待的最大时长（默认 30 秒；测试可注入缩短）。
    /// 长时间无帧不等同于会话无进展，属于载流链路静默；达到时长即放弃本轮连接计入重连次数，
    /// 耗尽后降级到既有 HTTP 终态轮询并写明降级事实，绝不永久卡在“重连其中一步”。</summary>
    public TimeSpan EventFrameTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Host readiness gate（合同提交前执行；默认 EnsureWebHostReadyAsync）。</summary>
    public Func<CancellationToken, Task<HarnessHostReadyResult>>? HostReadyEnsurer { get; init; }

    public DeepSeekHarnessRunner(AppPaths paths)
    {
        this.paths = paths;
        taskRegistry = Path.Combine(paths.BaseDirectory, "harness-tasks");
        paths.EnsureCreated();
        Directory.CreateDirectory(taskRegistry);
    }

    public string TaskDirectoryFor(string taskId) => Path.Combine(taskRegistry, taskId + ".json");

    /// <summary>
    /// 启动一个 Harness 任务。projectRoot 与 taskDirectory 必须为绝对路径，且 taskDirectory 位于
    /// projectRoot 内；任务正文从 taskDirectory/SPEC.md 读取（不读入命令行）。
    /// 同一任务进程内并发启动单飞：并发调用共享同一个飞行中的提交/接回任务，只允许一个 Runner
    /// 创建会话、提交提示或接回会话。
    /// </summary>
    public async Task<HarnessTaskStatus> StartAsync(string projectRoot, string taskDirectory, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        if (!PathSafety.IsWithin(taskDirectory, projectRoot))
            throw new InvalidOperationException("任务目录必须位于项目根目录内。");
        var specPath = Path.Combine(taskDirectory, "SPEC.md");
        if (!File.Exists(specPath))
            throw new FileNotFoundException("任务目录缺少 SPEC.md，任务正文只从文件读取。", specPath);
        // 读取正文仅用于确认存在且可读，绝不出现在任何命令行参数或提示中。
        _ = File.ReadAllText(specPath, System.Text.Encoding.UTF8);

        var taskId = Path.GetFileName(taskDirectory);

        // 单飞：同一任务同时只有一个飞行中的 Start 任务；后到者直接加入，绝不重复提交/接管。
        Task<HarnessTaskStatus> shared;
        lock (sync)
        {
            if (inflightStarts.TryGetValue(taskId, out var existing))
                shared = existing;
            else
            {
                shared = StartCoreAsync(projectRoot, taskDirectory, taskId, cancellationToken);
                inflightStarts[taskId] = shared;
            }
        }
        try
        {
            return await shared;
        }
        finally
        {
            lock (sync)
            {
                if (inflightStarts.TryGetValue(taskId, out var same) && ReferenceEquals(same, shared))
                    inflightStarts.Remove(taskId);
            }
        }
    }

    private async Task<HarnessTaskStatus> StartCoreAsync(string projectRoot, string taskDirectory, string taskId, CancellationToken cancellationToken)
    {
        await using var taskLease = await AcquireTaskLeaseAsync(taskDirectory, cancellationToken);
        var settings = new SettingsService(paths).Load();
        var executionMode = HarnessExecutionOptions.NormalizeMode(settings.HarnessExecutionMode);
        var permissionMode = HarnessExecutionOptions.NormalizePermission(settings.HarnessPermissionMode);
        var executionStrength = HarnessExecutionOptions.NormalizeStrength(settings.HarnessExecutionStrength);
        var agentPreset = HarnessExecutionOptions.AgentPreset(executionMode);
        var presetDegraded = false;
        if (executionMode == HarnessExecutionOptions.DefaultMode && !string.IsNullOrWhiteSpace(settings.HarnessDshEntryPath))
        {
            try
            {
                var profile = new HarnessContractProfileService();
                profile.InstallOrRepair(settings.HarnessDshEntryPath);
                if (profile.IsInstalled) agentPreset = HarnessContractProfileService.PresetId;
            }
            catch
            {
                // 新版 Harness 的 preset 结构不兼容时诚实降级为 standard；合同边界仍由短提示保证。
                // 任务状态记录降级事实，UI 不得虚报 codex-contract 已生效。
                agentPreset = "standard";
                presetDegraded = true;
            }
        }
        var contractFingerprint = ComputeContractFingerprint(taskDirectory);
        // rootCauseKey 必须来自 manifest.json 的显式字段（审计/合并合同组键）；缺失时不猜测、不合并。
        var rootCauseKey = ReadRootCauseKey(taskDirectory);
        // Read the previous state before writing the new starting snapshot. Reading it afterwards
        // always returned the just-written empty SessionId and made session reuse impossible.
        var previous = TryRead(taskId);
        var sameContract = previous is not null
            && string.Equals(previous.ContractFingerprint, contractFingerprint, StringComparison.Ordinal);
        // 会话创建前的持久化取消意图必须被兑现（无论是否同一合同）：停止可能发生在本进程已写
        // starting 快照之后或其他进程正在提交期间；命中就写回可读终态并终止，绝不覆盖取消意图、
        // 绝不创建会话。这同时防止“取消意图被新建 starting 快照覆盖”的竞态。
        if (previous is not null && TryConsumeCancelIntent(previous) is { } cancelledEarly)
        {
            Write(cancelledEarly);
            return cancelledEarly;
        }
        // 同一 taskId 已有 sessionId 的终态记录（历史 completed/awaiting-gpt/failed/cancelled）
        // 或仍处于 starting（可能由本进程正在提交或崩溃遗留）：直接返回已有任务（读取可信终态），
        // 绝不再次 session.create。running 走下方接回核验路径；不同项目/不同合同绝不误复用。
        if (sameContract && previous!.State.ToLowerInvariant() is "completed" or "awaiting-gpt" or "failed" or "cancelled" or "starting")
        {
            return previous;
        }
        // 合同启动前体检与安全归一化（与 Reasonix 同等级）：校验四份合同文件、去重 workerChecks、
        // 把视觉/GUI/release 打包/发布类检查移交 GPT，并派生 WORKER_ACCEPTANCE.md；无法安全归一化时
        // 阻止提交并给出中文原因（写 failed 终态，绝不创建会话/提交提示）。
        var contractHealth = HarnessContractHealth.Inspect(taskDirectory);
        if (contractHealth.Blocked)
        {
            var blocked = new HarnessTaskStatus(taskId, projectRoot, taskDirectory, "failed",
                "合同体检阻止提交：" + contractHealth.BlockReason, DateTime.UtcNow, DateTime.UtcNow, 0, WebUrl,
                RootCauseKey: rootCauseKey, ContractFingerprint: contractFingerprint, SessionState: "failed");
            Write(blocked);
            return blocked;
        }
        HarnessContractHealth.WriteWorkerAcceptance(taskDirectory, contractHealth);
        string? resumeSessionId = null;
        var resumeSameTask = false;
        if (settings.HarnessReuseSession && sameContract
            && string.Equals(previous!.State, "running", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(previous.SessionId))
        {
            resumeSessionId = previous.SessionId;
            resumeSameTask = true;
        }
        var now = DateTime.UtcNow;
        // ProjectSessionId（项目会话亲和）已废弃：历史值仅兼容读取，绝不作为新会话选择依据。
        var status = new HarnessTaskStatus(taskId, projectRoot, taskDirectory, "starting",
            presetDegraded
                ? "codex-contract 预设不可用，已诚实降级为 standard 预设；合同边界由短提示保证。"
                : "正在提交到 Harness Web Host 会话。", now, now, 0, WebUrl,
            SessionId: resumeSessionId,
            RootCauseKey: rootCauseKey,
            ContractFingerprint: contractFingerprint,
            ExecutionMode: executionMode, PermissionMode: permissionMode, ExecutionStrength: executionStrength,
            SessionState: presetDegraded ? "degraded-standard" : resumeSessionId is not null ? "resuming" : "creating");
        // 初始化 PROGRESS.json：写入归一化后的 workerCheck 清单与总额（worker 已写进度时保留，绝不覆盖）。
        HarnessTaskStateStore.EnsureProgress(taskDirectory, taskId, contractHealth.WorkerChecks);
        Write(status);

        // ---- 原子项目互斥：同项目不同任务目录或不同 Helper 进程必须由一个"项目级跨进程租约"原子占位。 ----
        // 绝不依赖"先扫描注册表，再判断 busy"的非原子流程——第一个 starting 即使还没有 sessionId，
        // 只要拿到项目租约即原子占用整个项目；第二个（任意 taskId/进程）拿不到项目租约即判 busy，
        // 绝不创建或提交 Harness 会话。项目租约用 DeleteOnClose 持有，进程崩溃由 OS 自动删除释放；
        // 机械并行例外仅当 manifest 显式 mechanicalParallel:true 且 parallelWriteSets≥2 且互不重叠时才放行。
        using var projectLeaseScope = TryAcquireProjectLease(projectRoot, taskDirectory, taskId);
        if (!projectLeaseScope.Held)
        {
            status = status with
            {
                State = "busy",
                Message = "同一项目已被其他任务原子占用" + (string.IsNullOrWhiteSpace(projectLeaseScope.OccupantDescription) ? "" : "（" + projectLeaseScope.OccupantDescription + "）")
                    + "；未创建新会话、未排队，请等待其结束后再启动。",
                UpdatedUtc = DateTime.UtcNow,
                SessionState = "busy"
            };
            Write(status);
            return status;
        }

        // 统一活动任务记录：本任务已原子占用该项目（启动占位；停止/对账统一引用该记录）。
        HarnessTaskStateStore.WriteActiveRecord(projectRoot, taskId, resumeSessionId, "starting");

        // 合同执行前复用 EnsureWebHostReadyAsync：Host 未运行时自动启动并等待健康检查；
        // 权限模式经受控进程环境变量注入 Helper 启动的 Host（不进入命令行/日志）。
        var readiness = HostReadyEnsurer is not null
            ? await HostReadyEnsurer(cancellationToken)
            : await new DeepSeekHarnessService(paths).EnsureWebHostReadyAsync(cancellationToken: cancellationToken, permissionMode: permissionMode);
        if (!readiness.Ready)
        {
            status = status with { State = "failed", Message = readiness.Message, UpdatedUtc = DateTime.UtcNow, HostProcessId = readiness.ProcessId };
            Write(status);
            return status;
        }
        status = status with { Message = readiness.Message + " 正在探测中继能力。", UpdatedUtc = DateTime.UtcNow, HostProcessId = readiness.ProcessId };
        Write(status);

        // 探测中继：提交/事件流/取消三项全部确认才允许自动提交；任何一项失败都诚实降级。
        var relay = RelayProbe ?? new DeepSeekHarnessRelayProbe(WebUrl);
        HarnessRelayCapability capability;
        try
        {
            capability = await relay.ProbeCapabilitiesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            status = status with { State = "failed", Message = "中继能力探测失败：" + HarnessJson.Truncate(ex.Message, 200), UpdatedUtc = DateTime.UtcNow };
            Write(status);
            return status;
        }
        if (!capability.SubmitSupported)
        {
            status = status with { State = "failed", Message = "中继能力未确认，任务未提交。" + capability.Message, UpdatedUtc = DateTime.UtcNow };
            Write(status);
            return status;
        }
        status = status with { Message = capability.Message + " 正在创建会话。", UpdatedUtc = DateTime.UtcNow };
        Write(status);

        // 会话创建前的第二次取消意图核验：停止可能发生在 readiness/探测期间（本进程或跨进程），
        // 命中则在创建任何会话之前终止，绝不抛给后面再创建。
        if (TryConsumePendingCancel(taskId) is { } cancelledBeforeCreate)
        {
            Write(cancelledBeforeCreate);
            return cancelledBeforeCreate;
        }

        using var rpc = (RpcClientFactory ?? (() => new HarnessRpcClient(WebUrl)))();
        HarnessRpcResult created = HarnessRpcResult.Ok(null);
        if (resumeSessionId is null)
        {
            // 不做跨合同的项目会话亲和复用（旧 affinity 文件不得导致新合同复用旧 Session）：
            // 不同 taskId 或不同合同指纹一律创建新 Session；同项目其他运行合同返回 busy
            // （判断忙碌以扫描其他任务状态 + session.list 核验实现，绝不复用旧会话）。
            // 当前 taskId 的 starting 状态已写入注册表，会覆盖同名旧合同的持久状态；因此必须
            // 先使用内存中保留的 previous 核验“同 taskId、不同指纹”的旧会话，不能只依赖扫描。
            // 再次核验取消意图（stop 可能在前面 RPC/探测期间到达），避免在遗留等待后仍创建会话。
            if (TryConsumePendingCancel(taskId) is { } cancelledBeforeSession)
            {
                Write(cancelledBeforeSession);
                return cancelledBeforeSession;
            }
            if (!sameContract
                && previous is { IsRunning: true }
                && !string.IsNullOrWhiteSpace(previous.SessionId)
                && string.Equals(NormalizeRoot(previous.ProjectRoot), NormalizeRoot(projectRoot), StringComparison.Ordinal)
                && await VerifyResumeSessionAsync(rpc, previous.SessionId, cancellationToken) == ResumeVerifyResult.Running)
            {
                status = status with
                {
                    State = "busy",
                    Message = "同一任务 ID 的旧合同正在运行，未创建新会话；请先等待或停止旧合同。",
                    UpdatedUtc = DateTime.UtcNow,
                    SessionState = "busy"
                };
                Write(status);
                return status;
            }
            // 显式 rootCauseKey 组键接回：相同项目且同组键已有运行任务时，后续合同必须接回
            // 该运行任务而不是新建会话；未声明组键时保持保守隔离（绝不基于自然语言猜测合并）。
            // 组键接回优先于"同项目其他运行合同 busy"：同组键意味着同一根因的碎片化任务应合并执行。
            if (!string.IsNullOrWhiteSpace(rootCauseKey))
                resumeSessionId = await FindGroupResumeSessionIdAsync(rpc, projectRoot, rootCauseKey, taskId, cancellationToken);
            if (resumeSessionId is null)
            {
                var busyMessage = await FindConcurrentProjectBusyAsync(rpc, projectRoot, taskId, contractFingerprint, cancellationToken);
                if (busyMessage is not null)
                {
                    status = status with
                    {
                        State = "busy",
                        Message = busyMessage,
                        UpdatedUtc = DateTime.UtcNow,
                        SessionState = "busy"
                    };
                    Write(status);
                    return status;
                }
            }
        }
        // 统一接回核验：resumeSessionId 非空表示接回运行中会话（同 taskId+同合同指纹，或同项目+同显式组键）。
        if (resumeSessionId is not null)
        {
            var resumeTarget = resumeSessionId;
            // 接回前必须 session.list 核验会话状态：running → 续接监听；已结束 → 读取可信终态
            // （session.history 的最后一个 turn/end），绝不接回已死会话；不存在/无法核验 → 诚实失败，
            // 绝不再次 session.create 或重复提交提示。
            switch (await VerifyResumeSessionAsync(rpc, resumeTarget, cancellationToken))
            {
                case ResumeVerifyResult.Running:
                    created = HarnessRpcResult.Ok(new System.Text.Json.Nodes.JsonObject { ["sessionId"] = resumeTarget });
                    break;
                case ResumeVerifyResult.Ended:
                    var endedTerminal = await ReadEndedSessionTerminalAsync(rpc, resumeTarget, cancellationToken);
                    if (endedTerminal is null)
                    {
                        status = status with
                        {
                            State = "failed",
                            Message = "接回前核验发现原 Harness 会话已结束，但缺少可信终态（session.history 缺失、损坏或没有 turn/end）；未接回、未重复提交。",
                            UpdatedUtc = DateTime.UtcNow
                        };
                        Write(status);
                        return status;
                    }
                    // completed 只进入 awaiting-gpt 候选：接回读取的终态同样必须通过报告完成门禁。
                    endedTerminal = GateReport(endedTerminal, status);
                    status = status with { State = endedTerminal.State, Message = endedTerminal.Message, UpdatedUtc = DateTime.UtcNow, SessionState = endedTerminal.State };
                    Write(status);
                    return status;
                default:
                    status = status with
                    {
                        State = "failed",
                        Message = "接回前核验失败：原 Harness 会话已结束或不存在（或 Host 无法核验），未接回、未重复提交。",
                        UpdatedUtc = DateTime.UtcNow
                    };
                    Write(status);
                    return status;
            }
        }
        else
        {
            created = await CreateSessionGuardedAsync(rpc, projectRoot, agentPreset, cancellationToken);
        }
        if (!created.Success)
        {
            status = status with { State = "failed", Message = "创建 Harness 会话失败：" + created.ErrorMessage, UpdatedUtc = DateTime.UtcNow };
            Write(status);
            return status;
        }
        var sessionId = created.GetString("sessionId");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            status = status with { State = "failed", Message = "创建 Harness 会话失败：响应缺少 sessionId。", UpdatedUtc = DateTime.UtcNow };
            Write(status);
            return status;
        }

        // 不做项目会话亲和：新会话只属于当前任务，跨合同不复用。

        // 统一活动任务记录：会话已创建（启动/接回占位带真实 sessionId）。
        HarnessTaskStateStore.WriteActiveRecord(projectRoot, taskId, sessionId, "starting");

        status = status with { State = "starting", Message = "会话已创建，正在提交合同提示。", SessionId = sessionId, UpdatedUtc = DateTime.UtcNow, SessionState = "starting" };
        Write(status);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (sync) live[taskId] = new ActiveHarnessTask(stop, sessionId);
        try
        {
            // DSH 官方 Web 客户端在提交 prompt 前已建立 mux/host generation。此前 Runner
            // 反过来先提交再监听，短任务或首批事件很快时会错过会话绑定，随后错误降级 HTTP。
            // 创建 session 后先等待当前 session 的订阅帧；超时只代表未能预连接，不阻止同一会话
            // 提交，也绝不创建第二个会话。listener 全程只属于这一个 session。
            Task<TerminalState>? listener = null;
            var eventPreconnected = false;
            if (resumeSessionId is null)
            {
                var streamReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                listener = Task.Run(() => ListenForTerminalAsync(rpc, sessionId, status, stop.Token, streamReady), CancellationToken.None);
                var preconnect = await Task.WhenAny(streamReady.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
                if (ReferenceEquals(preconnect, streamReady.Task) && streamReady.Task.IsCompletedSuccessfully)
                {
                    eventPreconnected = true;
                    status = status with
                    {
                        State = "starting",
                        Message = "实时事件通道已预连接，正在提交合同提示。",
                        UpdatedUtc = DateTime.UtcNow,
                        SessionState = "starting"
                    };
                    Write(status);
                }
            }

            HarnessRpcResult prompt = HarnessRpcResult.Ok(null);
            if (resumeSessionId is null)
            {
                try
                {
                    prompt = await rpc.PromptAsync(sessionId, BuildPrompt(projectRoot, taskDirectory, taskId, contractFingerprint), "Asia/Shanghai", cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    prompt = HarnessRpcResult.Fail("RPC 异常：" + HarnessJson.Truncate(ex.Message, 200));
                }
                if (!prompt.Success)
                {
                    stop.Cancel();
                    await TryCancelSessionAsync(rpc, sessionId);
                    status = status with { State = "failed", Message = "提交合同提示失败：" + prompt.ErrorMessage, UpdatedUtc = DateTime.UtcNow };
                    Write(status);
                    return status;
                }
            }
            status = status with
            {
                State = resumeSessionId is not null || eventPreconnected ? "running" : "starting",
                Message = resumeSessionId is not null
                    ? (resumeSameTask ? "已接回同一合同的运行中会话，未重复提交。" : "已接回同组键（rootCauseKey）的运行中会话，未重复提交。")
                    : eventPreconnected ? "任务提示已提交，实时事件通道正在跟踪该会话。" : "任务提示已提交，等待会话启动证据。",
                SessionState = resumeSessionId is not null || eventPreconnected ? "running" : "starting",
                UpdatedUtc = DateTime.UtcNow
            };
            Write(status);

            // 新会话的 listener 已在 prompt 前预连接，避免错过首批会话事件；接回会话则在
            // “已接回”状态写入后开始监听，避免后续状态覆盖实时事件。
            listener ??= Task.Run(() => ListenForTerminalAsync(rpc, sessionId, status, stop.Token), CancellationToken.None);

            TerminalState terminal;
            try
            {
                terminal = await listener;
            }
            catch (OperationCanceledException)
            {
                // 用户停止：accepted 不等于已停止，必须以 session.list running=false 为准。
                var stopped = await TryCancelSessionAsync(rpc, sessionId);
                status = stopped
                    ? status with { State = "cancelled", Message = "用户已停止任务，Harness 会话已确认结束。", UpdatedUtc = DateTime.UtcNow, SessionState = "cancelled" }
                    : status with { State = "failed", Message = "Harness 已接受取消请求，但会话仍在运行；未虚报已停止，需要再次取消或在 Harness Web 中停止。", UpdatedUtc = DateTime.UtcNow, SessionState = "cancel-unconfirmed" };
                Write(status);
                return status;
            }

            // 监听器在独立任务中持续写入真实事件传输信息。收尾时不能用提交阶段的
            // `status` 覆盖它，否则任务已走 Node Relay，最终状态却显示为空，任务中心
            // 无法区分实时通道和 HTTP 回退。
            var observed = TryRead(taskId);
            status = status with
            {
                State = terminal.State,
                Message = terminal.Message,
                UpdatedUtc = DateTime.UtcNow,
                SessionState = terminal.State,
                StateSource = observed?.StateSource ?? status.StateSource,
                EventTransport = observed?.EventTransport ?? status.EventTransport,
                EventTransportError = observed?.EventTransportError ?? status.EventTransportError
            };
            status = await EnrichFromSessionAsync(rpc, status, cancellationToken);
            if (terminal.Summary is { } summary)
                status = status with
                {
                    Stage = summary.Stage ?? status.Stage,
                    ToolCallCount = summary.ToolCallCount,
                    ReasoningEventCount = summary.ReasoningEventCount,
                    NoProgressWarnings = summary.NoProgressWarnings
                };
            Write(status);
            WriteReviewPacket(status);
            return status;
        }
        finally
        {
            lock (sync) live.Remove(taskId);
        }

        // 创建会话的守卫局部函数：网络/协议异常转换为可读失败，不抛出（取消除外）。
        async Task<HarnessRpcResult> CreateSessionGuardedAsync(HarnessRpcClient rpc, string cwd, string preset, CancellationToken ct)
        {
            try { return await rpc.CreateSessionAsync(cwd, preset, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return HarnessRpcResult.Fail("RPC 异常：" + HarnessJson.Truncate(ex.Message, 200)); }
        }
    }

    private static async Task<FileStream> AcquireTaskLeaseAsync(string taskDirectory, CancellationToken cancellationToken)
    {
        var leasePath = Path.Combine(taskDirectory, TaskLeaseFileName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }
    }

    /// <summary>
    /// 是否正有提交进程持有该任务的本地租约（`.codex-helper-harness.lock` 存在即视为被持有，
    /// 因为 <see cref="AcquireTaskLeaseAsync"/> 以 DeleteOnClose 且共享排它方式持有；
    /// 文件缺失意味着没有任何进程正在提交，即该 starting 状态是孤儿占位）。
    /// 用于：跨任务目录单飞时只把“真正正在提交”的 starting 视为项目占位；
    /// 对账时把“无租约的 starting 孤儿”诚实清扫，避免永久 busy 或重复创建。
    /// </summary>
    private static bool IsTaskLeaseHeld(string taskDirectory)
        => File.Exists(Path.Combine(taskDirectory, TaskLeaseFileName));

    /// <summary>
    /// 项目级租约结果：Held=true 表示调用方可继续（已持有租约，或机械并行例外放行）；
    /// Held=false 表示项目已被其他任务原子占用（OccupantDescription 为可读 busy 说明）。
    /// 持有期覆盖整个 StartCoreAsync，借 Dispose 释放底层文件流（DeleteOnClose 崩溃自动删除）。
    /// </summary>
    private sealed record ProjectLeaseResult(bool Held, string OccupantDescription, IDisposable? Lease) : IDisposable
    {
        public void Dispose() => Lease?.Dispose();
    }

    /// <summary>
    /// 原子获取项目级跨进程租约（projectRoot/.codex-helper/.codex-helper-project.lock）。
    /// 以 OpenOrCreate + FileShare.None-ish（Read 共享仅供诊断读取）+ DeleteOnClose 独占文件流持有：
    /// <list type="bullet">
    /// <item>获取成功 → 写入占位方戳（taskId/projectRoot/进程/状态），返回 Held=true；</item>
    /// <item>文件已被别进程独占（IOException）→ 项目占用，读取戳生成可读 busy 说明，返回 Held=false；</item>
    /// <item>manifest 显式机械并行（mechanicalParallel:true 且 parallelWriteSets≥2 互不重叠）→ 放行绕过分互斥。</item>
    /// </list>
    /// 不轮询等待：占用即判 busy，绝不永久阻塞（与"先扫描注册表再判断"的非原子流程不同，这里是原子互斥）。
    /// </summary>
    private static ProjectLeaseResult TryAcquireProjectLease(string projectRoot, string taskDirectory, string taskId)
    {
        // 机械并行例外：仅 manifest 显式声明并行写集互不重叠才放行；其余一律默认单飞。
        if (ReadMechanicalParallelAllow(taskDirectory))
            return new ProjectLeaseResult(true, "机械并行例外：跳过项目级互斥", null);

        var leaseDir = Path.Combine(projectRoot, ".codex-helper");
        var lockPath = Path.Combine(leaseDir, ProjectLeaseFileName);
        try { Directory.CreateDirectory(leaseDir); } catch { /* 项目根不可写时以任务级租约单飞为准 */ }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            stream = null;
        }
        if (stream is not null)
        {
            TryWriteProjectLeaseStamp(stream, projectRoot, taskId);
            return new ProjectLeaseResult(true, "当前任务已原子占用该项目", stream);
        }
        // 项目已被其他任务/进程占据。Windows 的 FileShare 规则会阻止第二个句柄读取
        // 正在以 ReadWrite 持有的锁文件；不能为了展示 taskId 放宽独占锁，因此读不到时
        // 使用通用 busy 文案，原子互斥仍是唯一事实来源。
        return new ProjectLeaseResult(false, TryReadProjectLeaseOccupant(lockPath), null);
    }

    /// <summary>向已占用的项目租约文件写入占位方戳（仅非敏感诊断信息，绝不写入正文/凭据）。</summary>
    private static void TryWriteProjectLeaseStamp(FileStream stream, string projectRoot, string taskId)
    {
        try
        {
            var content = new System.Text.Json.Nodes.JsonObject
            {
                ["taskId"] = taskId,
                ["projectRoot"] = projectRoot,
                ["hostProcessId"] = Environment.ProcessId,
                ["state"] = "starting",
                ["atUtc"] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            };
            var bytes = System.Text.Encoding.UTF8.GetBytes(content.ToJsonString());
            stream.SetLength(0);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch { /* 戳写入失败不影响互斥成立 */ }
    }

    /// <summary>从项目租约文件读取占位方描述（供并发进程判 busy）；缺失/损坏返回空串。</summary>
    private static string TryReadProjectLeaseOccupant(string lockPath)
    {
        try
        {
            // 仅作为最佳努力诊断：某些 Windows 共享组合下打开会失败，调用方必须回退通用 busy，
            // 绝不因此放宽项目级原子锁。
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(text);
            var taskId = doc.RootElement.TryGetProperty("taskId", out var id) ? id.GetString() : null;
            var state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() : null;
            return string.IsNullOrWhiteSpace(taskId)
                ? string.Empty
                : $"任务「{taskId}」正在{state ?? "启动"}";
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// 读取任务目录 manifest.json 的机械并行声明：仅当 mechanicalParallel 严格为 true、且
    /// parallelWriteSets 为至少两个"非空且互不重叠"的字符串集合时才放行（允许同项目多任务目录
    /// 并行写不同文件集合）；缺失/损坏/重叠一律回到默认单飞（项目级原子互斥）。
    /// </summary>
    private static bool ReadMechanicalParallelAllow(string taskDirectory)
    {
        var path = Path.Combine(taskDirectory, "manifest.json");
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
            var root = doc.RootElement;
            if (!root.TryGetProperty("mechanicalParallel", out var mp) || mp.ValueKind != JsonValueKind.True)
                return false;
            if (!root.TryGetProperty("parallelWriteSets", out var sets) || sets.ValueKind != JsonValueKind.Array)
                return false;
            var collected = new List<System.Collections.Generic.HashSet<string>>();
            foreach (var set in sets.EnumerateArray())
            {
                if (set.ValueKind != JsonValueKind.Array) return false;
                var hs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in set.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) hs.Add(item.GetString() ?? string.Empty);
                if (hs.Count == 0) return false;
                foreach (var existing in collected)
                    if (existing.Overlaps(hs)) return false; // 存在重叠 → 不互不重叠，拒绝放行
                collected.Add(hs);
            }
            return collected.Count >= 2;
        }
        catch
        {
            return false;
        }
    }

    private sealed record TerminalState(string State, string Message, HarnessProgressSummary? Summary = null);

    /// <summary>接回前核验结果：会话运行中 / 已结束 / 不存在 / Host 无法核验。</summary>
    private enum ResumeVerifyResult { Running, Ended, NotFound, Unverifiable }

    /// <summary>
    /// 接回前核验：session.list 中该会话 running=true → Running（可续接监听）；
    /// running=false → Ended（由调用方读取可信终态，绝不接回已死会话）；
    /// 不存在 → NotFound；Host 响应不可信（RPC 失败）→ Unverifiable。
    /// </summary>
    private static async Task<ResumeVerifyResult> VerifyResumeSessionAsync(HarnessRpcClient rpc, string sessionId, CancellationToken cancellationToken)
    {
        HarnessRpcResult list;
        try
        {
            list = await rpc.ListSessionsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ResumeVerifyResult.Unverifiable;
        }
        if (!list.Success) return ResumeVerifyResult.Unverifiable;
        var item = list.Value?["items"]?.AsArray()
            .FirstOrDefault(node => string.Equals(HarnessJson.Text(node?["sessionId"]), sessionId, StringComparison.Ordinal));
        if (item is null) return ResumeVerifyResult.NotFound;
        return item["running"] is System.Text.Json.Nodes.JsonValue runningValue
            && runningValue.TryGetValue<bool>(out var running) && running
            ? ResumeVerifyResult.Running
            : ResumeVerifyResult.Ended;
    }

    /// <summary>
    /// 监听事件流直到会话终态（turn/end）。运行中事件（turn/start）实时写状态；
    /// 事件流断开时有限重连（不提前标 completed）；重连耗尽后诚实失败。
    /// 事件按 seq 去重：同一连接内的重复帧与重连后的重放帧（旧序号）都不再推进状态或重复计数。
    /// 每次 turn/start 重置 step 级退化检测窗口；同一步内检测退化（显著长度文本片段连续重复 /
    /// 相同工具名+完整规范化参数连续重复，工具调用按一次调用聚合），
    /// 达到阈值立即 session.cancel 并写 failed（绝不自动重提合同或创建新会话）。
    /// turn/end completed 只进入 awaiting-gpt 候选，必须通过 EXECUTION_REPORT.md 完成门禁。
    /// 只记录状态与类型，绝不记录事件正文/密钥等敏感内容。
    /// </summary>
    private async Task<TerminalState> ListenForTerminalAsync(HarnessRpcClient rpc, string sessionId, HarnessTaskStatus initial,
        CancellationToken cancellationToken, TaskCompletionSource<bool>? streamReady = null)
    {
        var current = initial;
        var attempts = 0;
        var lastSeq = -1L;
        var detector = new HarnessDegenerationDetector();
        while (true)
        {
            try
            {
                // 每次事件流等待都必须有可取消的"无帧上限"：单次连接在 EventFrameTimeout 内没有任何
                // 新帧（载流但静默 / 连接假活）即视为本连接卡死，主动放弃本轮计入重连次数；耗尽后降级
                // 到既有 HTTP 终态轮询并写明降级事实，绝不永久卡在"重连其中一步"。用户取消（外部
                // CancellationToken）仍由 frameCts 链式传播并原样抛出，由调用方优先 session.cancel 核验。
                // ListenAsync 在创建枚举器时捕获 CancellationToken；因此不能只给
                // MoveNextAsync 外包一层短期 token。每次连接使用自己的 linked CTS，
                // 无帧超时时主动取消该连接，确保底层 WebSocket ReceiveAsync 真正退出。
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var nodePath = ResolveNodeExecutablePath();
                var relayPath = ResolveNodeRelayScriptPath();
                // Node relay 已按 session 精确过滤，并和 DSH 官方客户端使用同一 WebSocket 实现。
                // 不能再给它套“若干秒无帧就断开”的猜测性 watchdog：模型思考/工具运行期间
                // 合法静默可远长于该窗口，旧逻辑正是因此把健康会话错误降级为 HTTP。
                // 注入 WebSocket 工厂代表受控测试/兼容宿主：必须保留 C# 流以验证其回退语义。
                // 真正 DSH 运行时没有注入工厂，才选用 Node 官方通道。
                var nodeRelayActive = EventSocketFactory is null
                    && Uri.TryCreate(WebUrl, UriKind.Absolute, out var harnessUri) && harnessUri.Port == 3080
                    && !string.IsNullOrWhiteSpace(nodePath) && !string.IsNullOrWhiteSpace(relayPath);
                current = current with
                {
                    StateSource = nodeRelayActive ? "node-relay" : "dotnet-websocket",
                    EventTransport = nodeRelayActive ? "node-relay" : "dotnet-websocket"
                };
                using var stream = new DeepSeekHarnessEventStream(WebUrl)
                {
                    WebSocketFactory = EventSocketFactory,
                    NodeExecutablePath = nodeRelayActive ? nodePath : null,
                    NodeRelayScriptPath = nodeRelayActive ? relayPath : null,
                    SessionIdFilter = sessionId,
                    // 与 rc.6 官方客户端保持同一就绪协议：双 WS 均已打开后再验证 Host。
                    // 不使用启动前那次探测替代这里的检查，因为 Web Host 可在两次之间重启。
                    ReadyCheckAsync = async ct =>
                    {
                        var described = await rpc.CallAsync("host.describe", new System.Text.Json.Nodes.JsonObject(), ct);
                        if (!described.Success)
                            throw new InvalidOperationException("host.describe 未通过：" + described.ErrorMessage);
                    }
                };
                var enumerator = stream.ListenAsync(attemptCts.Token).GetAsyncEnumerator();
                try
                {
                    var stalled = false;
                    // Web Host 会广播其他会话的事件。只有当前合同会话的帧才表示
                    // 本连接对本任务仍有进展；无关帧不能无限推迟无帧 watchdog。
                    var lastRelevantFrameUtc = DateTime.UtcNow;
                    while (true)
                    {
                        var remaining = nodeRelayActive
                            ? Timeout.InfiniteTimeSpan
                            : EventFrameTimeout - (DateTime.UtcNow - lastRelevantFrameUtc);
                        if (!nodeRelayActive && remaining <= TimeSpan.Zero)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            attemptCts.Cancel();
                            stalled = true;
                            break;
                        }
                        var moveTask = enumerator.MoveNextAsync().AsTask();
                        var timeoutTask = Task.Delay(remaining, cancellationToken);
                        var winner = await Task.WhenAny(moveTask, timeoutTask);
                        if (!ReferenceEquals(winner, moveTask))
                        {
                            // 外部取消必须原样上抛；只有自身的无帧 watchdog 才按断流处理。
                            cancellationToken.ThrowIfCancellationRequested();
                            attemptCts.Cancel();
                            // 已由 DeepSeekHarnessEventStream 的取消注册主动 Abort 底层
                            // socket。部分 Host 的 ReceiveAsync 在 Abort 后仍不及时结束；
                            // 这里绝不能继续 await 旧 MoveNextAsync，否则 watchdog 名存实亡。
                            _ = moveTask.ContinueWith(static task => _ = task.Exception,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                            stalled = true;
                            break;
                        }
                        var moved = await moveTask;
                        if (!moved) break; // 服务端关闭连接。
                        var frame = enumerator.Current;
                        if (frame.Type == "stream/error")
                        {
                            current = WithSummary(current with { Message = "事件流错误：" + HarnessJson.Truncate(frame.ErrorMessage, 200), UpdatedUtc = DateTime.UtcNow }, detector);
                            Write(current);
                            continue;
                        }
                        if (frame.Type == "relay/ready")
                        {
                            current = WithSummary(current with
                            {
                                State = "running",
                                Message = "Node 实时事件中继已连接，正在等待当前会话事件。",
                                UpdatedUtc = DateTime.UtcNow,
                                SessionState = "starting"
                            }, detector);
                            Write(current);
                            continue;
                        }
                        if (!string.Equals(frame.SessionId, sessionId, StringComparison.Ordinal)) continue;
                        lastRelevantFrameUtc = DateTime.UtcNow;
                        // 订阅帧是正式连接到当前 session 的唯一可靠证据。任何属于该 session
                        // 的后续事件也足以解除提交前预连接等待，兼容未来版本省略 subscribed 的情况。
                        streamReady?.TrySetResult(true);
                        if (string.Equals(frame.Type, "session/subscribed", StringComparison.Ordinal)
                            && frame.SubscriptionLastSeq is long baseline)
                        {
                            // rc.6 订阅帧给出当前会话的事件基线。后续只处理更大的 seq，
                            // 避免重连时的旧帧重放污染退化计数与进度状态。
                            lastSeq = Math.Max(lastSeq, baseline);
                            current = WithSummary(current with
                            {
                                State = "running",
                                Message = "Node 实时事件通道已就绪，正在接收 Harness 会话事件。",
                                UpdatedUtc = DateTime.UtcNow,
                                SessionState = "running"
                            }, detector);
                            Write(current);
                            continue;
                        }
                        // 按事件 seq 去重：旧序号（同连接重复帧或重连重放帧）直接丢弃，不再推进状态/重复计数。
                        if (frame.Seq is long seq)
                        {
                            if (seq <= lastSeq) continue;
                            lastSeq = seq;
                        }
                        if (frame.EventType == "turn/start")
                        {
                            // 每次 turn/start 必须重置 step 级退化检测窗口（跨 step 不累计；
                            // WebSocket 与 HTTP 轮询行为一致）。
                            detector.ResetStep();
                            detector.EnterStage("start");
                            current = WithSummary(current with { State = "running", Message = "会话正在运行。", UpdatedUtc = DateTime.UtcNow }, detector);
                            Write(current);
                        }
                        else if (frame.EventType == "turn/end")
                        {
                            // 提交进行中的工具调用后再判退化，避免漏计最后一次调用。
                            detector.CommitToolCall();
                            detector.EnterStage("report");
                            if (detector.IsDegenerate)
                            {
                                await TryCancelSessionAsync(rpc, sessionId);
                                var message = $"检测到重复生成/重复工具调用，已停止以防继续覆盖，等待 GPT 接管（{detector.Reason}）。";
                                current = WithSummary(current with { State = "failed", Message = message, UpdatedUtc = DateTime.UtcNow }, detector);
                                Write(current);
                                return WithSummary(GateReport(new TerminalState("failed", message), current), detector);
                            }
                            // completed 只进入 awaiting-gpt 候选：必须通过 EXECUTION_REPORT.md 完成门禁。
                            return WithSummary(GateReport(MapTurnEnd(frame.TurnEndKind), current), detector);
                        }
                        else if (frame.AssistantChunkKind is not null)
                        {
                            // 由工具名统一推断阶段（WS 与 HTTP 轮询共用同一映射），阶段转换即进展。
                            var stage = HarnessDegenerationDetector.MapToolStage(frame.ToolName);
                            var stageChanged = stage is not null && detector.EnterStage(stage);
                            if (frame.TextDelta is not null) detector.ObserveText(frame.TextDelta);
                            // 带 toolName 开始新调用并提交上一次；纯参数增量只追加不单独计次（分片不误计数）。
                            detector.ObserveToolCall(frame.ToolName, frame.ToolArgsDelta);
                            if (detector.IsDegenerate)
                            {
                                // 达到退化阈值：立即取消会话并写 failed；绝不自动重提合同或创建新会话。
                                await TryCancelSessionAsync(rpc, sessionId);
                                var message = $"检测到重复生成/重复工具调用，已停止以防继续覆盖，等待 GPT 接管（{detector.Reason}）。";
                                current = WithSummary(current with { State = "failed", Message = message, UpdatedUtc = DateTime.UtcNow }, detector);
                                Write(current);
                                return WithSummary(new TerminalState("failed", message), detector);
                            }
                            // 真实工具调用且发生阶段切换：这是"真实工具"进度，写入状态与 PROGRESS.json；
                            // 同一阶段内的重复增量不重复写盘（只在真实阶段/工具变化时投影）。
                            if (stageChanged)
                            {
                                current = WithSummary(current with { Message = "检测到真实工具调用（" + ShortToolName(frame.ToolName) + "）。", UpdatedUtc = DateTime.UtcNow }, detector);
                                Write(current);
                            }
                        }
                    }

                    // 事件流被服务端关闭，或本连接在无帧窗口内卡死：有限重连，绝不提前标 completed。
                    // 耗尽后转 HTTP 增量轮询（沿用同一退化检测器与 seq）。
                    if (++attempts > MaxEventReconnects)
                        return await PollSessionTerminalAsync(rpc, sessionId, current, detector, lastSeq, cancellationToken);
                    var reconnectReason = stalled ? "事件流长时间无新帧" : "事件流已断开";
                    current = WithSummary(current with
                    {
                        State = "running",
                        Message = $"{reconnectReason}，正在重连（{attempts}/{MaxEventReconnects}）…",
                        EventTransportError = nodeRelayActive ? "Node Relay 在收到当前会话终态前结束。" : "原生 WebSocket 连接已结束。",
                        UpdatedUtc = DateTime.UtcNow
                    }, detector);
                    Write(current);
                    await Task.Delay(EventReconnectDelay, cancellationToken);
                }
                finally
                {
                    // 同理，底层 WebSocket 在少数 Host 实现里可能让 DisposeAsync
                    // 长时间挂起。连接已被取消/Abort，清理只允许短暂等待，不能阻止
                    // 上层转入 HTTP 增量回退。
                    var disposeTask = enumerator.DisposeAsync().AsTask();
                    await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(1)));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 只有调用方（用户停止 / Codex 退出）发起的取消才真正结束合同。
                // attemptCts 的取消是本轮无帧 watchdog 的内部控制信号，必须继续走
                // 同一 session 的 HTTP 增量回退，绝不能被误判成用户取消。
                throw;
            }
            catch (OperationCanceledException)
            {
                current = WithSummary(current with
                {
                    State = "running",
                    Message = "事件流本轮无响应，已切换 HTTP 状态通道。",
                    EventTransportError = "事件流等待被内部取消。",
                    UpdatedUtc = DateTime.UtcNow
                }, detector);
                Write(current);
                return await PollSessionTerminalAsync(rpc, sessionId, current, detector, lastSeq, cancellationToken);
            }
            catch (Exception ex)
            {
                // 连接被 Host 主动关闭、网络短断或 generation 切换都会以异常形式从
                // EventStream 冒出。它们与枚举自然结束一样应先按有限次数重连，
                // 否则 rc.6 的重放帧永远没有机会进入 seq 去重逻辑；仅重连耗尽后
                // 才回退到同一 session 的 HTTP 增量查询，绝不重新提交合同。
                if (++attempts <= MaxEventReconnects)
                {
                    current = WithSummary(current with
                    {
                        State = "running",
                        Message = "实时事件流连接失败（" + HarnessJson.Truncate(ex.Message, 180) + $"），正在重连（{attempts}/{MaxEventReconnects}）…",
                        EventTransportError = HarnessJson.Truncate(ex.Message, 180),
                        UpdatedUtc = DateTime.UtcNow
                    }, detector);
                    Write(current);
                    await Task.Delay(EventReconnectDelay, cancellationToken);
                    continue;
                }
                current = WithSummary(current with
                {
                    State = "running",
                    Message = "实时事件流重连已耗尽（" + HarnessJson.Truncate(ex.Message, 180) + "），已切换 HTTP 状态通道。",
                    EventTransportError = HarnessJson.Truncate(ex.Message, 180),
                    UpdatedUtc = DateTime.UtcNow
                }, detector);
                Write(current);
                return await PollSessionTerminalAsync(rpc, sessionId, current, detector, lastSeq, cancellationToken);
            }
        }
    }

    private string? ResolveNodeExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(NodeExecutablePath) && File.Exists(NodeExecutablePath)) return NodeExecutablePath;
        return new DeepSeekHarnessDiscovery().Discover(null)
            .FirstOrDefault(candidate => DeepSeekHarnessProbe.IsNodeVersionSupported(ReadNodeVersion(candidate.Path)))?.Path;
    }

    private static string? ResolveNodeRelayScriptPath()
    {
        // Core.dll 可能被宿主（PowerShell、测试进程、Runner）加载；AppContext.BaseDirectory
        // 并不总是 HarnessRunner 的发布目录。优先从入口可执行文件定位随 Runner 发布的资产。
        // Core.dll 与 HarnessRunner.exe 一同发布到 runner 目录；用自身程序集位置比宿主
        // 进程/入口程序集可靠（framework-dependent apphost、测试宿主都会改变后二者）。
        var assemblyDirectory = Path.GetDirectoryName(typeof(DeepSeekHarnessRunner).Assembly.Location);
        var entryDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
        var baseDirectory = !string.IsNullOrWhiteSpace(assemblyDirectory) ? assemblyDirectory
            : (!string.IsNullOrWhiteSpace(entryDirectory) ? entryDirectory : AppContext.BaseDirectory);
        var candidate = Path.Combine(baseDirectory, "Assets", "harness-event-relay.mjs");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string ReadNodeVersion(string nodePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(nodePath, "--version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            });
            if (process is null) return string.Empty;
            var value = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return value.Trim();
        }
        catch { return string.Empty; }
    }

    private static TerminalState MapTurnEnd(string? kind) => kind switch
    {
        // 成功结束统一为 awaiting-gpt 候选：Harness 会话已完成，但必须通过 EXECUTION_REPORT.md
        // 完成门禁校验（GateReport）后才等待 GPT 独立验收，绝不直接显示为可验收完成。
        "completed" => new TerminalState("awaiting-gpt", "任务已在 Harness 会话中完成，等待 GPT 独立验收。"),
        "aborted" => new TerminalState("cancelled", "会话已取消。"),
        _ => new TerminalState("failed", kind is null
            ? "会话结束但缺少结束原因，视为失败。"
            : $"会话以非成功原因结束（{kind}），视为失败。")
    };

    /// <summary>
    /// 完成门禁：turn/end completed 只能进入 awaiting-gpt 候选；必须校验当前任务的
    /// EXECUTION_REPORT.md（晚于任务开始、taskId 与合同指纹匹配、结构化字段齐全）。
    /// 缺失、陈旧、合同不匹配或结构不完整的报告 → failed 并说明原因（等待 GPT 接管）。
    /// </summary>
    private static TerminalState GateReport(TerminalState terminal, HarnessTaskStatus status)
    {
        if (!string.Equals(terminal.State, "awaiting-gpt", StringComparison.Ordinal)) return terminal;
        var validation = HarnessExecutionReportValidator.Validate(
            status.TaskDirectory, status.TaskId, status.ContractFingerprint, status.StartedUtc);
        return validation.Valid
            ? terminal
            : new TerminalState("failed",
                "任务已在 Harness 会话中完成，但 EXECUTION_REPORT.md 未通过完成门禁（" + validation.Reason + "），等待 GPT 接管。");
    }

    /// <summary>
    /// 同项目并发 busy 判定（不做跨合同会话复用）：扫描本 Runner 任务注册表中同一规范化
    /// 项目根下的其他活动任务（running/starting 且带 SessionId；排除同 taskId 且同合同指纹
    /// 的自身），若其会话经 session.list 核验仍 running=true → 返回 busy 消息（避免并发覆盖）；
    /// 已结束/不存在/Host 无法核验 → 返回 null（继续创建新会话，绝不复用旧会话）。
    /// </summary>
    private async Task<string?> FindConcurrentProjectBusyAsync(HarnessRpcClient rpc, string projectRoot,
        string taskId, string contractFingerprint, CancellationToken cancellationToken)
    {
        var normalized = NormalizeRoot(projectRoot);
        var candidates = GetRecentTasks(200)
            .Where(task => task.IsRunning
                && !(string.Equals(task.TaskId, taskId, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(task.ContractFingerprint, contractFingerprint, StringComparison.Ordinal))
                && string.Equals(NormalizeRoot(task.ProjectRoot), normalized, StringComparison.Ordinal)
                // 同项目其他任务目录的“正在提交但尚无 sessionId”的 starting 任务同样占位整个项目
                // （跨任务目录单飞）：只要它仍持有本地租约（真实提交进行中），第二份合同就不得创建会话。
                // 带真实会话的运行中任务必然占位；无租约（孤儿）的 starting 不占位，避免永久 busy 或误判阻塞。
                && (task.IsRunning && (!string.IsNullOrWhiteSpace(task.SessionId)
                        || IsTaskLeaseHeld(task.TaskDirectory))))
            .ToList();
        if (candidates.Count == 0) return null;
        var list = await rpc.ListSessionsAsync(cancellationToken);
        if (!list.Success) return null; // Host 无法核验：不虚报 busy，创建失败会诚实报错。
        foreach (var task in candidates)
        {
            // 尚无 sessionId 的 starting 占位（跨任务目录单飞）：本地租约仍被持有即真实提交进行中，
            // 无需（也无法在 Host 中）核验会话，直接视为项目占用返回 busy。
            if (string.IsNullOrWhiteSpace(task.SessionId))
                return $"同一项目已有任务「{task.TaskId}」正在启动（{task.State}），尚未创建会话；未创建新会话、未排队，请等待其创建会话或结束。";
            var item = list.Value?["items"]?.AsArray()
                .FirstOrDefault(node => string.Equals(HarnessJson.Text(node?["sessionId"]), task.SessionId, StringComparison.Ordinal));
            var running = item is not null
                && item["running"] is System.Text.Json.Nodes.JsonValue runningValue
                && runningValue.TryGetValue<bool>(out var isRunning) && isRunning;
            if (running)
                return $"同一项目已有任务「{task.TaskId}」的 Harness 会话正在运行（{task.State}），未创建新会话、未排队，请等待其结束后再启动。";
        }
        return null;
    }

    /// <summary>
    /// 显式 rootCauseKey 组键接回：扫描本 Runner 任务注册表中同一规范化项目根下、相同显式
    /// rootCauseKey 且正在运行的其他任务（排除当前 taskId），若其会话经 session.list 核验仍
    /// running=true → 返回该会话 ID（后续合同接回该运行任务而非新建会话）；已结束/不存在/Host
    /// 无法核验 → 返回 null（走保守隔离：busy 或新建会话）。未声明组键的任务绝不进入此路径，
    /// 也绝不基于自然语言猜测合并不同合同。
    /// </summary>
    private async Task<string?> FindGroupResumeSessionIdAsync(HarnessRpcClient rpc, string projectRoot,
        string rootCauseKey, string taskId, CancellationToken cancellationToken)
    {
        var normalized = NormalizeRoot(projectRoot);
        var candidates = GetRecentTasks(200)
            .Where(task => task.IsRunning
                && !string.IsNullOrWhiteSpace(task.SessionId)
                && !string.Equals(task.TaskId, taskId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeRoot(task.ProjectRoot), normalized, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(task.RootCauseKey)
                && string.Equals(task.RootCauseKey, rootCauseKey, StringComparison.Ordinal))
            .OrderByDescending(task => task.StartedUtc)
            .ToList();
        if (candidates.Count == 0) return null;
        var list = await rpc.ListSessionsAsync(cancellationToken);
        if (!list.Success) return null; // Host 无法核验：不虚报接回，走保守隔离。
        foreach (var task in candidates)
        {
            var item = list.Value?["items"]?.AsArray()
                .FirstOrDefault(node => string.Equals(HarnessJson.Text(node?["sessionId"]), task.SessionId, StringComparison.Ordinal));
            var running = item is not null
                && item["running"] is System.Text.Json.Nodes.JsonValue runningValue
                && runningValue.TryGetValue<bool>(out var isRunning) && isRunning;
            if (running) return task.SessionId;
        }
        return null;
    }

    /// <summary>从任务目录 manifest.json 读取显式 rootCauseKey（合同组键）；缺失/损坏/空白返回 null。</summary>
    private static string? ReadRootCauseKey(string taskDirectory)
    {
        var path = Path.Combine(taskDirectory, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("rootCauseKey", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch
        {
            // 损坏 manifest 安全降级为无组键（保守隔离），绝不因解析失败猜测合并。
        }
        return null;
    }

    private static string NormalizeRoot(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();

    /// <summary>
    /// 只包含任务目录定位、项目根、合同标识与启动/停止要求的短提示（第一阶段收敛）；
    /// 长期行为规则（冻结决策、集中读取与批量编辑、强度检查预算、无显式 workerChecks 的默认 Release build 规则等）
    /// 保留在 codex-contract preset persona 与派生的 WORKER_ACCEPTANCE.md 中，运行时 prompt 不复制整段 persona/测试政策。
    /// </summary>
    private static string BuildPrompt(string projectRoot, string taskDirectory, string taskId, string contractFingerprint)
        => $"请执行任务目录中的合同任务。项目根目录：{projectRoot}。任务目录：{taskDirectory}。只读取任务目录内的 SPEC.md、HANDOFF.md、manifest.json 与 WORKER_ACCEPTANCE.md（绝不读取 ACCEPTANCE.md）；方案已冻结，直接实施，不要重新规划，不要修改项目根目录之外的文件，禁止截图、查看图片、做视觉结论或发布结论。完成后只运行 WORKER_ACCEPTANCE.md 中的 workerChecks，每项最多一次，严禁追加任何未列出的构建、测试、检查或自查；每个 workerCheck 前后以 UTF-8 无 BOM 原子更新任务目录内 PROGRESS.json；完成即报告：在任务目录写入 EXECUTION_REPORT.md，采用固定键 Markdown 格式（- 任务 ID：{taskId}、- 合同指纹：{contractFingerprint}、- 退出码：、- 修改文件：、- workerChecks：、- 风险/未完成项：），报告写入时间不得早于任务开始时间；写入 EXECUTION_REPORT.md 后停止，等待 GPT 验收。合同标识：{taskId}（指纹 {contractFingerprint}）。";

    private static string ComputeContractFingerprint(string taskDirectory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "SPEC.md", "HANDOFF.md", "manifest.json" })
        {
            var path = Path.Combine(taskDirectory, name);
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData([0]);
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// WebSocket 事件流不可用后的 HTTP session.history 增量轮询：每轮只处理 seq 大于
    /// <paramref name="lastSeq"/> 的新事件（绝不每轮全量重新计数），并沿用 WebSocket 阶段
    /// 的同一个 <see cref="HarnessDegenerationDetector"/>；重复文本/重复工具调用达到阈值时
    /// 立即 session.cancel 并写 failed（绝不自动重提合同或创建新会话）。
    /// 会话已停止但 history 长时间没有可信 turn/end 时诚实失败，绝不伪造成功。
    /// </summary>
    private async Task<TerminalState> PollSessionTerminalAsync(HarnessRpcClient rpc, string sessionId, HarnessTaskStatus current,
        HarnessDegenerationDetector detector, long lastSeq, CancellationToken cancellationToken)
    {
        var noTerminalRounds = 0;
        var noStartEvidenceRounds = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = await rpc.ListSessionsAsync(cancellationToken);
            if (!list.Success)
            {
                current = WithSummary(current with { State = "running", Message = "实时事件流不可用，正通过 HTTP 会话状态继续等待。", UpdatedUtc = DateTime.UtcNow }, detector);
                Write(current);
                await Task.Delay(HttpPollInterval, cancellationToken);
                continue;
            }

            var item = list.Value?["items"]?.AsArray()
                .FirstOrDefault(node => string.Equals(HarnessJson.Text(node?["sessionId"]), sessionId, StringComparison.Ordinal));
            if (item is null)
                return WithSummary(new TerminalState("failed", "Harness Host 中找不到已提交的会话。"), detector);
            var running = item["running"] is System.Text.Json.Nodes.JsonValue runningValue
                && runningValue.TryGetValue<bool>(out var isRunning) && isRunning;

            // WebSocket 已断而 session 仍显示 running 时，不能无限把一个并未
            // 真正启动的会话伪装成“运行中”。先从 session 投影读取可验证进度；
            // 再增量读取 history：history 中出现的真实事件（turn/start、assistant 增量、
            // turn/end 等）本身就是启动证据，绝不因 session.list 缺投影就误判零证据。
            // 连续四轮既无事件、无步骤也无 token，才视为中继提交后的启动失败。
            current = EnrichFromSessionItem(current, item);

            // 增量读取 history：只处理 seq > lastSeq 的新事件，同一检测器继续累计。
            // 事件处理会推进 lastSeq / 置 running / 捕获终态，因此必须先于零证据判定。
            var history = await rpc.GetSessionHistoryAsync(sessionId, cancellationToken);
            TerminalState? terminal = null;
            if (history.Success && history.Value?["events"] is System.Text.Json.Nodes.JsonArray events)
            {
                foreach (var node in events)
                {
                    var eventNode = node?["event"];
                    if (eventNode?["seq"] is System.Text.Json.Nodes.JsonValue seqValue
                        && seqValue.TryGetValue<long>(out var seq))
                    {
                        if (seq <= lastSeq) continue;
                        lastSeq = seq;
                    }
                    var type = HarnessJson.Text(eventNode?["type"]);
                    if (string.Equals(type, "turn/end", StringComparison.Ordinal))
                    {
                        // 提交进行中的工具调用后再判退化；completed 只进入 awaiting-gpt 候选，
                        // 必须通过 EXECUTION_REPORT.md 完成门禁（与 WebSocket 路径行为一致）。
                        detector.CommitToolCall();
                        detector.EnterStage("report");
                        if (detector.IsDegenerate)
                        {
                            await TryCancelSessionAsync(rpc, sessionId);
                            var message = $"检测到重复生成/重复工具调用，已停止以防继续覆盖，等待 GPT 接管（{detector.Reason}）。";
                            return WithSummary(new TerminalState("failed", message), detector);
                        }
                        terminal = WithSummary(GateReport(MapTurnEnd(HarnessJson.Text(eventNode?["data"]?["reason"]?["kind"])), current), detector);
                        break;
                    }
                    if (string.Equals(type, "turn/start", StringComparison.Ordinal))
                    {
                        // 每次 turn/start 必须重置 step 级退化检测窗口（WebSocket 与 HTTP 轮询行为一致）。
                        detector.ResetStep();
                        detector.EnterStage("start");
                        current = WithSummary(current with { State = "running", Message = "会话正在运行（HTTP 事件轮询）。", UpdatedUtc = DateTime.UtcNow }, detector);
                        Write(current);
                        continue;
                    }
                    // 非终态事件：尽力提取 assistant 增量字段（多种协议形状兼容），喂给同一退化检测器。
                    var data = eventNode?["data"];
                    var text = FirstValue(data?["text"], data?["delta"], data?["message"], data?["chunk"]?["text"]);
                    var toolName = FirstValue(data?["toolName"], data?["name"], data?["tool"]?["name"]);
                    var toolArgs = FirstValue(data?["arguments"], data?["args"], data?["tool"]?["arguments"], data?["chunk"]?["argumentsDelta"]);
                    // 由工具名统一推断阶段（WS 与 HTTP 轮询共用同一映射），阶段转换即进展。
                    var stage = HarnessDegenerationDetector.MapToolStage(toolName);
                    var stageChanged = stage is not null && detector.EnterStage(stage);
                    if (text is not null) detector.ObserveText(text);
                    // 带 toolName 开始新调用并提交上一次；纯参数增量只追加不单独计次（分片不误计数）。
                    detector.ObserveToolCall(toolName, toolArgs);
                    if (detector.IsDegenerate)
                    {
                        // 达到退化阈值：立即取消会话并写 failed；绝不自动重提合同或创建新会话。
                        await TryCancelSessionAsync(rpc, sessionId);
                        var message = $"检测到重复生成/重复工具调用，已停止以防继续覆盖，等待 GPT 接管（{detector.Reason}）。";
                        return WithSummary(new TerminalState("failed", message), detector);
                    }
                    // 真实工具调用且发生阶段切换：写入状态与 PROGRESS.json（同一阶段内增量不重复写盘）。
                    if (stageChanged)
                    {
                        current = WithSummary(current with { Message = "检测到真实工具调用（" + ShortToolName(toolName) + "）。", UpdatedUtc = DateTime.UtcNow }, detector);
                        Write(current);
                    }
                }
            }

            // 启动证据：history 中出现的任何事件（lastSeq 已推进）或步骤/token 投影，任一满足即已启动。
            var hasStartEvidence = lastSeq >= 0
                || current.Steps > 0
                || current.UncachedInputTokens > 0
                || current.CacheReadTokens > 0
                || current.OutputTokens > 0;
            if (!hasStartEvidence)
            {
                if (++noStartEvidenceRounds > 3)
                    return WithSummary(new TerminalState("failed",
                        "提交后的事件流不可用，且连续探测未发现会话启动证据（事件、步骤和 token 均为空）；已停止等待，未重复提交任务。"), detector);
                current = WithSummary(current with
                {
                    State = "starting",
                    Message = $"正在确认 Harness 会话是否真正启动（{noStartEvidenceRounds}/4）…",
                    UpdatedUtc = DateTime.UtcNow
                }, detector);
                Write(current);
                await Task.Delay(HttpPollInterval, cancellationToken);
                continue;
            }
            noStartEvidenceRounds = 0;
            if (terminal is not null) return terminal;

            if (!running)
            {
                // 会话已停止但 history 尚未出现可信 turn/end：有限等待后诚实失败（绝不伪造成功）。
                if (++noTerminalRounds > 30)
                    return WithSummary(new TerminalState("failed", "会话已停止，但 HTTP 事件历史长时间未出现可信的结束事件（turn/end），不能判定成功。"), detector);
                current = WithSummary(current with { State = "running", Message = "会话已结束，正在读取可信终态…", UpdatedUtc = DateTime.UtcNow }, detector);
                Write(current);
            }
            else
            {
                noTerminalRounds = 0;
                // 保留进入 HTTP 回退前的精确事件流错误，不能用泛化文案覆盖根因；
                // 否则现场只能看到“已回退”，无法判断是 Node 中继退出、协议关闭还是连接失败。
                if (!current.Message.Contains("事件流", StringComparison.Ordinal)
                    && !current.Message.Contains("中继", StringComparison.Ordinal))
                    current = WithSummary(current with { State = "running", Message = "实时事件流不可用，已切换为 HTTP 事件增量轮询。", UpdatedUtc = DateTime.UtcNow }, detector);
                Write(current);
            }
            await Task.Delay(HttpPollInterval, cancellationToken);
        }
    }

    /// <summary>取第一个非空文本值（对象/数组节点转紧凑 JSON 文本），用于 history 事件增量提取。</summary>
    private static string? FirstValue(params System.Text.Json.Nodes.JsonNode?[] candidates)
    {
        foreach (var node in candidates)
        {
            if (node is null) continue;
            if (node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) && text is not null) return text;
            if (node is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray) return node.ToJsonString();
        }
        return null;
    }

    private static async Task<HarnessTaskStatus> EnrichFromSessionAsync(HarnessRpcClient rpc, HarnessTaskStatus status, CancellationToken cancellationToken)
    {
        try
        {
            var list = await rpc.ListSessionsAsync(cancellationToken);
            var items = list.Value?["items"]?.AsArray();
            var item = items?.FirstOrDefault(node => string.Equals(HarnessJson.Text(node?["sessionId"]), status.SessionId, StringComparison.Ordinal));
            return EnrichFromSessionItem(status, item);
        }
        catch { return status; }
    }

    /// <summary>用已取得的 session.list 条目刷新统计投影（模型/步骤/token），不额外发起 RPC。</summary>
    private static HarnessTaskStatus EnrichFromSessionItem(HarnessTaskStatus status, System.Text.Json.Nodes.JsonNode? item)
    {
        var values = item?["projections"]?["values"];
        return status with
        {
            Model = HarnessJson.Text(values?["model"]?["modelId"]) ?? HarnessJson.Text(values?["model"]?["id"]),
            Steps = ReadInt(values?["sessionStats"]?["steps"]),
            UncachedInputTokens = ReadLong(values?["tokenUsage"]?["uncachedInputTokens"]),
            CacheReadTokens = ReadLong(values?["tokenUsage"]?["cacheReadTokens"]),
            OutputTokens = ReadLong(values?["tokenUsage"]?["outputTokens"])
        };
    }

    private static int ReadInt(System.Text.Json.Nodes.JsonNode? node)
        => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static long ReadLong(System.Text.Json.Nodes.JsonNode? node)
        => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    /// <summary>工具名短展示（最多 48 字符），避免把完整工具名/参数写入状态消息。</summary>
    private static string ShortToolName(string? toolName)
        => string.IsNullOrWhiteSpace(toolName) ? "未知工具" : (toolName.Length <= 48 ? toolName : toolName[..48] + "…");

    /// <summary>
    /// 把退化检测器的进程内压缩摘要合并进任务状态（阶段/工具调用数/推理事件数/无进展告警数）。
    /// 仅诊断计数，绝不持久化原始文本/工具参数/凭据。
    /// </summary>
    private static HarnessTaskStatus WithSummary(HarnessTaskStatus status, HarnessDegenerationDetector detector)
    {
        var summary = detector.Snapshot();
        return status with
        {
            Stage = summary.Stage ?? status.Stage,
            ToolCallCount = summary.ToolCallCount,
            ReasoningEventCount = summary.ReasoningEventCount,
            NoProgressWarnings = summary.NoProgressWarnings
        };
    }

    /// <summary>把检测器的压缩摘要附加到终态（供 StartCoreAsync 应用进持久化状态）。</summary>
    private static TerminalState WithSummary(TerminalState terminal, HarnessDegenerationDetector detector)
        => terminal with { Summary = detector.Snapshot() };

    private static void WriteReviewPacket(HarnessTaskStatus status)
    {
        try
        {
            // REVIEW_PACKET 必须记录报告校验结果（归属/时效/结构），不得只检查文件是否存在。
            var validation = HarnessExecutionReportValidator.Validate(
                status.TaskDirectory, status.TaskId, status.ContractFingerprint, status.StartedUtc);
            var reportState = validation.Valid
                ? "通过（归属/时效/结构已校验）"
                : "未通过（" + validation.Reason + "），GPT 不得直接判定通过";
            var text = $"# GPT 验收包\n\n- 执行器：{status.Executor}\n- 会话 ID：{status.SessionId}\n- 终态：{status.State}\n- 步骤：{status.Steps}\n- 未缓存输入：{status.UncachedInputTokens}\n- 缓存命中：{status.CacheReadTokens}\n- 输出：{status.OutputTokens}\n- 报告校验：{reportState}\n\nGPT 必须检查实际 diff，并独立执行 ACCEPTANCE.md 中的聚焦验收；视觉项只由 GPT 验收。\n";
            AtomicFile.WriteAllText(Path.Combine(status.TaskDirectory, "REVIEW_PACKET.md"), text);
        }
        catch { }
    }

    /// <summary>停止任务的请求结果：Requested=false 表示无法发出取消请求（缺少会话 ID），Message 为可读原因。</summary>
    public sealed record HarnessStopResult(bool Requested, string Message);

    /// <summary>持久化取消意图的 SessionState 标记：用于会话创建前（尚无 sessionId）或跨进程的停止请求。</summary>
    public const string CancelRequestedSessionState = "cancel-requested";

    /// <summary>持久化取消意图：仅在任务仍属运行态（starting/running）且尚无会话或无法核验 Host 时写入，供并发/后续提交进程在创建会话前终止。</summary>
    private void PersistCancelIntent(HarnessTaskStatus status)
    {
        if (!status.IsRunning) return;
        var updated = status with
        {
            SessionState = CancelRequestedSessionState,
            Message = "收到停止请求，等待会话创建前终止（尚未创建 Harness 会话）。",
            UpdatedUtc = DateTime.UtcNow
        };
        Write(updated);
        // 统一活动任务记录写入取消意图：停止期间任何新的 starting 都必须兑现取消，绝不被覆盖；
        // 新合同只有在旧 session 核验终止（running=false）且项目租约释放后才被允许提交。
        HarnessTaskStateStore.WriteActiveRecord(status.ProjectRoot, status.TaskId, status.SessionId, "cancel-requested");
    }

    /// <summary>读取持久化取消意图：开始提交/创建会话前调用，命中即返回可读终态（由调用方写回并终止）。</summary>
    private static HarnessTaskStatus? TryConsumeCancelIntent(HarnessTaskStatus status)
        => string.Equals(status.SessionState, CancelRequestedSessionState, StringComparison.Ordinal)
            ? status with
            {
                State = "cancelled",
                Message = "任务已停止：收到取消请求时尚未创建 Harness 会话，未提交、未创建会话。",
                SessionState = "cancelled",
                UpdatedUtc = DateTime.UtcNow
            }
            : null;

    /// <summary>从磁盘重新读取任务状态并尝试兑现取消意图（取消可能由本进程/跨进程在任意时刻写入）。</summary>
    private HarnessTaskStatus? TryConsumePendingCancel(string taskId)
    {
        var current = TryRead(taskId);
        return current is null ? null : TryConsumeCancelIntent(current);
    }

    /// <summary>
    /// 停止运行中的 Harness 任务：取消本地等待并通过 session.cancel 请求 Host 取消会话。
    /// 任务中心可见的运行任务可能来自本进程（live 字典）或之前 Helper 进程留下的状态文件
    /// （如 Helper 重启后会话仍在 Host 中运行）；只要状态文件带有真实 SessionId，就回退为
    /// 直接向 Host 发送 session.cancel，绝不静默失败。任务正文/凭据不进入任何参数或日志。
    /// </summary>
    public async Task<HarnessStopResult> StopTaskAsync(string taskId)
    {
        ActiveHarnessTask? active = null;
        lock (sync)
        {
            if (live.TryGetValue(taskId, out var entry)) active = entry;
        }
        var sessionId = active?.SessionId;
        var fromPersisted = false;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var persisted = TryRead(taskId);
            sessionId = persisted?.SessionId;
            fromPersisted = persisted is not null;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                // 会话尚未创建（starting）或状态文件无会话：不能向 Host 发送 session.cancel。
                // 必须持久化取消意图并让本进程 live 立即终止，由创建会话前的核验兑现取消，
                // 绝不静默放弃或误报成功。
                if (persisted is not null && persisted.IsRunning)
                {
                    if (active is not null) active.Stop.Cancel();
                    PersistCancelIntent(persisted);
                    return new HarnessStopResult(true, "已记录取消请求：任务尚未创建 Harness 会话，将在创建会话前终止；未创建会话、未提交。");
                }
                return new HarnessStopResult(false, fromPersisted
                    ? "任务状态文件缺少可运行会话（会话 ID 缺失或已结束），无需取消。"
                    : "未找到该任务的状态文件，无法取消。");
            }
        }
        if (active is not null) active.Stop.Cancel();

        HarnessRpcResult cancel;
        try
        {
            using var rpc = new HarnessRpcClient(WebUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            cancel = await rpc.CancelAsync(sessionId, cts.Token);
        }
        catch (Exception ex)
        {
            return new HarnessStopResult(active is not null, "已请求取消，但 Host 不可达（" + HarnessJson.Truncate(ex.Message, 120) + "）；任务在 Host 中可能仍在运行。");
        }
        if (!cancel.Success)
            return new HarnessStopResult(active is not null, "已请求取消，但 Host 返回失败：" + HarnessJson.Truncate(cancel.ErrorMessage, 160));
        using var verifyRpc = new HarnessRpcClient(WebUrl);
        if (!await WaitForSessionStoppedAsync(verifyRpc, sessionId, TimeSpan.FromSeconds(6)))
            return new HarnessStopResult(false, "Harness 已接受取消请求，但会话仍在运行；未虚报已停止，请稍后重试或在 Harness Web 中停止。");
        // 非 live 回退路径没有监听者更新终态：只有 session.list 确认 running=false 后才标记取消。
        if (active is null)
        {
            var current = TryRead(taskId);
            if (current is not null && current.IsRunning)
                Write(current with { State = "cancelled", Message = "用户已停止任务（已向 Harness 发送取消请求）。", UpdatedUtc = DateTime.UtcNow });
        }
        return new HarnessStopResult(true, "Harness 会话已确认停止。");
    }

    /// <summary>停止运行中的 Harness 任务（尽力而为、不等待 Host 响应；与既有调用兼容）。</summary>
    public void StopTask(string taskId)
    {
        _ = StopTaskAsync(taskId);
    }

    private static async Task<bool> TryCancelSessionAsync(HarnessRpcClient rpc, string sessionId)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var result = await rpc.CancelAsync(sessionId, cts.Token);
                if (!result.Success) continue;
                if (await WaitForSessionStoppedAsync(rpc, sessionId, TimeSpan.FromSeconds(3)))
                    return true;
            }
            catch
            {
                // A transient RPC/stream failure must not turn an accepted cancellation into
                // a false terminal state. Retry and verify against session.list instead.
            }
        }
        return false;
    }

    private static async Task<bool> WaitForSessionStoppedAsync(HarnessRpcClient rpc, string sessionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var list = await rpc.ListSessionsAsync();
            if (!list.Success) return false;
            var item = list.Value?["items"]?.AsArray()
                .FirstOrDefault(node => string.Equals(HarnessJson.Text(node?["sessionId"]), sessionId, StringComparison.Ordinal));
            if (item is null) return true;
            var running = item["running"] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<bool>(out var isRunning) && isRunning;
            if (!running) return true;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>宽容读取任务状态：以任务目录 HARNESS_STATUS.json 真相源优先，旧注册表索引兼容回退并迁移；缺失/损坏返回 null。</summary>
    public HarnessTaskStatus? TryRead(string taskId)
        => HarnessTaskStateStore.TryReadStatus(taskRegistry, taskId);

    /// <summary>最近任务快照（按启动时间倒序；只从已登记任务目录读取，不做全盘递归扫描；离线/测试兼容的同步读取）。</summary>
    public IReadOnlyList<HarnessTaskStatus> GetRecentTasks(int limit = 20)
    {
        var wanted = Math.Max(1, limit);
        return Directory.EnumerateFiles(taskRegistry, "*.json")
            .Select(path => HarnessTaskStateStore.TryReadStatus(taskRegistry, Path.GetFileNameWithoutExtension(path)))
            .Where(status => status is not null)
            .Cast<HarnessTaskStatus>()
            .OrderByDescending(status => status.StartedUtc)
            .Take(wanted)
            .ToList();
    }

    /// <summary>对账结果：改写（写回）的任务数与可读说明；不携带任务正文/凭据。</summary>
    public sealed record HarnessReconcileResult(int Written, string Message);

    /// <summary>
    /// 异步 recent-tasks 对账：一次 <c>session.list</c> 对账所有本地活动记录（running/starting 且带会话 ID）。
    /// 只在 Host 可达且得到可信响应时改写状态文件：
    /// <list type="bullet">
    /// <item>会话在列表中且 running → 保持 running，并刷新统计投影；</item>
    /// <item>会话在列表中且已结束 → 读 history 的最后一个 turn/end reason.kind，沿用 <see cref="MapTurnEnd"/> 写回终态；</item>
    /// <item>会话在列表中且已结束，但 history 缺失/损坏或没有可信 turn/end → 写回 failed，
    /// 说明“会话已停止，但缺少可信终态，不能判定成功”，任务不得永久停留在“进行中”；</item>
    /// <item>会话不在列表中 → 标记 failed/interrupted（诚实说明），绝不伪造 completed；</item>
    /// <item>Host 不可达/响应不可信 → 不改写任何状态；</item>
    /// <item>无会话 ID 的任务无法在 Host 中核对，保持原样（由真实提交进程负责推进）。</item>
    /// </list>
    /// 每次写回均原子完成（<see cref="Write"/>）。
    /// </summary>
    public async Task<HarnessReconcileResult> ReconcileRecentTasksAsync(CancellationToken cancellationToken = default)
    {
        // 带会话 ID 的运行中/启动中任务：与 Host 会话对账。
        var active = GetRecentTasks(100)
            .Where(task => task.IsRunning && !string.IsNullOrWhiteSpace(task.SessionId))
            .ToList();
        // 无会话 ID 但仍标记 starting 的任务：若本地租约已释放即为“孤儿占位”（Runner 意外终止遗留），
        // 必须诚实清扫为 failed，绝不因它而永久 busy 或重复创建。
        var orphanStarting = GetRecentTasks(100)
            .Where(task => string.Equals(task.State, "starting", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(task.SessionId)
                && !IsTaskLeaseHeld(task.TaskDirectory))
            .ToList();
        // 孤儿清扫是纯本地租约判定（不依赖 Host），先执行：Runner 意外终止后留下的 starting 无会话
        // 且租约已释放的任务，诚实清扫为 failed，避免它对新建会话造成永久 busy 阻塞。
        var orphanWritten = 0;
        foreach (var orphan in orphanStarting)
        {
            Write(orphan with
            {
                State = "failed",
                Message = "对账发现该任务停留在 starting 但从未创建会话，且本地租约已释放（可能 Runner 意外终止遗留）；标记为失败，不阻塞后续任务。",
                SessionState = "failed",
                UpdatedUtc = DateTime.UtcNow
            });
            orphanWritten++;
        }
        if (active.Count == 0)
            return new HarnessReconcileResult(orphanWritten,
                orphanWritten > 0 ? $"已清扫 {orphanWritten} 个孤儿 starting 占位（无会话且租约已释放），没有带会话 ID 的活动任务需要对账。" : "没有带会话 ID 的活动任务需要对账。");

        using var rpc = (RpcClientFactory ?? (() => new HarnessRpcClient(WebUrl)))();
        HarnessRpcResult list;
        try
        {
            list = await rpc.ListSessionsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HarnessReconcileResult(0, "对账失败：Host 不可达（" + HarnessJson.Truncate(ex.Message, 120) + "），未改写任何状态。");
        }
        if (!list.Success)
            return new HarnessReconcileResult(0, "对账失败：Host 响应不可信（" + HarnessJson.Truncate(list.ErrorMessage, 160) + "），未改写任何状态。");

        var items = list.Value?["items"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();
        var sessions = new Dictionary<string, System.Text.Json.Nodes.JsonObject>(StringComparer.Ordinal);
        foreach (var node in items)
        {
            var sessionId = HarnessJson.Text(node?["sessionId"]);
            if (string.IsNullOrWhiteSpace(sessionId) || node is not System.Text.Json.Nodes.JsonObject obj) continue;
            sessions[sessionId] = obj;
        }

        var written = 0;
        var checkedCount = 0;
        foreach (var task in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = task.SessionId!;
            HarnessTaskStatus updated;
            if (sessions.TryGetValue(sessionId, out var session))
            {
                var running = session["running"] is System.Text.Json.Nodes.JsonValue runningValue
                    && runningValue.TryGetValue<bool>(out var isRunning) && isRunning;
                if (running)
                {
                    // 会话仍在运行：保持 running，只用已取得的列表条目刷新统计投影。
                    updated = EnrichFromSessionItem(task, session);
                }
                else
                {
                    // 会话已结束：以 history 的最后一个 turn/end 原因映射终态，绝不凭空猜测。
                    var terminal = await ReadEndedSessionTerminalAsync(rpc, sessionId, cancellationToken);
                    if (terminal is null)
                    {
                        // session.list 已确认会话停止，但 history 缺失/损坏或没有可信 turn/end：
                        // 写回诚实的非运行终态 failed（绝不伪造 completed），任务不得永久停留在"进行中"。
                        updated = task with
                        {
                            State = "failed",
                            Message = "会话已停止，但缺少可信终态（session.history 缺失、损坏或没有 turn/end），不能判定成功。",
                            SessionState = "failed"
                        };
                    }
                    else
                    {
                        // completed 只进入 awaiting-gpt 候选：对账写回的终态同样必须通过报告完成门禁。
                        var gated = GateReport(terminal, task);
                        updated = EnrichFromSessionItem(task with { State = gated.State, Message = gated.Message }, session);
                    }
                }
            }
            else
            {
                // 会话在 Host 中不存在：诚实标记失败，绝不伪造 completed。
                updated = task with
                {
                    State = "failed",
                    Message = "对账发现该会话已不在 Harness Host 中（可能已删除或从未持久化），标记为失败。",
                    UpdatedUtc = DateTime.UtcNow
                };
            }
            updated = updated with { UpdatedUtc = DateTime.UtcNow };
            Write(updated);
            written++;
            checkedCount++;
        }

        var orphanSuffix = orphanWritten > 0 ? $"（其中清扫 {orphanWritten} 个孤儿 starting 占位）" : "";
        return new HarnessReconcileResult(written + orphanWritten,
            $"已对账 {checkedCount} 个活动任务，改写 {written + orphanWritten} 个状态文件{orphanSuffix}。");
    }

    /// <summary>
    /// 读取已结束会话的终态：session.history 中最后一个 turn/end 的 reason.kind → MapTurnEnd。
    /// history 调用失败、响应不可信或没有任何 turn/end 时返回 null
    /// （调用方写回诚实的 failed 终态，绝不凭空猜测或伪造 completed）。
    /// </summary>
    private async Task<TerminalState?> ReadEndedSessionTerminalAsync(HarnessRpcClient rpc, string sessionId, CancellationToken cancellationToken)
    {
        HarnessRpcResult history;
        try
        {
            history = await rpc.GetSessionHistoryAsync(sessionId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        if (!history.Success) return null;
        var events = history.Value?["events"]?.AsArray();
        if (events is null) return null;
        var turnEnd = events.LastOrDefault(node => string.Equals(HarnessJson.Text(node?["event"]?["type"]), "turn/end", StringComparison.Ordinal));
        // 没有任何可信 turn/end：返回 null，由调用方写回诚实的非运行终态（绝不凭空猜测）。
        if (turnEnd is null) return null;
        return MapTurnEnd(HarnessJson.Text(turnEnd?["event"]?["data"]?["reason"]?["kind"]));
    }

    /// <summary>
    /// 串行化状态写入：事件监听线程与提交流程可能并发写同一任务状态，
    /// 必须避免并发 AtomicFile 操作（并发 File.Replace 会互相干扰并可能遗留临时文件）。
    /// 统一双写：任务目录 HARNESS_STATUS.json 真相源（主）+ harness-tasks 轻量索引（兼容入口），
    /// 并投影任务目录 PROGRESS.json（真实事件/工具/可信终态驱动；worker 已写的检查进度保留）。
    /// 任务进入可信终态（非运行）且活动任务记录仍属本任务时清除占用，停止期间取消意图
    /// 由 StopTaskAsync 单独写入 active 记录（cancel-requested），绝不被新 starting 覆盖。
    /// </summary>
    private void Write(HarnessTaskStatus status)
    {
        lock (writeSync)
        {
            HarnessTaskStateStore.WriteStatus(taskRegistry, status);
            HarnessTaskStateStore.ProjectProgress(status.TaskDirectory, status);
            if (!status.IsRunning)
                HarnessTaskStateStore.TryRemoveActiveRecord(status.ProjectRoot, status.TaskId);
        }
    }
}
