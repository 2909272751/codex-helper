using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Services;

// Harness 托管执行器控制台入口（薄壳）：
// 只接受绝对 ProjectRoot 与 TaskDirectory，调用 DeepSeekHarnessRunner.StartAsync 等待真实终态，
// 输出不含任务正文/凭据的中文摘要，并用退出码区分完成(0)/失败(1)/取消(2)/参数错误(3)。
// 参数解析、退出码映射与摘要构建的可测试核心逻辑在 CodexHelper.Core 的 HarnessRunnerCli。
var parsed = HarnessRunnerCli.Parse(args);
if (parsed.Error is not null)
{
    Console.Error.WriteLine(parsed.Error);
    Console.Error.WriteLine(HarnessRunnerCli.UsageText);
    return HarnessRunnerCli.ExitUsageError;
}

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
