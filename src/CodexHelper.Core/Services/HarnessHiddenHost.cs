using System.Diagnostics;

namespace CodexHelper.Core.Services;

/// <summary>
/// CodexHelper WinExe 隐藏宿主模式参数解析（--harness-host --node &lt;绝对路径&gt; --dsh &lt;绝对路径&gt;）。
/// 纯函数可独立测试；任务正文/凭据绝不进入命令行。
/// </summary>
public static class HarnessHiddenHostCli
{
    /// <summary>Host 已健康 / 子进程正常结束。</summary>
    public const int ExitOk = 0;
    /// <summary>启动或等待失败。</summary>
    public const int ExitFailed = 1;
    /// <summary>参数错误（缺失/相对路径）。</summary>
    public const int ExitUsageError = 3;

    public const string UsageText =
        "用法：CodexHelper.exe --harness-host --node <绝对 node.exe 路径> --dsh <绝对 dsh 入口路径>\n" +
        "行为：先探测 127.0.0.1:3080，已健康则安静退出 0；否则无窗口启动绝对 node + dsh web --host 127.0.0.1 并等待。\n" +
        "退出码：0=健康或宿主已退出，1=启动/等待失败，3=参数错误。";

    public sealed record HiddenHostOptions(string NodePath, string DshEntryPath);

    /// <summary>解析参数；返回 null 表示不是隐藏宿主模式（由普通 UI 继续处理）。</summary>
    public static HiddenHostOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var list = args ?? Array.Empty<string>();
        var host = false;
        string? node = null;
        string? dsh = null;
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (string.Equals(arg, "--harness-host", StringComparison.OrdinalIgnoreCase)) { host = true; continue; }
            if (string.Equals(arg, "--node", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= list.Count) { error = "缺少 --node 的参数值。"; return null; }
                node = list[++i];
                continue;
            }
            if (string.Equals(arg, "--dsh", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= list.Count) { error = "缺少 --dsh 的参数值。"; return null; }
                dsh = list[++i];
                continue;
            }
        }
        if (!host) return null;
        if (string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(dsh))
        {
            error = "隐藏宿主模式必须提供绝对路径 --node 与 --dsh。";
            return null;
        }
        node = node.Trim();
        dsh = dsh.Trim();
        if (!Path.IsPathRooted(node) || !Path.IsPathRooted(dsh))
        {
            error = "隐藏宿主模式的 --node 与 --dsh 都必须是绝对路径。";
            return null;
        }
        try
        {
            node = Path.GetFullPath(node);
            dsh = Path.GetFullPath(dsh);
        }
        catch (Exception ex)
        {
            error = "路径无效：" + ex.Message;
            return null;
        }
        // 不在此处校验文件存在：状态查询/对账需要在文件已缺失时仍能识别配置形状；
        // 实际启动时文件缺失会让启动失败并返回非零退出码。
        return new HiddenHostOptions(node, dsh);
    }
}

/// <summary>
/// CodexHelper WinExe 隐藏宿主核心：不创建主窗口、不弹消息框。
/// 先探测本机回环 3080；已健康则安静退出 0；否则用 <see cref="DeepSeekHarnessProcess.LaunchWebHost"/>
/// 无窗口启动绝对 node + dsh 并等待子进程（退出码随子进程）。
/// 探测与启动均可注入以便测试。
/// </summary>
public static class DeepSeekHarnessHiddenHost
{
    /// <summary>Host 端口探测（默认本机 GET；测试可注入）。</summary>
    public static Func<string, int, CancellationToken, Task<bool>>? PortProbe { get; set; }
    /// <summary>Web Host 启动器（默认 DeepSeekHarnessProcess.LaunchWebHost；测试可注入）。</summary>
    public static Func<string, string, Process?>? Launcher { get; set; }

    public static async Task<int> RunAsync(string nodePath, string dshEntryPath, CancellationToken cancellationToken = default)
    {
        var probe = new DeepSeekHarnessWebHostProbe { PortProbe = PortProbe };
        bool healthy;
        try
        {
            healthy = await probe.IsWebHostRunningAsync(
                DeepSeekHarnessVersions.WebHostDefaultUrl,
                DeepSeekHarnessVersions.WebHostDefaultPort,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return HarnessHiddenHostCli.ExitFailed;
        }
        catch
        {
            return HarnessHiddenHostCli.ExitFailed;
        }
        if (healthy) return HarnessHiddenHostCli.ExitOk;

        // 隐藏宿主补位启动时同样注入持久化的权限环境（受控进程环境变量，不进入命令行/日志）。
        var permissionMode = HarnessExecutionOptions.DefaultPermission;
        try
        {
            var app = new CodexHelper.Core.Infrastructure.AppPaths();
            permissionMode = HarnessExecutionOptions.NormalizePermission(new SettingsService(app).Load().HarnessPermissionMode);
        }
        catch { /* 设置不可读时保持默认权限 */ }

        var process = Launcher?.Invoke(nodePath, dshEntryPath) ?? DeepSeekHarnessProcess.LaunchWebHost(nodePath, dshEntryPath, permissionMode);
        if (process is null) return HarnessHiddenHostCli.ExitFailed;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var exitCode = 0;
            try { exitCode = process.ExitCode; }
            catch { return HarnessHiddenHostCli.ExitFailed; }
            LogEarlyExitIfNeeded(process, exitCode);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return HarnessHiddenHostCli.ExitFailed;
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return HarnessHiddenHostCli.ExitFailed;
        }
    }

    /// <summary>
    /// 隐藏宿主不弹窗；子进程非 0 退出时仅把脱敏摘要写入 Helper 本地日志，绝不写原始完整 stderr。
    /// 仅对默认 LaunchWebHost 启动（有输出捕获）的进程记录；注入启动器与无捕获进程不产生日志。
    /// </summary>
    private static void LogEarlyExitIfNeeded(Process process, int exitCode)
    {
        if (exitCode == HarnessHiddenHostCli.ExitOk) return;
        var output = DeepSeekHarnessProcess.GetCapturedOutput(process);
        if (output is null) return;
        try
        {
            DeepSeekHarnessProcess.WaitOutputDrainedAsync(process, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch { }
        var summary = HarnessOutputDiagnostics.BuildExitSummary(exitCode, output.StdoutTail, output.StderrTail);
        try
        {
            new CodexHelper.Core.Infrastructure.AppLogger(new CodexHelper.Core.Infrastructure.AppPaths())
                .WriteError("HarnessHiddenHost 子进程提前退出", new InvalidOperationException(summary));
        }
        catch { /* 日志写入失败不影响退出码 */ }
    }
}
