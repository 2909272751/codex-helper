using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Services;

// Harness 托管执行器控制台入口（薄壳）：
// 只接受绝对 ProjectRoot 与 TaskDirectory；任务正文绝不进入命令行，摘要只输出脱敏状态与定位信息。
// 旧调用形态（无 -Mode）：调用 DeepSeekHarnessRunner.StartAsync 前台等待真实终态，并用退出码
//   区分完成(0)/失败(1)/取消(2)/参数错误(3)——running/busy/starting 绝不映射为成功。
// 监督模式（-Mode start|await|status|automate，阶段一持久等待/自动验收协议）：
//   start  后台启动受控 Runner 子进程并立即返回安全摘要与可重连标识（start 返回绝不代表任务完成）；
//          成功后自动、单飞拉起独立验收守候（阶段二：真实终态后执行一次独立 Codex 验收）；
//   await  按监督记录接回等待真实终态（子 Runner 退出 + HARNESS_STATUS.json 非 running + 报告门禁）；
//   status 固定大小脱敏摘要，不读取 DSH 聊天/session.history；
//   automate 启动/接回本地验收守候并默认执行一次独立 Codex 验收（codex app-server 独立线程，绝不重投 DSH）。
// 参数解析、退出码映射与摘要构建的可测试核心逻辑在 CodexHelper.Core 的 HarnessRunnerCli 与 HarnessSupervisor。
var parsed = HarnessRunnerCli.Parse(args);
if (parsed.Error is not null)
{
    Console.Error.WriteLine(parsed.Error);
    Console.Error.WriteLine(HarnessRunnerCli.UsageText);
    return HarnessRunnerCli.ExitUsageError;
}

if (!string.IsNullOrWhiteSpace(parsed.Mode))
{
    var supervisor = new HarnessSupervisor
    {
        // await 需要"现有安全对账"能力：受管 Runner 消失而任务仍运行时按 Host 会话列表对账，绝不虚报完成。
        ReconcileAsync = parsed.Mode == HarnessRunnerCli.ModeAwait
            ? ct => new DeepSeekHarnessRunner(new AppPaths()).ReconcileRecentTasksAsync(ct)
            : null
    };
    try
    {
        switch (parsed.Mode.ToLowerInvariant())
        {
            case HarnessRunnerCli.ModeStart:
            {
                // start：启动受控子进程并立即返回安全摘要；绝不等待真实终态、绝不表述为任务完成。
                var started = await supervisor.StartAsync(parsed.ProjectRoot!, parsed.TaskDirectory!);
                Console.WriteLine(HarnessRunnerCli.BuildSupervisorStartSummary(started));
                // 自动、单飞拉起独立验收守候（阶段二 A）：失败不影响 DSH 本身，但摘要明确标注，绝不假称启用。
                var watchStart = await new HarnessAcceptanceWatcherStarter().TryStartAsync(parsed.ProjectRoot!, parsed.TaskDirectory!);
                Console.WriteLine(HarnessRunnerCli.BuildAcceptanceWatchStartSummary(watchStart));
                return HarnessRunnerCli.ExitCompleted;
            }
            case HarnessRunnerCli.ModeAwait:
            {
                // await：按监督记录接回并等待真实终态（阻塞直到结论；受管 Runner 仍存活时任意进程可接回）。
                var awaited = await supervisor.AwaitAsync(parsed.ProjectRoot!, parsed.TaskDirectory!);
                Console.WriteLine(HarnessRunnerCli.BuildSupervisorAwaitSummary(awaited));
                return HarnessRunnerCli.MapSupervisorOutcomeExitCode(awaited.Outcome);
            }
            case HarnessRunnerCli.ModeStatus:
            {
                var snapshot = supervisor.Status(parsed.ProjectRoot!, parsed.TaskDirectory!);
                Console.WriteLine(HarnessRunnerCli.BuildSupervisorStatusSummary(snapshot));
                // status 的 0 只表示摘要输出成功，绝不表示任务完成（摘要里以文字区分 running/终态）。
                return HarnessRunnerCli.ExitCompleted;
            }
            case HarnessRunnerCli.ModeAutomate:
            {
                // automate：本地验收守候（阻塞直到排队决策/任务终局）；不创建 HarnessSupervisor 记录、绝不重投 DSH。
                var watch = await new HarnessAcceptanceWatcher().WatchUntilQueueDecisionAsync(parsed.ProjectRoot!, parsed.TaskDirectory!);
                Console.WriteLine(HarnessRunnerCli.BuildAcceptanceAutomateSummary(watch));
                return HarnessRunnerCli.MapAcceptanceExitCode(watch.Outcome);
            }
            default:
                return HarnessRunnerCli.ExitUsageError;
        }
    }
    catch (OperationCanceledException)
    {
        // 仅等待被中断：受管 Runner 仍在后台/验收未重投，绝不表述为任务完成/取消。
        if (parsed.Mode == HarnessRunnerCli.ModeAutomate)
            Console.Error.WriteLine("验收守候被中断；未重投 DSH 合同、未改动验收队列，可随时用同一命令（-Mode automate）重新接回本地守候。本地等待不消耗 Codex token。");
        else
            Console.Error.WriteLine("监督等待被中断；受管 Runner 仍在运行，可随时用同一命令（-Mode await）重新接回等待，本地等待不消耗 Codex token。");
        return HarnessRunnerCli.ExitFailed;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("监督命令执行失败：" + ex.Message);
        return HarnessRunnerCli.ExitFailed;
    }
}

// 旧同步调用形态：前台等待真实终态（兼容既有 invoke-harness.ps1 直接调用与回归测试）。
var runner = new DeepSeekHarnessRunner(new AppPaths());
try
{
    var status = await runner.StartAsync(parsed.ProjectRoot!, parsed.TaskDirectory!);
    Console.WriteLine(HarnessRunnerCli.BuildSummary(status));
    return HarnessRunnerCli.MapExitCode(status.State);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("任务已取消：用户停止了 Harness 任务。");
    return HarnessRunnerCli.ExitCancelled;
}
catch (Exception ex)
{
    Console.Error.WriteLine("任务执行失败：" + ex.Message);
    return HarnessRunnerCli.ExitFailed;
}
