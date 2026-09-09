using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 任务目录内的 Harness 监督记录（阶段一协议）：schema 版本、taskId、合同指纹、项目/任务绝对路径、
/// 被托管 Runner PID、Runner 启动 UTC（PID 重用身份校验）、监督启动 UTC、监督状态、最后核验 UTC。
/// 绝不包含合同正文、凭据、token 或聊天内容；写入一律 UTF-8 无 BOM 原子写入（<see cref="AtomicFile"/>）。
/// 终态清理只清"活动"语义（由外层 active 记录/任务中心负责），本文件作为可审计监督记录保留。
/// </summary>
public sealed record HarnessSupervisorRecord(
    int SchemaVersion,
    string TaskId,
    string ProjectRoot,
    string TaskDirectory,
    string? ContractFingerprint,
    int RunnerPid,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime RunnerStartedUtc,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime StartedUtc,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime LastVerifiedUtc,
    string SupervisionState,
    string? TerminalOutcome = null,
    int? RunnerExitCode = null)
{
    public bool IsTerminal => string.Equals(SupervisionState, "terminal", StringComparison.OrdinalIgnoreCase);
}

/// <summary>子受管 Runner 进程句柄抽象：测试可注入受控假进程，生产用真实 <see cref="Process"/>。</summary>
public interface IHarnessSupervisedProcess
{
    /// <summary>受管进程 PID。</summary>
    int Id { get; }
    /// <summary>受管进程 OS 启动 UTC（PID 重用身份校验的等价身份）。</summary>
    DateTime StartTimeUtc { get; }
    /// <summary>进程是否已退出。</summary>
    bool HasExited { get; }
    /// <summary>已退出时的退出码；未退出为 null。</summary>
    int? ExitCode { get; }
    /// <summary>等待进程退出；进程退出时完成，外部取消时抛 OperationCanceledException。</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>监督 start 的子进程启动参数（只含两个绝对路径，任务正文绝不进入命令行）。</summary>
public sealed record HarnessSupervisorSpawnArguments(string ProjectRoot, string TaskDirectory);

/// <summary>start 结果：Reused=true 表示返回既有受管句柄（未启动第二个 Runner）；StartedUtc/RunnerPid 为既有或新句柄值。</summary>
public sealed record HarnessSupervisorStartResult(
    bool Reused,
    string TaskId,
    string TaskDirectory,
    int? RunnerPid,
    DateTime? StartedUtc,
    string SupervisionState,
    string Message);

/// <summary>status 快照（固定大小、脱敏，只含定位/状态/核验信息）。</summary>
public sealed record HarnessSupervisorStatusSnapshot(
    bool Found,
    string TaskId,
    string TaskDirectory,
    string? SupervisionState,
    string? TerminalOutcome,
    bool TaskRunning,
    string? TaskState,
    int? RunnerPid,
    DateTime? StartedUtc,
    DateTime? LastVerifiedUtc,
    string Message)
{
    public bool IsTerminal => string.Equals(SupervisionState, "terminal", StringComparison.OrdinalIgnoreCase);
}

/// <summary>await 结果：Outcome 取值 completed/cancelled/failed/uncertain，均代表已给出明确结论；绝不虚报 completed。</summary>
public sealed record HarnessSupervisorAwaitResult(
    string Outcome,
    string Message,
    HarnessTaskStatus? TaskStatus = null,
    bool ReconnectedTerminal = false);

/// <summary>
/// 持久 Harness 等待监督器（阶段一，可测试核心逻辑）：
/// <list type="bullet">
/// <item><b>start</b>：校验两绝对路径与合同存在后启动一个独立受控的 Runner 子进程（传统同步模式），
/// 监督记录在返回前原子落盘（含 PID 与启动 UTC）；立即返回安全摘要与可重连标识，绝不把 "start 已返回" 表述为任务完成；
/// 同一任务已有监督句柄（含真实终态）时返回既有句柄，绝不启动第二个受管 Runner、绝不产生第二次 DSH 提交。</item>
/// <item><b>await</b>：按任务目录读取同一监督记录，只基于"子 Runner 已退出 + HARNESS_STATUS.json 非 running + 已有报告门禁结果"
/// 返回最终结论；不提交新合同。新进程/中断后可对仍存活的 PID 接管等待（以 PID + 启动 UTC 双重身份核验防 PID 重用）；
/// PID 消失而真相状态仍 running/starting 时执行注入的现有安全对账（如 DeepSeekHarnessRunner 的 Host 会话对账），
/// 仍无法确认真实终态则明确写出 uncertain/failed 结论，绝不虚报 completed。</item>
/// <item><b>status</b>：固定大小脱敏摘要，不读取 session.history/DSH 文本。</item>
/// </list>
/// 任务正文只从任务目录文件读取；合同提示/凭据/聊天内容绝不出现在监督记录、摘要或命令行。
/// </summary>
public sealed class HarnessSupervisor
{
    /// <summary>任务目录内的监督记录文件名。</summary>
    public const string RecordFileName = "HARNESS_SUPERVISOR.json";
    /// <summary>监督记录 schema 版本。</summary>
    public const int RecordSchemaVersion = 1;
    /// <summary>监督状态：受管 Runner 存活/应存活。</summary>
    public const string RunningState = "running";
    /// <summary>监督状态：已到真实终态/明确结论（记录保留供审计）。</summary>
    public const string TerminalState = "terminal";

    /// <summary>await/终态结论常量。</summary>
    public const string OutcomeCompleted = "completed";
    public const string OutcomeCancelled = "cancelled";
    public const string OutcomeFailed = "failed";
    public const string OutcomeUncertain = "uncertain";

    /// <summary>子进程启动身份允许的最大时间偏差（PID 重用防护按启动 UTC 双重校验）。</summary>
    private static readonly TimeSpan IdentityTolerance = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions RecordOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    private readonly Dictionary<string, Task<HarnessSupervisorStartResult>> inflightStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();

    /// <summary>子受管进程启动工厂；缺省启动与当前进程同路径的 CodexHelper.HarnessRunner.exe（无 -Mode 传统同步模式）。测试注入受控假进程。</summary>
    public Func<HarnessSupervisorSpawnArguments, IHarnessSupervisedProcess>? SpawnRunner { get; init; }

    /// <summary>按 PID + 启动 UTC 核验并接回既有受管进程；身份不符/进程不存在返回 null。测试注入受控实现。</summary>
    public Func<int, DateTime, IHarnessSupervisedProcess?>? AttachRunner { get; init; }

    /// <summary>可选的现有安全对账（Host 会话列表驱动）；CLI await 注入 <see cref="DeepSeekHarnessRunner.ReconcileRecentTasksAsync"/>。</summary>
    public Func<CancellationToken, Task<DeepSeekHarnessRunner.HarnessReconcileResult>>? ReconcileAsync { get; init; }

    /// <summary>监督记录路径（任务目录内）。</summary>
    public static string RecordPath(string taskDirectory) => Path.Combine(taskDirectory, RecordFileName);

    // ---- start ----

    /// <summary>
    /// 启动受管 Runner（或返回既有受管句柄）。绝对路径校验 + 合同（SPEC.md）存在校验通过后：
    /// 无既有受管 Runner 时启动一个独立受控子进程并在返回前原子写入监督记录；立即返回安全摘要。
    /// 同一任务只允许一个受管 Runner：任务已到真实终态、存在活跃句柄、或已有残留记录时一律返回既有
    /// 句柄/终态结论并说明原因，绝不重复启动、绝不产生第二次 DSH 提交。
    /// </summary>
    public async Task<HarnessSupervisorStartResult> StartAsync(string projectRoot, string taskDirectory, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        if (!PathSafety.IsWithin(taskDirectory, projectRoot))
            throw new InvalidOperationException("任务目录必须位于项目根目录内。");
        var specPath = Path.Combine(taskDirectory, "SPEC.md");
        if (!File.Exists(specPath))
            throw new FileNotFoundException("任务目录缺少 SPEC.md，任务正文只从文件读取。", specPath);
        var taskId = Path.GetFileName(taskDirectory);

        // 进程内并发 start 单飞：同一任务同时只允许一个"正在启动"的飞行中任务；后到者共享结果。
        Task<HarnessSupervisorStartResult> shared;
        lock (sync)
        {
            if (!inflightStarts.TryGetValue(taskId, out shared!))
            {
                shared = StartCoreAsync(projectRoot, taskDirectory, taskId, cancellationToken);
                inflightStarts[taskId] = shared;
            }
        }
        try { return await shared; }
        finally
        {
            lock (sync)
            {
                if (inflightStarts.TryGetValue(taskId, out var same) && ReferenceEquals(same, shared))
                    inflightStarts.Remove(taskId);
            }
        }
    }

    private Task<HarnessSupervisorStartResult> StartCoreAsync(string projectRoot, string taskDirectory, string taskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var truth = TryReadTaskStatus(taskDirectory);
        var record = TryReadRecord(taskDirectory);

        // 任务真相已是真实终态（无论是否有监督记录）：返回既有终态句柄，绝不重新启动受管 Runner。
        if (truth is not null && !truth.IsRunning)
        {
            var (outcome, message) = DeriveTerminal(truth, taskDirectory);
            return Task.FromResult(new HarnessSupervisorStartResult(true, taskId, taskDirectory, record?.RunnerPid,
                record?.StartedUtc, TerminalState,
                $"任务已到真实终态（{OutcomeText(outcome)}），无需启动受管 Runner：" + message));
        }

        // 存在监督记录：同一任务只允许一个受管 Runner。受管 PID 仍存活且身份相符 → 返回活跃句柄。
        if (record is not null && !record.IsTerminal)
        {
            var handle = Attach(record.RunnerPid, record.RunnerStartedUtc);
            if (handle is not null)
            {
                if (handle is IDisposable disposable) disposable.Dispose();
                return Task.FromResult(new HarnessSupervisorStartResult(true, taskId, taskDirectory, record.RunnerPid, record.StartedUtc,
                    RunningState, $"已存在活跃受管 Runner（PID {record.RunnerPid}），未重复启动、未重复提交；可用 -Mode await 接回等待真实终态。"));
            }
            // 受管 PID 已消失但任务真相仍运行/启动中：返回残留句柄并指向 await（安全对账/明确结论），绝不重复启动。
            var runningState = truth?.State ?? "unknown";
            return Task.FromResult(new HarnessSupervisorStartResult(true, taskId, taskDirectory, record.RunnerPid, record.StartedUtc,
                RunningState, $"同一任务已有监督记录但其受管 Runner 已消失，且任务状态仍为 {runningState}；未重复启动、未重复提交，请用 -Mode await 接回（执行现有安全对账）或 -Mode status 查看，绝不虚报完成。"));
        }
        if (record is not null)
        {
            // 终态监督记录但任务真相非终态（罕见）：不重复启动，交由 await 对账给出明确结论。
            return Task.FromResult(new HarnessSupervisorStartResult(true, taskId, taskDirectory, record.RunnerPid, record.StartedUtc,
                record.SupervisionState, $"同一任务已有终态监督记录但任务真相未到真实终态（{truth?.State ?? "状态缺失"}）；未重复启动，请用 -Mode await 接回对账或停止该任务。"));
        }

        // 无监督记录：若任务目录仍被活跃 Runner 租约占用（传统非受管启动在跑），拒绝重复启动。
        if (File.Exists(Path.Combine(taskDirectory, DeepSeekHarnessRunner.TaskLeaseFileName)))
            return Task.FromResult(new HarnessSupervisorStartResult(true, taskId, taskDirectory, null, null, RunningState,
                "检测到任务目录存在活跃 Runner 租约（可能有非受管 Runner 正在执行）；未重复启动、未重复提交，请等待其结束后再启动或直接对账该任务。"));

        // 全新受管启动：先启动独立受控子进程（传统同步模式：真正提交并在进程内等待真实终态），
        // 随即在返回前原子写入监督记录（含 PID 与启动 UTC），确保任意后续进程可按同一记录接回。
        // 说明：PID 在子进程启动前不可知，因此记录在启动后立即原子落盘、绝不推迟到返回之后。
        var spawned = (SpawnRunner ?? DefaultSpawn)(new HarnessSupervisorSpawnArguments(projectRoot, taskDirectory));
        var pid = spawned.Id;
        DateTime runnerStartedUtc;
        try { runnerStartedUtc = spawned.StartTimeUtc; }
        catch { runnerStartedUtc = DateTime.UtcNow; }
        if (spawned is IDisposable spawnedDisposable) spawnedDisposable.Dispose();

        var fingerprint = DeepSeekHarnessRunner.ComputeContractFingerprint(taskDirectory);
        var now = DateTime.UtcNow;
        var fresh = new HarnessSupervisorRecord(RecordSchemaVersion, taskId, projectRoot, taskDirectory, fingerprint,
            pid, runnerStartedUtc, now, now, RunningState);
        WriteRecord(fresh);
        return Task.FromResult(new HarnessSupervisorStartResult(false, taskId, taskDirectory, pid, now, RunningState,
            $"已在后台启动受管 Runner（PID {pid}，监督状态 running）。start 返回不代表任务完成；可重连标识（taskId）：{taskId}。请用 -Mode await（同两个绝对路径）接回等待真实终态，或 -Mode status 查看。本地等待不消耗 Codex token，DSH 模型用量独立。"));
    }

    /// <summary>生产默认：启动与当前进程同路径的可执行文件（CodexHelper.HarnessRunner.exe）的传统同步模式。</summary>
    private static IHarnessSupervisedProcess DefaultSpawn(HarnessSupervisorSpawnArguments arguments)
    {
        var fileName = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            throw new InvalidOperationException("无法定位受管 Runner 可执行文件（Environment.ProcessPath 缺失），无法启动子进程。");
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-ProjectRoot");
        startInfo.ArgumentList.Add(arguments.ProjectRoot);
        startInfo.ArgumentList.Add("-TaskDirectory");
        startInfo.ArgumentList.Add(arguments.TaskDirectory);
        var process = Process.Start(startInfo);
        if (process is null) throw new InvalidOperationException("受管 Runner 子进程启动失败。");
        return new RealManagedProcess(process);
    }

    /// <summary>生产默认：按 PID + 启动 UTC 双重身份接回既有受管进程；进程不存在或身份不符返回 null（绝不误接被重用 PID）。</summary>
    private static IHarnessSupervisedProcess? DefaultAttach(int pid, DateTime recordRunnerStartedUtc)
    {
        if (pid <= 0) return null;
        Process? process = null;
        try { process = Process.GetProcessById(pid); }
        catch { return null; }
        DateTime osStartedUtc;
        try { osStartedUtc = process.StartTime.ToUniversalTime(); }
        catch { process.Dispose(); return null; }
        if ((osStartedUtc - recordRunnerStartedUtc).Duration() > IdentityTolerance)
        {
            process.Dispose();
            return null; // PID 已被重用（启动时间不符），绝不把无关进程当作受管 Runner。
        }
        return new RealManagedProcess(process);
    }

    // ---- await ----

    /// <summary>
    /// 按监督协议等待真实终态：读取同一监督记录，只基于"子 Runner 已退出 + HARNESS_STATUS.json 非 running +
    /// 已有报告门禁结果"返回结论；绝不提交新合同。新进程/中断后仍可对存活的受管 PID 接管等待；
    /// 受管 PID 已消失而真相仍运行/启动中时执行注入的现有安全对账，仍无法确认真实终态则明确写出
    /// uncertain/failed，绝不虚报 completed。外部取消（调用方中断）原样上抛且不写任何终态。
    /// </summary>
    public async Task<HarnessSupervisorAwaitResult> AwaitAsync(string projectRoot, string taskDirectory, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        var record = TryReadRecord(taskDirectory);
        if (record is null)
            return new HarnessSupervisorAwaitResult(OutcomeUncertain,
                $"任务目录缺少监督记录（{RecordFileName}），无法按监督协议接回；请先确认该任务由 -Mode start 启动，或直接用任务中心对该任务执行安全对账。");

        // 终态可重连读取：监督记录已到终态时，读取任务真相与门禁结果后立即返回既有结论（不等待、不重复提交）。
        if (record.IsTerminal)
            return ReconnectedResult(taskDirectory, record, record.TerminalOutcome ?? OutcomeUncertain);

        // 受管 PID 仍存活且身份相符 → 接管等待其退出（不做任何 DSH 读取）。
        var handle = Attach(record.RunnerPid, record.RunnerStartedUtc);
        if (handle is not null)
        {
            WriteRecord(record with { LastVerifiedUtc = DateTime.UtcNow });
            try
            {
                await handle.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                if (handle is IDisposable disposable) disposable.Dispose();
            }
        }

        // 子 Runner 已退出（或 PID 已消失）：以任务目录真相 + 报告门禁给出结论。
        return await ResolveAfterChildGone(taskDirectory, record, cancellationToken);
    }

    private async Task<HarnessSupervisorAwaitResult> ResolveAfterChildGone(string taskDirectory, HarnessSupervisorRecord record, CancellationToken cancellationToken)
    {
        var truth = TryReadTaskStatus(taskDirectory);
        if (truth is not null && !truth.IsRunning)
        {
            var (outcome, message) = DeriveTerminal(truth, taskDirectory);
            WriteRecord(MarkTerminal(record, outcome, record.RunnerExitCode));
            return new HarnessSupervisorAwaitResult(outcome, message, truth);
        }

        // 受管 PID 已消失但真相状态仍是 running/starting：绝不虚报 completed。
        // 先执行注入的现有安全对账（如 Runner 的 Host 会话对账）；对账后仍非终态则明确写出 uncertain。
        var stateBefore = truth?.State ?? "状态文件缺失";
        if (ReconcileAsync is not null)
        {
            try
            {
                await ReconcileAsync(cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 对账失败（Host 不可达等）不阻断：继续按不确定结论处理并说明。
            }
            var reconciled = TryReadTaskStatus(taskDirectory);
            if (reconciled is not null && !reconciled.IsRunning)
            {
                var (outcome, message) = DeriveTerminal(reconciled, taskDirectory);
                WriteRecord(MarkTerminal(record, outcome, record.RunnerExitCode));
                return new HarnessSupervisorAwaitResult(outcome, message, reconciled);
            }
            truth = reconciled;
        }
        var messageText = $"受管 Runner 已退出/消失，但任务真相状态仍为 {stateBefore}{(truth is null ? "" : "（对账后仍非真实终态）")}；已执行现有安全对账仍无法确认真实终态，任务未完成：请用任务中心对该任务执行对账/停止，或人工核验 Harness 会话后再判定。绝不虚报 completed。";
        WriteRecord(MarkTerminal(record, OutcomeUncertain, record.RunnerExitCode));
        return new HarnessSupervisorAwaitResult(OutcomeUncertain, messageText, truth);
    }

    /// <summary>
    /// 终态记录重连读取（阶段二收紧）：每次对外报告完成都必须由【当前】子 Runner 已退出（监督记录终态）
    /// +【当前】HARNESS_STATUS.json 非 running +【当前】EXECUTION_REPORT.md 门禁通过共同确认，绝不沿用旧 completed：
    /// <list type="bullet">
    /// <item>当前真相为可读非运行终态：以当前真相 + 当前报告门禁复算结论（completed 需门禁通过；cancelled/failed 保持真实结论，
    /// 旧记录不得覆盖它）；复算结论与旧记录不一致时把监督记录同步为新结论，避免旧 completed 残留；</item>
    /// <item>当前真相缺失/损坏或仍为 running/starting/busy：若旧记录为 completed，绝不可返回旧 completed，
    /// 返回明确 uncertain（摘要说明需要安全对账，退出码非 0）并把记录同步为 uncertain；</item>
    /// <item>旧记录为 cancelled/failed/uncertain（非完成结论）且当前真相无更新终态时，保留真实非完成结论并说明当前真相，
    /// 绝不因记录存在而虚报完成。</item>
    /// </list>
    /// </summary>
    private HarnessSupervisorAwaitResult ReconnectedResult(string taskDirectory, HarnessSupervisorRecord record, string recordedOutcome)
    {
        var truth = TryReadTaskStatus(taskDirectory);
        if (truth is not null && !truth.IsRunning)
        {
            // 当前真相为可读终态：以当前真相 + 当前报告门禁复算结论（门禁在 DeriveTerminal 内强制执行）。
            var (outcome, message) = DeriveTerminal(truth, taskDirectory);
            // 复算结论与旧记录不一致时同步监督记录（如旧 completed 不得覆盖当前 cancelled/failed；报告修复后失败也可升级为完成）。
            if (!string.Equals(outcome, recordedOutcome, StringComparison.OrdinalIgnoreCase))
                WriteRecord(MarkTerminal(record, outcome, record.RunnerExitCode));
            return new HarnessSupervisorAwaitResult(outcome, "监督终态重连读取：" + message, truth, ReconnectedTerminal: true);
        }

        // 当前真相缺失/损坏或仍为 running/starting/busy：绝不能沿用旧 completed——完成必须由当前真相 + 当前报告门禁重新确认。
        if (string.Equals(recordedOutcome, OutcomeCompleted, StringComparison.OrdinalIgnoreCase))
        {
            var reason = truth is null
                ? "任务目录真相源（HARNESS_STATUS.json）缺失或损坏"
                : $"任务真相状态仍为 {truth.State}（非真实终态）";
            var message = $"监督记录曾标记完成，但{reason}，无法由当前真相与报告门禁重新确认完成；请用任务中心对该任务执行安全对账后再判定，绝不虚报 completed。";
            WriteRecord(MarkTerminal(record, OutcomeUncertain, record.RunnerExitCode)); // 清除旧 completed，避免后续任何读取再复现。
            return new HarnessSupervisorAwaitResult(OutcomeUncertain, "监督终态重连读取：" + message, truth, ReconnectedTerminal: true);
        }

        // 非完成结论（cancelled/failed/uncertain）与当前真相无更新终态：保留真实非完成结论并如实说明当前真相（不虚报成功）。
        var stateText = truth?.State ?? "状态文件缺失";
        return new HarnessSupervisorAwaitResult(recordedOutcome,
            "监督记录已到终态（" + OutcomeText(recordedOutcome) + "）；任务真相无更新终态（" + stateText + "）。", truth, ReconnectedTerminal: true);
    }

    // ---- status ----

    /// <summary>固定大小、脱敏的状态快照：只读监督记录 + 任务目录真相源，绝不读取 session.history/DSH 文本。</summary>
    public HarnessSupervisorStatusSnapshot Status(string projectRoot, string taskDirectory)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        var taskId = Path.GetFileName(taskDirectory);
        var record = TryReadRecord(taskDirectory);
        var truth = TryReadTaskStatus(taskDirectory);
        if (record is null)
            return new HarnessSupervisorStatusSnapshot(false, taskId, taskDirectory, null, null,
                truth?.IsRunning ?? false, truth?.State, null, null, null,
                "任务目录缺少监督记录；该任务可能不是由 -Mode start 启动。任务真相状态：" + (truth?.State ?? "状态文件缺失") + "。");
        var now = DateTime.UtcNow;
        WriteRecord(record with { LastVerifiedUtc = now });
        return new HarnessSupervisorStatusSnapshot(true, taskId, taskDirectory, record.SupervisionState,
            record.TerminalOutcome, truth?.IsRunning ?? false, truth?.State, record.RunnerPid, record.StartedUtc, now,
            $"监督状态：{OutcomeText(record.SupervisionState)}；任务真相状态：{truth?.State ?? "状态文件缺失"}；受管 Runner PID：{record.RunnerPid}。");
    }

    // ---- 记录读写 ----

    /// <summary>宽容读取监督记录；缺失/损坏返回 null。</summary>
    public static HarnessSupervisorRecord? TryReadRecord(string taskDirectory)
    {
        var path = RecordPath(taskDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<HarnessSupervisorRecord>(text, RecordOptions);
        }
        catch { return null; }
    }

    /// <summary>原子写入监督记录（UTF-8 无 BOM；失败上抛，绝不静默丢记录）。</summary>
    public static void WriteRecord(HarnessSupervisorRecord record)
        => AtomicFile.WriteAllText(RecordPath(record.TaskDirectory), JsonSerializer.Serialize(record, RecordOptions));

    /// <summary>监督记录置终态（记录保留供审计；仅更新状态字段与核验时间）。</summary>
    private static HarnessSupervisorRecord MarkTerminal(HarnessSupervisorRecord record, string outcome, int? runnerExitCode)
        => record with
        {
            SupervisionState = TerminalState,
            TerminalOutcome = outcome,
            RunnerExitCode = runnerExitCode,
            LastVerifiedUtc = DateTime.UtcNow
        };

    // ---- 真相源/终态推导 ----

    /// <summary>宽容读取任务目录真相源 HARNESS_STATUS.json（与 HarnessTaskStateStore 同构容忍旧日期/损坏）。</summary>
    private static HarnessTaskStatus? TryReadTaskStatus(string taskDirectory)
    {
        var path = Path.Combine(taskDirectory, HarnessTaskStateStore.StatusFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new HarnessUtcConverter() }
            };
            return JsonSerializer.Deserialize<HarnessTaskStatus>(text, options);
        }
        catch { return null; }
    }

    /// <summary>
    /// 终态结论：completed/awaiting-gpt 只在与合同指纹匹配、报告晚于任务开始且结构完整（报告门禁通过）时
    /// 才成功；报告门禁失败 → failed。cancelled → cancelled；failed/未知 → failed。绝不把进行中/未知映射为成功。
    /// </summary>
    private static (string Outcome, string Message) DeriveTerminal(HarnessTaskStatus truth, string taskDirectory)
    {
        var state = (truth.State ?? string.Empty).ToLowerInvariant();
        if (state is "completed" or "awaiting-gpt")
        {
            var validation = HarnessExecutionReportValidator.Validate(taskDirectory, truth.TaskId, truth.ContractFingerprint, truth.StartedUtc);
            if (!validation.Valid)
                return (OutcomeFailed, "任务真相为可验收候选，但 EXECUTION_REPORT.md 未通过完成门禁（" + validation.Reason + "），任务未完成，等待 GPT 接管。");
            return (OutcomeCompleted, "受管 Runner 已退出，任务已到真实终态（" + state + "）且报告门禁通过，等待 GPT 独立验收。");
        }
        if (state == "cancelled")
            return (OutcomeCancelled, "任务已取消。");
        return (OutcomeFailed, state == "failed"
            ? "任务真相为失败终态。"
            : $"任务真相状态 {state} 不是可信终态，按失败处理（绝不虚报完成）。");
    }

    private static string OutcomeText(string? outcome)
        => (outcome ?? string.Empty).ToLowerInvariant() switch
        {
            OutcomeCompleted => "已完成（awaiting-gpt/报告门禁通过）",
            OutcomeCancelled => "已取消",
            OutcomeFailed => "失败",
            OutcomeUncertain => "不确定（需对账）",
            "running" => "运行中（受管 Runner 存活）",
            "terminal" => "已到监督终态",
            _ => outcome ?? string.Empty
        };

    /// <summary>真实 <see cref="Process"/> 的受控句柄封装（spawn 与 attach 共用）。</summary>
    private sealed class RealManagedProcess : IHarnessSupervisedProcess
    {
        private readonly Process process;

        internal RealManagedProcess(Process process)
        {
            this.process = process;
            try { StartTimeUtc = process.StartTime.ToUniversalTime(); }
            catch { StartTimeUtc = DateTime.UtcNow; }
        }

        public int Id => process.Id;
        public DateTime StartTimeUtc { get; }
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    }

    private IHarnessSupervisedProcess? Attach(int pid, DateTime runnerStartedUtc)
        => AttachRunner is not null ? AttachRunner(pid, runnerStartedUtc) : DefaultAttach(pid, runnerStartedUtc);
}
