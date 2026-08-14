using System.Net.WebSockets;
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
    string Executor = "DeepSeek Harness",
    string? Model = null,
    int Steps = 0,
    long UncachedInputTokens = 0,
    long CacheReadTokens = 0,
    long OutputTokens = 0)
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
/// 合同执行前复用 EnsureWebHostReadyAsync（Host 未运行时自动启动并等待健康检查），
/// 随后探测中继（提交/事件流/取消三项）并提交；任何一项未确认都诚实降级，绝不虚报成功。
/// 不再使用 dsh run &lt;taskDirectory&gt; 进程路径，不存在会虚报成功的回退。
/// </summary>
public sealed class DeepSeekHarnessRunner
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    private readonly AppPaths paths;
    private readonly string taskRegistry;
    private readonly Dictionary<string, ActiveHarnessTask> live = new(StringComparer.OrdinalIgnoreCase);
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
    /// <summary>事件流断开后的最大重连次数（默认 3）。</summary>
    public int MaxEventReconnects { get; init; } = 3;
    /// <summary>事件流重连间隔（默认 500 毫秒）。</summary>
    public TimeSpan EventReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(500);
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
        var now = DateTime.UtcNow;
        var status = new HarnessTaskStatus(taskId, projectRoot, taskDirectory, "starting", "正在提交到 Harness Web Host 会话。", now, now, 0, WebUrl);
        Write(status);

        // 合同执行前复用 EnsureWebHostReadyAsync：Host 未运行时自动启动并等待健康检查。
        var readiness = HostReadyEnsurer is not null
            ? await HostReadyEnsurer(cancellationToken)
            : await new DeepSeekHarnessService(paths).EnsureWebHostReadyAsync(cancellationToken: cancellationToken);
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
        if (!capability.Confirmed || !capability.SubmitSupported)
        {
            status = status with { State = "failed", Message = "中继能力未确认，任务未提交。" + capability.Message, UpdatedUtc = DateTime.UtcNow };
            Write(status);
            return status;
        }
        status = status with { Message = capability.Message + " 正在创建会话。", UpdatedUtc = DateTime.UtcNow };
        Write(status);

        using var rpc = (RpcClientFactory ?? (() => new HarnessRpcClient(WebUrl)))();
        HarnessRpcResult created;
        var previous = TryRead(taskId);
        var reusableSessionId = previous?.SessionId;
        try
        {
            created = string.IsNullOrWhiteSpace(reusableSessionId)
                ? await rpc.CreateSessionAsync(projectRoot, "standard", cancellationToken)
                : HarnessRpcResult.Ok(new System.Text.Json.Nodes.JsonObject { ["sessionId"] = reusableSessionId });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            created = HarnessRpcResult.Fail("RPC 异常：" + HarnessJson.Truncate(ex.Message, 200));
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

        status = status with { State = "running", Message = "会话已创建，正在提交合同提示。", SessionId = sessionId, UpdatedUtc = DateTime.UtcNow };
        Write(status);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (sync) live[taskId] = new ActiveHarnessTask(stop, sessionId);
        try
        {
            var listener = Task.Run(() => ListenForTerminalAsync(rpc, sessionId, status, stop.Token), CancellationToken.None);

            HarnessRpcResult prompt;
            try
            {
                prompt = await rpc.PromptAsync(sessionId, BuildPrompt(taskDirectory), "Asia/Shanghai", cancellationToken);
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
                try { await listener; } catch (OperationCanceledException) { } catch { }
                await TryCancelSessionAsync(rpc, sessionId);
                status = status with { State = "failed", Message = "提交合同提示失败：" + prompt.ErrorMessage, UpdatedUtc = DateTime.UtcNow };
                Write(status);
                return status;
            }
            status = status with { Message = "任务提示已提交，会话正在运行。", UpdatedUtc = DateTime.UtcNow };
            Write(status);

            TerminalState terminal;
            try
            {
                terminal = await listener;
            }
            catch (OperationCanceledException)
            {
                // 用户停止：本地标记取消，并尽力请求 Host 取消会话。
                await TryCancelSessionAsync(rpc, sessionId);
                status = status with { State = "cancelled", Message = "用户已停止任务。", UpdatedUtc = DateTime.UtcNow };
                Write(status);
                return status;
            }

            status = await EnrichFromSessionAsync(rpc, status with { State = terminal.State, Message = terminal.Message, UpdatedUtc = DateTime.UtcNow }, cancellationToken);
            Write(status);
            WriteReviewPacket(status);
            return status;
        }
        finally
        {
            lock (sync) live.Remove(taskId);
        }
    }

    private sealed record TerminalState(string State, string Message);

    /// <summary>
    /// 监听事件流直到会话终态（turn/end）。运行中事件（turn/start）实时写状态；
    /// 事件流断开时有限重连（不提前标 completed）；重连耗尽后诚实失败。
    /// 只记录状态与类型，绝不记录事件正文/密钥等敏感内容。
    /// </summary>
    private async Task<TerminalState> ListenForTerminalAsync(HarnessRpcClient rpc, string sessionId, HarnessTaskStatus initial, CancellationToken cancellationToken)
    {
        var current = initial;
        var attempts = 0;
        while (true)
        {
            try
            {
                using var stream = new DeepSeekHarnessEventStream(WebUrl) { WebSocketFactory = EventSocketFactory };
                await foreach (var frame in stream.ListenAsync(cancellationToken))
                {
                    if (frame.Type == "stream/error")
                    {
                        current = current with { Message = "事件流错误：" + HarnessJson.Truncate(frame.ErrorMessage, 200), UpdatedUtc = DateTime.UtcNow };
                        Write(current);
                        continue;
                    }
                    if (!string.Equals(frame.SessionId, sessionId, StringComparison.Ordinal)) continue;
                    if (frame.EventType == "turn/start")
                    {
                        current = current with { State = "running", Message = "会话正在运行。", UpdatedUtc = DateTime.UtcNow };
                        Write(current);
                    }
                    else if (frame.EventType == "turn/end")
                    {
                        return MapTurnEnd(frame.TurnEndKind);
                    }
                }

                // 事件流被服务端关闭：有限重连，绝不提前标 completed。
                if (++attempts > MaxEventReconnects)
                    return new TerminalState("failed", "事件流已断开且重连失败，无法确认会话终态；会话仍在 Harness Host 中，请在 Web 中人工确认或停止。");
                current = current with { State = "running", Message = $"事件流已断开，正在重连（{attempts}/{MaxEventReconnects}）…", UpdatedUtc = DateTime.UtcNow };
                Write(current);
                await Task.Delay(EventReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (++attempts > MaxEventReconnects)
                    return new TerminalState("failed", "事件流连接失败且重连耗尽（" + HarnessJson.Truncate(ex.Message, 120) + "），无法确认会话终态；请在 Harness Web 中人工确认或停止。");
                current = current with { State = "running", Message = $"事件流连接失败，正在重连（{attempts}/{MaxEventReconnects}）…", UpdatedUtc = DateTime.UtcNow };
                Write(current);
                await Task.Delay(EventReconnectDelay, cancellationToken);
            }
        }
    }

    private static TerminalState MapTurnEnd(string? kind) => kind switch
    {
        "completed" => new TerminalState("completed", "任务已在 Harness 会话中完成。"),
        "aborted" => new TerminalState("cancelled", "会话已取消。"),
        _ => new TerminalState("failed", kind is null
            ? "会话结束但缺少结束原因，视为失败。"
            : $"会话以非成功原因结束（{kind}），视为失败。")
    };

    /// <summary>只包含任务目录定位与执行职责的短提示；任务正文只从项目内合同文件读取，不进入命令行。</summary>
    private static string BuildPrompt(string taskDirectory)
        => $"请执行任务目录中的合同任务。任务目录：{taskDirectory}。合同正文（SPEC.md、HANDOFF.md 等）位于该目录内，请从文件读取后实施；完成后按合同要求运行检查并写报告。不要臆测合同内容，不要修改任务目录之外的文件。";

    private static async Task<HarnessTaskStatus> EnrichFromSessionAsync(HarnessRpcClient rpc, HarnessTaskStatus status, CancellationToken cancellationToken)
    {
        try
        {
            var list = await rpc.ListSessionsAsync(cancellationToken);
            var items = list.Value?["items"]?.AsArray();
            var item = items?.FirstOrDefault(node => string.Equals(HarnessJson.Text(node?["sessionId"]), status.SessionId, StringComparison.Ordinal));
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
        catch { return status; }
    }

    private static int ReadInt(System.Text.Json.Nodes.JsonNode? node)
        => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static long ReadLong(System.Text.Json.Nodes.JsonNode? node)
        => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    private static void WriteReviewPacket(HarnessTaskStatus status)
    {
        try
        {
            var reportExists = File.Exists(Path.Combine(status.TaskDirectory, "EXECUTION_REPORT.md"));
            var text = $"# GPT 验收包\n\n- 执行器：{status.Executor}\n- 会话 ID：{status.SessionId}\n- 终态：{status.State}\n- 步骤：{status.Steps}\n- 未缓存输入：{status.UncachedInputTokens}\n- 缓存命中：{status.CacheReadTokens}\n- 输出：{status.OutputTokens}\n- Worker 报告：{(reportExists ? "已生成" : "缺失，GPT 不得直接判定通过")}\n\nGPT 必须检查实际 diff，并独立执行 ACCEPTANCE.md 中的聚焦验收；视觉项只由 GPT 验收。\n";
            AtomicFile.WriteAllText(Path.Combine(status.TaskDirectory, "REVIEW_PACKET.md"), text);
        }
        catch { }
    }

    /// <summary>停止任务的请求结果：Requested=false 表示无法发出取消请求（缺少会话 ID），Message 为可读原因。</summary>
    public sealed record HarnessStopResult(bool Requested, string Message);

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
                return new HarnessStopResult(false, fromPersisted
                    ? "任务没有可取消的 Harness 会话（会话 ID 缺失）。"
                    : "未找到该任务的状态文件，无法取消。");
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
        // 非 live 回退路径没有监听者更新终态：Host 已确认接受取消后，按用户停止语义标记状态。
        if (active is null)
        {
            var current = TryRead(taskId);
            if (current is not null && current.IsRunning)
                Write(current with { State = "cancelled", Message = "用户已停止任务（已向 Harness 发送取消请求）。", UpdatedUtc = DateTime.UtcNow });
        }
        return new HarnessStopResult(true, "已向 Harness Web Host 发送取消请求。");
    }

    /// <summary>停止运行中的 Harness 任务（尽力而为、不等待 Host 响应；与既有调用兼容）。</summary>
    public void StopTask(string taskId)
    {
        _ = StopTaskAsync(taskId);
    }

    private static async Task TryCancelSessionAsync(HarnessRpcClient rpc, string sessionId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await rpc.CancelAsync(sessionId, cts.Token);
        }
        catch { /* 尽力而为 */ }
    }

    /// <summary>宽容读取状态文件；缺失/损坏返回 null。</summary>
    public HarnessTaskStatus? TryRead(string taskId)
    {
        var path = TaskDirectoryFor(taskId);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<HarnessTaskStatus>(text, Options);
        }
        catch { return null; }
    }

    /// <summary>最近任务快照（按启动时间倒序）。</summary>
    public IReadOnlyList<HarnessTaskStatus> GetRecentTasks(int limit = 20)
    {
        var wanted = Math.Max(1, limit);
        return Directory.EnumerateFiles(taskRegistry, "*.json")
            .Select(path => { try { return JsonSerializer.Deserialize<HarnessTaskStatus>(File.ReadAllText(path, System.Text.Encoding.UTF8), Options); } catch { return null; } })
            .Where(status => status is not null)
            .Cast<HarnessTaskStatus>()
            .OrderByDescending(status => status.StartedUtc)
            .Take(wanted)
            .ToList();
    }

    /// <summary>
    /// 串行化状态写入：事件监听线程与提交流程可能并发写同一任务状态文件，
    /// 必须避免并发 AtomicFile 操作（并发 File.Replace 会互相干扰并可能遗留临时文件）。
    /// </summary>
    private void Write(HarnessTaskStatus status)
    {
        lock (writeSync)
            AtomicFile.WriteAllText(TaskDirectoryFor(status.TaskId), JsonSerializer.Serialize(status, Options));
    }
}
