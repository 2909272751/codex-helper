using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 验收排队决策。Outcome 取值见 <see cref="HarnessAcceptanceCoordinator"/> 常量：
/// queued / already-queued / accepted / needs-repair / acceptance-failed / task-cancelled / task-failed / not-eligible。
/// Record 为决策后落盘的验收记录（接回既有记录时为既有记录，未落盘时为 null）。
/// </summary>
public sealed record HarnessAcceptanceQueueDecision(
    string Outcome,
    string Message,
    string TaskId,
    string TaskDirectory,
    HarnessAcceptanceRecord? Record);

/// <summary>
/// 本地自动验收编排协调器（阶段一）：只为每个任务目录把“一次独立验收”持久化、去重地排队。
/// 资格只来自现有 Harness Supervisor 监督记录、任务真相（HARNESS_STATUS.json）与
/// EXECUTION_REPORT.md 完成门禁：running/starting/busy、真相缺失、报告未过门禁、受管 Runner 仍存活，
/// 一律不排队、不启动。本阶段绝不重投 DSH 合同、绝不实际调用验收模型。
/// 确认资格后只把绝对项目根、任务目录与验收记录路径交给注入式 <see cref="IHarnessAcceptanceLauncher"/>
/// （阶段一用测试替身；生产默认不接线，只落盘 acceptance-queued 供阶段二驱动接手）。
/// 相同 taskId+合同指纹的并发/重复文件事件/重启读取同一记录只能启动一次；启动器异常落盘
/// acceptance-failed 可诊断摘要，绝不把失败伪装成 accepted。
/// </summary>
public sealed class HarnessAcceptanceCoordinator
{
    /// <summary>已为本任务新排队（无启动器）或排队并完成（含启动器结果）。</summary>
    public const string OutcomeQueued = "queued";
    /// <summary>任务目录已有验收队列记录（排队中/运行中），未重复排队。</summary>
    public const string OutcomeAlreadyQueued = "already-queued";
    /// <summary>独立验收已通过（终态 accepted）。</summary>
    public const string OutcomeAccepted = "accepted";
    /// <summary>独立验收结论：需修复（终态 needs-repair）。</summary>
    public const string OutcomeNeedsRepair = "needs-repair";
    /// <summary>验收启动失败（终态 acceptance-failed，可诊断）。</summary>
    public const string OutcomeAcceptanceFailed = "acceptance-failed";
    /// <summary>任务真相为取消终态（不排队验收）。</summary>
    public const string OutcomeTaskCancelled = "task-cancelled";
    /// <summary>任务真相失败/报告未过门禁/状态矛盾（不排队验收，等待 GPT 或人工）。</summary>
    public const string OutcomeTaskFailed = "task-failed";
    /// <summary>暂不具备排队条件（真相运行中/Runner 存活等），守候应继续等待。</summary>
    public const string OutcomeNotEligible = "not-eligible";

    /// <summary>受管 Runner PID + 启动 UTC 双重身份接回（与监督器同一语义）；测试注入受控实现。</summary>
    public Func<int, DateTime, IHarnessSupervisedProcess?> AttachRunner { get; init; } = DefaultAttachRunner;

    /// <summary>注入式验收启动器；阶段一生产不接线（null = 只落盘 acceptance-queued，由阶段二验收驱动接手）。</summary>
    public IHarnessAcceptanceLauncher? Launcher { get; init; }

    /// <summary>受管进程启动身份允许的最大时间偏差（PID 重用防护，与 HarnessSupervisor 一致）。</summary>
    private static readonly TimeSpan IdentityTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 为单个任务目录执行一次“去重排队”。可多次/多进程调用：
    /// 任务目录已有验收记录时只接回（并发/重复文件事件/重启读取同一记录的唯一入口），绝不重复排队或启动。
    /// </summary>
    public async Task<HarnessAcceptanceQueueDecision> QueueOnceAsync(string projectRoot, string taskDirectory, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        var runsRoot = Path.GetFullPath(Path.Combine(projectRoot, ".codex-helper", "runs"));
        if (!PathSafety.IsWithin(taskDirectory, runsRoot))
            throw new InvalidOperationException("验收编排只接受属于项目 .codex-helper/runs 的绝对任务目录。");
        var taskId = Path.GetFileName(taskDirectory);

        // 既有验收记录 → 只接回（含并发/重复事件/重启读取同一记录），绝不重复排队或启动。
        var existing = HarnessAcceptanceQueue.TryReadRecord(taskDirectory);
        if (existing is not null)
            return AttachToRecord(existing, taskId, taskDirectory);

        var truth = TryReadTruth(taskDirectory);
        if (truth is null)
            return new(OutcomeNotEligible, "任务真相源 HARNESS_STATUS.json 缺失或损坏，尚不具备验收排队条件；继续本地守候。", taskId, taskDirectory, null);
        var state = (truth.State ?? string.Empty).ToLowerInvariant();
        if (state is "running" or "starting" or "busy")
            return new(OutcomeNotEligible, "任务真相状态为 " + state + "（未到真实终态），不排队验收；继续本地守候。", taskId, taskDirectory, null);
        if (state == "cancelled")
            return new(OutcomeTaskCancelled, "任务真相为取消终态，不排队独立验收（保留待 GPT 处理）。", taskId, taskDirectory, null);

        // 只有完成候选（completed/awaiting-gpt，报告门禁通过）才可排队独立验收。
        if (state is not ("completed" or "awaiting-gpt"))
            return new(OutcomeTaskFailed, "任务真相状态 " + state + " 不是可信完成终态，不排队独立验收（保守按失败处理）。", taskId, taskDirectory, null);

        var fingerprint = string.IsNullOrWhiteSpace(truth.ContractFingerprint) ? null : truth.ContractFingerprint;
        if (fingerprint is null)
            return new(OutcomeTaskFailed, "任务真相缺少合同指纹，无法为验收记录归属合同，不排队独立验收。", taskId, taskDirectory, null);

        // 受管 Runner 仍存活（或监督记录与当前合同不一致/非完成结论）一律不排队。
        var supervisor = HarnessSupervisor.TryReadRecord(taskDirectory);
        if (supervisor is not null)
        {
            if (!string.IsNullOrWhiteSpace(supervisor.ContractFingerprint)
                && !string.Equals(supervisor.ContractFingerprint, fingerprint, StringComparison.Ordinal))
                return new(OutcomeTaskFailed, "监督记录合同指纹与当前任务真相不一致，保守不排队验收（需人工核对该任务目录）。", taskId, taskDirectory, null);

            // 证据先行（第六阶段）：受管任务的 Runner 退出证明由监督器写入的可信终态证据承载，
            // 绝不凭"PID 消失"或读取系统进程命令行猜测。监督记录未到终态（含 Runner 刚退出、await
            // 尚未落终态证据）→ 继续等待监督器落终态；已到完成终态但证据缺失/错配/非终态 → 保守失败。
            if (!supervisor.IsTerminal)
            {
                var handle = AttachRunner(supervisor.RunnerPid, supervisor.RunnerStartedUtc);
                if (handle is not null)
                {
                    if (handle is IDisposable disposable) { try { disposable.Dispose(); } catch { /* 忽略释放异常 */ } }
                    return new(OutcomeNotEligible, "受管 Runner 仍存活（PID " + supervisor.RunnerPid + "），不排队验收；继续本地守候其真实终态。", taskId, taskDirectory, null);
                }
                return new(OutcomeNotEligible,
                    "监督记录未到终态（Runner 已消失但 await 尚未落可信终态证据），不排队验收；等待监督器落终态证据后再接回。",
                    taskId, taskDirectory, null);
            }
            if (!string.Equals(supervisor.TerminalOutcome, HarnessSupervisor.OutcomeCompleted, StringComparison.Ordinal))
            {
                // 监督记录已到非完成终态而真相却是完成候选：状态矛盾，保守不排队。
                return new(OutcomeTaskFailed,
                    "监督记录已到非完成结论（" + (supervisor.TerminalOutcome ?? "未知") + "）而任务真相为完成候选，状态矛盾；不排队验收，等待人工核验。",
                    taskId, taskDirectory, null);
            }
            // 监督记录已到完成终态：本地读取链路必须看到监督器同步写入的最小可信终态证据
            // （HARNESS_TERMINAL_EVIDENCE.json）。证据文件尚未落盘（监督器刚写完记录、证据原子写进行中）
            // → 继续等待；证据已存在但缺失/错配/非终态由 Check 判为不可用 → 保守失败，绝不凭猜测排队。
            var evidence = HarnessTerminalEvidenceStore.TryReadRecord(taskDirectory);
            if (evidence is null)
                return new(OutcomeNotEligible,
                    "监督记录为终态完成，但可信终态证据文件尚未落盘（await 正在写入）；等待本地监督器证据出现后再排队。",
                    taskId, taskDirectory, null);
            var check = HarnessTerminalEvidenceStore.Check(taskDirectory, taskId, fingerprint);
            if (!check.Valid)
                return new(OutcomeTaskFailed,
                    "监督记录为终态完成，但可信终态证据" + check.Reason + "；保守不排队独立验收（Runner 退出证明由本地监督器证据承载，不靠读取系统进程命令行）。",
                    taskId, taskDirectory, null);
        }
        else if (File.Exists(Path.Combine(taskDirectory, DeepSeekHarnessRunner.TaskLeaseFileName)))
        {
            // 无监督记录但有 Runner 租约：Runner 可能仍存活（传统同步形态），不排队。
            return new(OutcomeNotEligible, "任务目录存在 Runner 租约占用（Runner 可能仍存活），不排队验收；继续本地守候。", taskId, taskDirectory, null);
        }

        var validation = HarnessExecutionReportValidator.Validate(taskDirectory, truth.TaskId, fingerprint, truth.StartedUtc);
        if (!validation.Valid)
            return new(OutcomeTaskFailed, "EXECUTION_REPORT.md 未通过完成门禁（" + validation.Reason + "），不排队独立验收（等待 GPT 接管）。", taskId, taskDirectory, null);

        // 占位排队（跨进程单飞）：先落盘 acceptance-queued，随后（若配置启动器）转 acceptance-running 并执行。
        var now = DateTime.UtcNow;
        var claimed = HarnessAcceptanceQueue.TryClaimQueued(new HarnessAcceptanceRecord(
            HarnessAcceptanceQueue.RecordSchemaVersion, taskId, projectRoot, taskDirectory, fingerprint,
            HarnessAcceptanceQueue.QueuedState, 1, now, now));
        if (claimed is null)
        {
            var raced = HarnessAcceptanceQueue.TryReadRecord(taskDirectory);
            if (raced is not null)
                return AttachToRecord(raced, taskId, taskDirectory);
            return new(OutcomeTaskFailed, "验收记录已被并发占用但不可读（可能写入中断），保守不重复排队；需要人工检查该任务目录。", taskId, taskDirectory, null);
        }

        if (Launcher is null)
        {
            return new(OutcomeQueued,
                "任务已真实完成（Runner 已退出 + 真相非 running + 报告门禁通过），已为该任务去重排队一次独立验收（HARNESS_ACCEPTANCE.json = acceptance-queued，等待验收驱动执行）。" +
                "本次调用未配置验收驱动，只做本地排队；绝不重投 DSH、等待阶段零 Codex 模型调用。",
                taskId, taskDirectory, claimed);
        }

        // 配置了启动器（阶段二验收驱动/测试替身）：落盘 acceptance-running 后执行，失败保留可诊断失败态。
        // LauncherStartedUtc 取本进程 OS 启动时间（与受管身份校验同一语义：PID + 启动时间防 PID 重用）。
        var running = HarnessAcceptanceQueue.Update(claimed, HarnessAcceptanceQueue.RunningState,
            launcherPid: Environment.ProcessId, launcherStartedUtc: CurrentProcessStartTimeUtc());
        var context = new HarnessAcceptanceLaunchContext(projectRoot, taskDirectory, taskId,
            HarnessAcceptanceQueue.RecordPath(taskDirectory), fingerprint);
        HarnessAcceptanceLauncherResult result;
        try
        {
            result = await Launcher.LaunchAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            HarnessAcceptanceQueue.Update(running, HarnessAcceptanceQueue.FailedState, safeSummary: "验收启动器被取消，未完成。");
            return new(OutcomeAcceptanceFailed, "验收启动器被取消，已记录为 acceptance-failed（可诊断），绝不伪装成功。", taskId, taskDirectory,
                HarnessAcceptanceQueue.TryReadRecord(taskDirectory));
        }
        catch (Exception ex)
        {
            HarnessAcceptanceQueue.Update(running, HarnessAcceptanceQueue.FailedState,
                safeSummary: "验收启动器异常：" + HarnessAcceptanceQueue.Safe(ex.Message));
            return new(OutcomeAcceptanceFailed, "验收启动器执行异常，已记录为 acceptance-failed（可诊断），绝不把任务标为 accepted。", taskId, taskDirectory,
                HarnessAcceptanceQueue.TryReadRecord(taskDirectory));
        }

        var finalState = result.State ?? string.Empty;
        if (string.Equals(finalState, HarnessAcceptanceQueue.AcceptedState, StringComparison.OrdinalIgnoreCase)
            || string.Equals(finalState, HarnessAcceptanceQueue.NeedsRepairState, StringComparison.OrdinalIgnoreCase))
        {
            var final = HarnessAcceptanceQueue.Update(running, finalState, safeSummary: HarnessAcceptanceQueue.Safe(result.SafeSummary));
            var accepted = string.Equals(finalState, HarnessAcceptanceQueue.AcceptedState, StringComparison.OrdinalIgnoreCase);
            return new(accepted ? OutcomeAccepted : OutcomeNeedsRepair,
                accepted ? "独立验收已完成并标记 accepted。" : "独立验收结论为 needs-repair（产品需修复后再验收）。",
                taskId, taskDirectory, final);
        }
        HarnessAcceptanceQueue.Update(running, HarnessAcceptanceQueue.FailedState,
            safeSummary: "验收启动器返回未知状态：" + HarnessAcceptanceQueue.Safe(finalState));
        return new(OutcomeAcceptanceFailed, "验收启动器返回未知状态，已记录为 acceptance-failed（可诊断）。", taskId, taskDirectory,
            HarnessAcceptanceQueue.TryReadRecord(taskDirectory));
    }

    /// <summary>既有验收记录一律只接回不重复排队（并发/重复文件事件/重启读取同一记录的唯一入口）。</summary>
    private HarnessAcceptanceQueueDecision AttachToRecord(HarnessAcceptanceRecord record, string taskId, string taskDirectory)
    {
        var state = record.QueueState;
        var note = string.Empty;
        var truth = TryReadTruth(taskDirectory);
        if (truth is not null && !string.IsNullOrWhiteSpace(truth.ContractFingerprint)
            && !string.IsNullOrWhiteSpace(record.ContractFingerprint)
            && !string.Equals(truth.ContractFingerprint, record.ContractFingerprint, StringComparison.Ordinal))
        {
            note = "（注意：验收记录合同指纹与当前任务真相不一致，属旧合同残留；同一 taskId+指纹只排队一次，不重复排队）";
        }
        if (string.Equals(state, HarnessAcceptanceQueue.AcceptedState, StringComparison.OrdinalIgnoreCase))
            return new(OutcomeAccepted, "该任务已有验收终态记录：独立验收已通过（accepted）。" + note, taskId, taskDirectory, record);
        if (string.Equals(state, HarnessAcceptanceQueue.NeedsRepairState, StringComparison.OrdinalIgnoreCase))
            return new(OutcomeNeedsRepair, "该任务已有验收终态记录：验收结论为 needs-repair（需修复）。" + note, taskId, taskDirectory, record);
        if (string.Equals(state, HarnessAcceptanceQueue.FailedState, StringComparison.OrdinalIgnoreCase))
            return new(OutcomeAcceptanceFailed, "该任务已有验收终态记录：验收失败（acceptance-failed，可诊断，绝不伪装成功）。" + note, taskId, taskDirectory, record);
        if (string.Equals(state, HarnessAcceptanceQueue.RunningState, StringComparison.OrdinalIgnoreCase))
        {
            // 验收运行中：启动器进程仍存活（PID+启动时间身份）→ 接回等待；进程消失/无法确认 → 保守失败（不自动重试，防额度循环）。
            if (record.LauncherPid is { } launcherPid && record.LauncherStartedUtc is { } launcherStarted)
            {
                var handle = AttachRunner(launcherPid, launcherStarted);
                if (handle is not null)
                {
                    if (handle is IDisposable disposable) { try { disposable.Dispose(); } catch { /* 忽略 */ } }
                    return new(OutcomeAlreadyQueued,
                        "独立验收正在运行（PID " + launcherPid + "），未重复排队、未重复启动。" + note,
                        taskId, taskDirectory, record);
                }
            }
            var healed = HarnessAcceptanceQueue.Update(record, HarnessAcceptanceQueue.FailedState,
                safeSummary: "验收运行中进程已消失（PID " + (record.LauncherPid?.ToString() ?? "未知") + "），验收未完成；不自动重试（防额度循环）。");
            return new(OutcomeAcceptanceFailed,
                "检测到验收运行中但启动器进程已消失/无法确认，已保守落盘 acceptance-failed（可诊断，不自动重试，绝不伪装成功）。" + note,
                taskId, taskDirectory, healed);
        }
        // acceptance-queued / awaiting-gpt：已排队或排队中，不再重复排队/启动。
        return new(OutcomeAlreadyQueued,
            "该任务已有验收队列记录（状态 " + state + "），未重复排队、未重复启动。" + note,
            taskId, taskDirectory, record);
    }

    /// <summary>当前进程 OS 启动 UTC（PID 重用身份校验用；读取失败回退 UtcNow）。</summary>
    private static DateTime CurrentProcessStartTimeUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>宽容读取任务目录真相源 HARNESS_STATUS.json（与监督器同一语义）。</summary>
    internal static HarnessTaskStatus? TryReadTruth(string taskDirectory)
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

    /// <summary>生产默认：按 PID + 启动 UTC 双重身份接回受管 Runner；不存在/身份不符/已退出返回 null。</summary>
    private static IHarnessSupervisedProcess? DefaultAttachRunner(int pid, DateTime startedUtc)
    {
        if (pid <= 0) return null;
        Process? process = null;
        try { process = Process.GetProcessById(pid); }
        catch { return null; }
        DateTime osStartedUtc;
        try { osStartedUtc = process.StartTime.ToUniversalTime(); }
        catch { process.Dispose(); return null; }
        if ((osStartedUtc - startedUtc).Duration() > IdentityTolerance)
        {
            process.Dispose();
            return null; // PID 已被重用（启动时间不符），绝不把无关进程当作受管 Runner。
        }
        if (process.HasExited)
        {
            process.Dispose();
            return null;
        }
        return new RealManagedHandle(process);
    }

    /// <summary>真实 <see cref="Process"/> 的受控句柄封装（仅用于身份核验；语义与监督器 RealManagedProcess 一致）。</summary>
    private sealed class RealManagedHandle : IHarnessSupervisedProcess
    {
        private readonly Process process;

        internal RealManagedHandle(Process process)
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
}
