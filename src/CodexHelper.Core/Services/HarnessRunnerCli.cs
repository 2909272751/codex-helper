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
    /// <summary>参数错误（缺失、相对路径、越界、缺 SPEC.md、参数过多）。</summary>
    public const int ExitUsageError = 3;

    /// <summary>参数解析结果；Error 非空时不可执行。</summary>
    public sealed record RunnerArguments(string? ProjectRoot, string? TaskDirectory, string? Error);

    public const string UsageText =
        "用法：CodexHelper.HarnessRunner.exe -ProjectRoot <绝对项目根目录> -TaskDirectory <绝对任务目录>\n" +
        "退出码：0=完成，1=失败，2=取消，3=参数错误。任务正文只从任务目录文件读取，绝不进入命令行。";

    /// <summary>
    /// 解析并校验命令行参数：支持 -ProjectRoot/-TaskDirectory（不区分大小写，也接受
    /// --project-root/--task-directory）与两个位置参数两种形式；两个路径都必须是绝对路径、
    /// 任务目录必须位于项目根目录内、任务目录必须存在 SPEC.md。任何一步失败都返回 Error（中文），
    /// 绝不开始执行。
    /// </summary>
    public static RunnerArguments Parse(string[] args)
    {
        var projectRoot = (string?)null;
        var taskDirectory = (string?)null;
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
        return new(projectRoot, taskDirectory, null);
    }

    /// <summary>把任务终态映射为退出码：completed→0、cancelled→2、其余（failed/未知）→1。</summary>
    public static int MapExitCode(string state)
        => string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase) ? ExitCompleted
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
            "completed" => "已完成",
            "cancelled" => "已取消",
            "failed" => "失败",
            "running" => "运行中",
            "starting" => "启动中",
            _ => status.State ?? string.Empty
        };
        var message = ReasonixIntegrationService.RedactSecrets(status.Message ?? string.Empty);
        return $"任务：{status.TaskId}\n状态：{stateText}\n说明：{message}\n项目根：{status.ProjectRoot}\n任务目录：{status.TaskDirectory}";
    }
}
