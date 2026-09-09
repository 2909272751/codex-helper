using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// Harness Runner 可测试核心逻辑：命令行参数解析、退出码映射与中文摘要构建。
/// 只接受绝对 ProjectRoot 与 TaskDirectory；任务正文绝不进入命令行参数，
/// 摘要只输出状态与定位信息，并脱敏可能的凭据片段。控制台入口 Program.cs
/// 只做薄壳：解析参数 → 调用 <see cref="DeepSeekHarnessRunner.StartAsync"/> → 输出摘要并按退出码退出。
/// </summary>
public static class HarnessRunnerCli
{
    /// <summary>任务完成（Harness 会话 completed）。</summary>
    public const int ExitCompleted = 0;
    /// <summary>任务失败（failed / 未知终态 / 未处理异常）。</summary>
    public const int ExitFailed = 1;
    /// <summary>任务取消（cancelled / 用户取消传播）。</summary>
    public const int ExitCancelled = 2;
    /// <summary>参数错误（缺失、相对路径、越界、缺 SPEC.md、参数过多、未知监督模式）。</summary>
    public const int ExitUsageError = 3;

    /// <summary>监督模式：start（后台启动受管 Runner 并立即返回句柄）。</summary>
    public const string ModeStart = "start";
    /// <summary>监督模式：await（按监督记录接回并等待真实终态，不提交新合同）。</summary>
    public const string ModeAwait = "await";
    /// <summary>监督模式：status（固定大小脱敏摘要，不读 DSH 文本）。</summary>
    public const string ModeStatus = "status";
    /// <summary>监督模式：automate（本地验收守候 + 阶段二真实验收驱动：真实终态后执行一次独立 Codex 验收，绝不重投 DSH）。</summary>
    public const string ModeAutomate = "automate";

    /// <summary>参数解析结果；Error 非空时不可执行。Mode 为空表示旧调用形态（同步真实终态等待）。</summary>
    public sealed record RunnerArguments(string? ProjectRoot, string? TaskDirectory, string? Error, string? Mode = null);

    public const string UsageText =
        "用法：\n" +
        "  同步（旧形态，等待真实终态）：CodexHelper.HarnessRunner.exe -ProjectRoot <绝对项目根目录> -TaskDirectory <绝对任务目录>\n" +
        "  监督模式：CodexHelper.HarnessRunner.exe -Mode <start|await|status|automate> -ProjectRoot <绝对项目根目录> -TaskDirectory <绝对任务目录>\n" +
        "    start  校验两绝对路径与合同后，后台启动一个独立受控 Runner 子进程，先写监督记录并只返回安全摘要与可重连标识；start 返回绝不代表任务完成。\n" +
        "           成功后自动、单飞拉起独立验收守候（HARNESS_ACCEPTANCE_WATCH.json；守候期零 Codex 模型调用）；拉起失败不影响 DSH，但 start 摘要会明确标注“自动验收守候未启动”，绝不假称启用。\n" +
        "    await  按任务目录读取同一监督记录，接回等待真实终态（子 Runner 退出 + HARNESS_STATUS.json 非 running + 报告门禁），不提交新合同；\n" +
        "           可重连：受管 Runner 仍存活时任意新进程都可按同一任务目录接回等待，本地等待不消耗 Codex token，DSH 模型用量独立。\n" +
        "    status 只输出固定大小脱敏摘要（监督状态/PID/真相状态/核验时间），不读取 DSH 聊天或 session.history。\n" +
        "    automate 本地自动验收守候 + 独立 Codex 验收驱动（阶段二）：守候只启动/接回（不重投 DSH 合同），在 Runner 已退出 +\n" +
        "           HARNESS_STATUS.json 非 running + EXECUTION_REPORT.md 通过完成门禁后，为该任务去重排队（HARNESS_ACCEPTANCE.json：\n" +
        "           acceptance-queued → acceptance-running → accepted/needs-repair/acceptance-failed）并默认执行一次独立 Codex 验收\n" +
        "           （codex app-server 独立线程，只收绝对任务目录/项目根/任务 ID/指纹与固定指令；以 GPT_ACCEPTANCE.json 匹配账本判定）。\n" +
        "           等待阶段零 Codex 模型调用：仅真实 DSH 完成后的一次独立验收会消耗 Codex 用量；accepted/needs-repair/acceptance-failed\n" +
        "           后永不自动重试；需修复不与 DSH 并发写项目。守候只用文件变更通知 + 启动一次对账 + 低频固定大小本地复查，\n" +
        "           不轮询 DSH/日志/session.history。automate 退出码：0=已排队/接回/独立验收通过(accepted)，1=失败/不确定/需修复，2=任务取消。\n" +
        "退出码：0=完成（awaiting-gpt 且报告门禁通过/completed），1=失败/不确定/中断，2=取消，3=参数错误。任务正文只从任务目录文件读取，绝不进入命令行。";

    /// <summary>
    /// 解析并校验命令行参数：支持 -ProjectRoot/-TaskDirectory（不区分大小写，也接受
    /// --project-root/--task-directory）与两个位置参数两种形式；监督模式经 -Mode/--mode
    /// 显式指定（start/await/status），未指定时保持旧同步调用形态。所有形态下两个路径都
    /// 必须是绝对路径、任务目录必须位于项目根目录内、任务目录必须存在 SPEC.md。
    /// 任何一步失败都返回 Error（中文），绝不开始执行。
    /// </summary>
    public static RunnerArguments Parse(string[] args)
    {
        var projectRoot = (string?)null;
        var taskDirectory = (string?)null;
        var mode = (string?)null;
        var positional = new List<string>();
        for (var i = 0; i < (args?.Length ?? 0); i++)
        {
            var arg = args![i];
            if (string.Equals(arg, "-ProjectRoot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--project-root", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return new(null, null, "缺少 -ProjectRoot 的参数值。");
                projectRoot = args[++i];
            }
            else if (string.Equals(arg, "-TaskDirectory", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(arg, "--task-directory", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return new(null, null, "缺少 -TaskDirectory 的参数值。");
                taskDirectory = args[++i];
            }
            else if (string.Equals(arg, "-Mode", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return new(null, null, "缺少 -Mode 的参数值（start/await/status）。");
                mode = args[++i];
            }
            else positional.Add(arg);
        }

        if (positional.Count == 1) projectRoot ??= positional[0];
        else if (positional.Count == 2) { projectRoot ??= positional[0]; taskDirectory ??= positional[1]; }
        else if (positional.Count > 2) return new(null, null, "参数过多：最多接受项目根目录与任务目录两个路径。");

        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(taskDirectory))
            return new(null, null, "必须提供绝对的项目根目录与任务目录（-ProjectRoot <路径> -TaskDirectory <路径>）。");
        projectRoot = projectRoot.Trim();
        taskDirectory = taskDirectory.Trim();
        if (!Path.IsPathRooted(projectRoot) || !Path.IsPathRooted(taskDirectory))
            return new(null, null, "项目根目录与任务目录都必须是绝对路径。");
        try
        {
            projectRoot = Path.GetFullPath(projectRoot);
            taskDirectory = Path.GetFullPath(taskDirectory);
        }
        catch (Exception ex)
        {
            return new(null, null, "路径无效：" + ex.Message);
        }
        if (!PathSafety.IsWithin(taskDirectory, projectRoot))
            return new(null, null, "任务目录必须位于项目根目录内。");
        if (!File.Exists(Path.Combine(taskDirectory, "SPEC.md")))
            return new(null, null, "任务目录缺少 SPEC.md，任务正文只从文件读取。");
        if (mode is not null)
        {
            mode = mode.Trim().ToLowerInvariant();
            if (mode is not (ModeStart or ModeAwait or ModeStatus or ModeAutomate))
                return new(null, null, $"未知监督模式：{mode}。仅支持 start/await/status/automate。");
        }
        return new(projectRoot, taskDirectory, null, mode);
    }

    /// <summary>
    /// 把任务状态映射为退出码。真实终态：completed/awaiting-gpt（已通过完成门禁，等待 GPT 验收）→0、
    /// cancelled→2；一切非终态（starting/running/busy，任务尚未真实结束）与失败态（failed/未知）→1。
    /// 上层必须只在 Runner 返回真实终态后才判定任务结束：running/busy/starting 绝不映射为成功。
    /// </summary>
    public static int MapExitCode(string state)
        => string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "awaiting-gpt", StringComparison.OrdinalIgnoreCase) ? ExitCompleted
         : string.Equals(state, "cancelled", StringComparison.OrdinalIgnoreCase) ? ExitCancelled
         : ExitFailed;

    /// <summary>
    /// 构建中文摘要：只含任务 ID、状态中文文案、脱敏消息与两个定位路径；
    /// 不包含任务正文（SPEC/HANDOFF 内容）、凭据或会话密钥。消息经
    /// <see cref="ReasonixIntegrationService.RedactSecrets"/> 脱敏。
    /// </summary>
    public static string BuildSummary(HarnessTaskStatus status)
    {
        var stateText = (status.State ?? string.Empty).ToLowerInvariant() switch
        {
            "awaiting-gpt" => "等待 GPT 验收",
            "completed" => "已完成",
            "cancelled" => "已取消",
            "failed" => "失败",
            "running" => "运行中",
            "starting" => "启动中",
            "busy" => "项目忙",
            _ => status.State ?? string.Empty
        };
        var message = ReasonixIntegrationService.RedactSecrets(status.Message ?? string.Empty);
        return $"任务：{status.TaskId}\n状态：{stateText}\n说明：{message}\n项目根：{status.ProjectRoot}\n任务目录：{status.TaskDirectory}";
    }

    // ---- 持久 Harness 等待监督协议（阶段一）的摘要与退出码映射 ----

    /// <summary>
    /// 监督结论 → 退出码。completed（awaiting-gpt 且报告门禁通过）→ 0；cancelled → 2；
    /// failed / uncertain（PID 消失且真相仍运行、无法确认真实终态）→ 1，绝不把不确定映射为成功。
    /// </summary>
    public static int MapSupervisorOutcomeExitCode(string outcome)
        => string.Equals(outcome, HarnessSupervisor.OutcomeCompleted, StringComparison.OrdinalIgnoreCase) ? ExitCompleted
         : string.Equals(outcome, HarnessSupervisor.OutcomeCancelled, StringComparison.OrdinalIgnoreCase) ? ExitCancelled
         : ExitFailed;

    /// <summary>监督 start 摘要：安全摘要 + 可重连标识；start 返回绝不表述为任务完成。</summary>
    public static string BuildSupervisorStartSummary(HarnessSupervisorStartResult result)
        => $"任务：{result.TaskId}\n可重连标识（taskId）：{result.TaskId}\n监督状态：{SupervisorStateText(result.SupervisionState)}\n受管 Runner PID：{(result.RunnerPid is null ? "无" : result.RunnerPid.ToString())}\n说明：{ReasonixIntegrationService.RedactSecrets(result.Message)}";

    /// <summary>监督 await 摘要：结论 + 任务真相摘要（若可读），绝不输出合同正文/凭据。</summary>
    public static string BuildSupervisorAwaitSummary(HarnessSupervisorAwaitResult result)
    {
        var text = $"监督等待结论：{SupervisorStateText(result.Outcome)}\n说明：{ReasonixIntegrationService.RedactSecrets(result.Message)}";
        return result.TaskStatus is not null ? text + "\n" + BuildSummary(result.TaskStatus) : text;
    }

    /// <summary>监督 status 摘要：固定大小、脱敏；只含定位/状态/PID/核验时间，绝不读取 DSH 文本。</summary>
    public static string BuildSupervisorStatusSummary(HarnessSupervisorStatusSnapshot snapshot)
        => $"任务：{snapshot.TaskId}\n监督记录：{(snapshot.Found ? "存在" : "不存在")}\n" +
           $"监督状态：{(snapshot.SupervisionState is null ? "无" : SupervisorStateText(snapshot.SupervisionState))}\n" +
           $"任务真相状态：{snapshot.TaskState ?? "状态文件缺失"}\n" +
           $"受管 Runner PID：{(snapshot.RunnerPid is null ? "无" : snapshot.RunnerPid.ToString())}\n" +
           $"监督启动 UTC：{FormatUtc(snapshot.StartedUtc)}\n" +
           $"最后核验 UTC：{FormatUtc(snapshot.LastVerifiedUtc)}\n" +
           $"说明：{ReasonixIntegrationService.RedactSecrets(snapshot.Message)}";

    // ---- automate（本地自动验收守候，阶段一）的摘要与退出码映射 ----

    /// <summary>
    /// automate 结论 → 退出码：0=已排队/接回既有队列记录（含已完成的验收 accepted、修复已通过 accepted、
    /// no-repair-needed、not-applicable 与仍在运行中的接回）；2=任务取消；
    /// 其余（任务失败/门禁不过/不确定/验收失败/需修复/需用户/修复失败/修复不定）→ 1。
    /// 绝不表示本命令重投过 DSH 或曾调用过模型。
    /// </summary>
    public static int MapAcceptanceExitCode(string outcome)
        => string.Equals(outcome, HarnessAcceptanceCoordinator.OutcomeQueued, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessAcceptanceCoordinator.OutcomeAlreadyQueued, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessAcceptanceCoordinator.OutcomeAccepted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessRemediationStore.AcceptedState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessRemediationStore.NoRepairNeededState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessRemediationCoordinator.OutcomeNotApplicable, StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, HarnessRemediationCoordinator.OutcomeAlreadyInProgress, StringComparison.OrdinalIgnoreCase) ? ExitCompleted
         : string.Equals(outcome, HarnessAcceptanceCoordinator.OutcomeTaskCancelled, StringComparison.OrdinalIgnoreCase) ? ExitCancelled
         : ExitFailed;

    /// <summary>automate 守候摘要：任务 ID + 验收队列状态中文文案 + 脱敏说明 + 定位路径（无合同正文/凭据）。</summary>
    public static string BuildAcceptanceAutomateSummary(HarnessAcceptanceWatchResult result)
        => $"任务：{result.TaskId}\n" +
           $"验收队列：{AcceptanceStateText(result.QueueState)}\n" +
           $"说明：{ReasonixIntegrationService.RedactSecrets(result.Message)}\n" +
           $"任务目录：{result.TaskDirectory}";

    /// <summary>start 的自动守候拉起摘要：明确“已启动/已接回/未启动”，绝不假称启用。</summary>
    public static string BuildAcceptanceWatchStartSummary(HarnessAcceptanceWatchStartResult result)
        => $"自动验收守候：{(result.Started
                ? "已自动启动（PID " + (result.WatchPid?.ToString() ?? "未知") + "）"
                : result.Reused
                    ? "已接回既有守候（PID " + (result.WatchPid?.ToString() ?? "未知") + "）"
                    : "未启动")}\n" +
           $"守候状态：{AcceptanceWatchStateText(result.State)}\n" +
           $"说明：{ReasonixIntegrationService.RedactSecrets(result.Message)}";

    private static string AcceptanceWatchStateText(string? state)
        => (state ?? string.Empty).ToLowerInvariant() switch
        {
            HarnessAcceptanceWatchStore.RunningState => "运行中",
            HarnessAcceptanceWatchStore.ExitedState => "已结束",
            HarnessAcceptanceWatchStore.NotStartedState => "未启动（可诊断）",
            _ => state ?? "无"
        };

    private static string AcceptanceStateText(string? state)
        => (state ?? string.Empty).ToLowerInvariant() switch
        {
            HarnessAcceptanceQueue.AwaitingGptState => "尚未排队（awaiting-gpt）",
            HarnessAcceptanceQueue.QueuedState => "已排队（acceptance-queued，等待独立验收驱动）",
            HarnessAcceptanceQueue.RunningState => "验收运行中（acceptance-running）",
            HarnessAcceptanceQueue.AcceptedState => "已验收通过（accepted）",
            HarnessAcceptanceQueue.NeedsRepairState => "需修复（needs-repair）",
            HarnessAcceptanceQueue.FailedState => "验收失败（acceptance-failed，可诊断）",
            _ => state ?? "无"
        };


    private static string SupervisorStateText(string state)
        => (state ?? string.Empty).ToLowerInvariant() switch
        {
            HarnessSupervisor.OutcomeCompleted => "已完成（awaiting-gpt 且报告门禁通过）",
            HarnessSupervisor.OutcomeCancelled => "已取消",
            HarnessSupervisor.OutcomeFailed => "失败",
            HarnessSupervisor.OutcomeUncertain => "不确定（需安全对账）",
            HarnessSupervisor.RunningState => "运行中（受管 Runner 存活）",
            HarnessSupervisor.TerminalState => "已到监督终态",
            "awaiting-gpt" => "等待 GPT 验收",
            "starting" => "启动中",
            "busy" => "项目忙",
            _ => state ?? string.Empty
        };

    private static string FormatUtc(DateTime? value)
        => value is null ? "无" : value.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
}
