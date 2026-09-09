using System.IO;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 本地验收守候结果：Outcome 复用协调器决策取值（除 not-eligible 外的全部取值均为终局/接回结论），
/// 例如 queued / already-queued / accepted / needs-repair / acceptance-failed / task-cancelled / task-failed。
/// </summary>
public sealed record HarnessAcceptanceWatchResult(
    string Outcome,
    string Message,
    string? QueueState,
    string TaskId,
    string TaskDirectory);

/// <summary>
/// 长期本地验收守候入口（阶段一）：由 CLI -Mode automate 或同等受管后台入口启动。
/// 采用文件变更通知 + 启动时一次安全对账（首轮 QueueOnce 即对账），对丢失通知只做低频、
/// 固定大小的本地状态/PID 复查（绝不轮询 DSH 聊天/事件流/日志或 session.history，绝不产生模型调用）。
/// 任务到真实终态且报告门禁通过时，由协调器为同一 taskId+指纹去重排队一次独立验收；绝不重投 DSH 合同。
/// </summary>
public sealed class HarnessAcceptanceWatcher
{
    /// <summary>本地守候关心的固定大小文件（任务目录内），出现变更即触发一次本地复核。</summary>
    private static readonly string[] RelevantFileNames =
    [
        HarnessTaskStateStore.StatusFileName,
        HarnessSupervisor.RecordFileName,
        HarnessTerminalEvidenceStore.RecordFileName,
        HarnessAcceptanceQueue.RecordFileName,
        HarnessTaskStateStore.ProgressFileName,
        "EXECUTION_REPORT.md"
    ];

    /// <summary>协调器（默认已接入阶段二真实验收驱动 <see cref="CodexAcceptanceLauncher"/>：真实终态后自动执行
    /// 一次独立 Codex 验收；测试/受控场景可注入无启动器或替身启动器的协调器）。</summary>
    public HarnessAcceptanceCoordinator Coordinator { get; init; } = new() { Launcher = new CodexAcceptanceLauncher() };

    /// <summary>失败修复编排协调器（阶段四：needs-repair/acceptance-failed/DSH 失败终态后自动一次 triage
    /// → 串行修复 → 一次复验；每任务每指纹最多一轮，超限/不确定落 needs-user，绝不循环耗额度）。
    /// 生产默认接线 <see cref="HarnessHelperRootResolver.TryResolve"/>：helper-repair 需要可解析的真实
    /// Helper 项目根（源码布局标记识别，拒绝把任意产品项目误认成 Helper），无法解析仍落 needs-user。</summary>
    public HarnessRemediationCoordinator RemediationCoordinator { get; init; } = new()
    {
        HelperProjectRootProvider = HarnessHelperRootResolver.TryResolve
    };

    /// <summary>丢失文件通知后的低频本地复查间隔（生产默认 30 秒；测试可注入缩短）。</summary>
    public TimeSpan RecheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 启动/接回本地验收守候直到任务达成“排队决策”（或任务取消/失败等不再排队的终局）。
    /// 任务仍运行/不具备条件时持续守候；外部取消抛 OperationCanceledException（不写任何终态）。
    /// </summary>
    public async Task<HarnessAcceptanceWatchResult> WatchUntilQueueDecisionAsync(
        string projectRoot,
        string taskDirectory,
        CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        var runsRoot = Path.GetFullPath(Path.Combine(projectRoot, ".codex-helper", "runs"));
        if (!PathSafety.IsWithin(taskDirectory, runsRoot))
            throw new InvalidOperationException("验收守候只接受属于项目 .codex-helper/runs 的绝对任务目录。");
        if (!Directory.Exists(taskDirectory))
            throw new DirectoryNotFoundException("任务目录不存在，无法启动本地验收守候：" + taskDirectory);
        var taskId = Path.GetFileName(taskDirectory);

        var wake = CreateWake();
        using (var watcher = new FileSystemWatcher(taskDirectory))
        {
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.IncludeSubdirectories = false;
            FileSystemEventHandler onChanged = (_, args) =>
            {
                if (IsRelevant(args.Name)) wake.TrySetResult(true);
            };
            RenamedEventHandler onRenamed = (_, args) =>
            {
                if (IsRelevant(args.Name)) wake.TrySetResult(true);
            };
            watcher.Changed += onChanged;
            watcher.Created += onChanged;
            watcher.Deleted += onChanged;
            watcher.Renamed += onRenamed;
            watcher.EnableRaisingEvents = true;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // 启动一次安全对账 + 每轮文件事件后的本地复核：只读固定大小的本地状态/PID 文件。
                    var decision = await Coordinator.QueueOnceAsync(projectRoot, taskDirectory, cancellationToken);
                    if (!string.Equals(decision.Outcome, HarnessAcceptanceCoordinator.OutcomeNotEligible, StringComparison.OrdinalIgnoreCase))
                    {
                        // 验收失败终态（needs-repair / acceptance-failed / DSH 失败）自动进入一次修复编排
                        // （阶段四：串行 triage → 可选修复 DSH → 一次复验；已有 remediation 记录只接回不第二轮）。
                        if (ShouldRemediate(decision.Outcome))
                        {
                            var remediation = await RemediationCoordinator.TryRemediationOnceAsync(projectRoot, taskDirectory, cancellationToken);
                            return new HarnessAcceptanceWatchResult(remediation.Outcome, remediation.Message,
                                remediation.Record?.State, remediation.TaskId, remediation.TaskDirectory);
                        }
                        return new HarnessAcceptanceWatchResult(decision.Outcome, decision.Message, decision.Record?.QueueState,
                            decision.TaskId, decision.TaskDirectory);
                    }
                    // 尚不具备排队条件：等待文件变更通知（等待期间的事件写入 wake）或低频固定大小
                    // 本地复查兜底（不轮询 DSH/日志/历史）；wake 在等待结束后才轮换，事件立即唤醒。
                    var pending = wake.Task;
                    await Task.WhenAny(pending, Task.Delay(RecheckInterval, cancellationToken));
                    wake = CreateWake();
                }
            }
            finally
            {
                watcher.EnableRaisingEvents = false;
            }
        }
    }

    private static TaskCompletionSource<bool> CreateWake()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsRelevant(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && RelevantFileNames.Any(relevant => string.Equals(relevant, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>需进入阶段四修复编排的验收终态（每任务每指纹最多一轮；协调器内部做单飞/容量上限）。</summary>
    private static bool ShouldRemediate(string outcome)
        => string.Equals(outcome, HarnessAcceptanceCoordinator.OutcomeNeedsRepair, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessAcceptanceCoordinator.OutcomeAcceptanceFailed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessAcceptanceCoordinator.OutcomeTaskFailed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessAcceptanceCoordinator.OutcomeTaskCancelled, StringComparison.OrdinalIgnoreCase);
}
