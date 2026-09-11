using System.Diagnostics;

namespace CodexHelper.Core.Services;

/// <summary>
/// CodexHelper WinExe 隐藏宿主模式参数解析（--harness-host --node &lt;绝对路径&gt; [--dsh &lt;旧入口回退&gt;]）。
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
        "用法：CodexHelper.exe --harness-host --node <绝对 node.exe 路径> [--dsh <旧入口回退>]\n" +
        "行为：自动发现本机最高有效 DSH 版本；已健康则安静退出 0，否则无窗口启动 node + dsh web --host 127.0.0.1 并等待。\n" +
        "退出码：0=健康或宿主已退出，1=启动/等待失败，3=参数错误。";

    public sealed record HiddenHostOptions(string NodePath, string? DshEntryPath);

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
        if (string.IsNullOrWhiteSpace(node))
        {
            error = "隐藏宿主模式必须提供绝对路径 --node。";
            return null;
        }
        node = node.Trim();
        dsh = dsh?.Trim();
        if (!Path.IsPathRooted(node) || (!string.IsNullOrWhiteSpace(dsh) && !Path.IsPathRooted(dsh)))
        {
            error = "隐藏宿主模式的 --node 与 --dsh 都必须是绝对路径。";
            return null;
        }
        try
        {
            node = Path.GetFullPath(node);
            if (!string.IsNullOrWhiteSpace(dsh)) dsh = Path.GetFullPath(dsh);
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
    /// <summary>最近一次隐藏宿主启动/重启使用的 authority（脱敏；测试可读取，仅一个字符串）。</summary>
    public static string? LastLaunchAuthority { get; set; }
    /// <summary>88frp 运行时配置的轻量检测周期；覆盖隐藏宿主启动的子进程与现存 Host 的信任同步。</summary>
    public static TimeSpan FrpMonitorInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>新发现的 88frp 公网入口须稳定保持的确认窗口；测试可显式缩短。</summary>
    public static TimeSpan FrpAuthorityDebounceWindow { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>authority 解析器（默认读取 88frp 实例根目录；测试可注入）。</summary>
    public static Func<IReadOnlyList<string>> AuthoritiesResolver { get; set; }
        = static () => FrpRuntimeAuthorityResolver.ResolveDshWebAuthorities();
    /// <summary>受管 profile patch 路径（默认按 DSH Home 解析；测试可注入临时目录）。</summary>
    public static Func<string?> PatchPathResolver { get; set; }
        = static () => DshProfilePatchLocator.TryResolveDefaultPatchPath();
    /// <summary>公网 Origin 只读验证（null = 使用默认 session.list 验证；测试可注入）。</summary>
    public static Func<string, CancellationToken, Task<FrpOriginVerification>>? OriginVerifier { get; set; }
    /// <summary>同步状态存储（null = 默认 %LOCALAPPDATA% 状态文件；测试可注入内存存储）。</summary>
    public static IFrpAuthorityStateStore? SyncStateStore { get; set; }
    /// <summary>同步服务（null = 按 <see cref="SyncStateStore"/> 与默认时钟构造；测试可注入）。</summary>
    public static FrpAuthoritySyncService? SyncService { get; set; }

    public static async Task<int> RunAsync(string nodePath, string dshEntryPath, CancellationToken cancellationToken = default)
    {
        // 隐藏宿主补位启动时同样注入持久化的权限环境（受控进程环境变量，不进入命令行/日志）。
        var permissionMode = HarnessExecutionOptions.DefaultPermission;
        try
        {
            var app = new CodexHelper.Core.Infrastructure.AppPaths();
            permissionMode = HarnessExecutionOptions.NormalizePermission(new SettingsService(app).Load().HarnessPermissionMode);
        }
        catch { /* 设置不可读时保持默认权限 */ }

        var sync = SyncService ?? new FrpAuthoritySyncService(SyncStateStore);
        // 启动前先同步一次：patch 与信任状态在 Host 起来之前就应具备，避免新 origin 首次访问被拒。
        var initial = await sync.CheckAsync(CreateSyncOptions(sync, restartHost: null));
        var authority = initial.Status.DetectedAuthority ?? FrpRuntimeAuthorityResolver.TryResolveDshWebAuthority();
        var appliedAuthority = authority;

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
        if (healthy)
        {
            // 接管现存 Host：检测入口变化、更新受管 patch 并验证 origin；本路径不重启他人进程，
            // pending 状态交给状态文件与界面展示，地址变化绝不中断 DSH 编码任务。
            try { await sync.CheckAsync(CreateSyncOptions(sync, restartHost: null)); } catch { }
            return HarnessHiddenHostCli.ExitOk;
        }

        var launched = Launch(nodePath, dshEntryPath, permissionMode, authority);
        if (launched is null) return HarnessHiddenHostCli.ExitFailed;
        Process process = launched;
        try
        {
            while (true)
            {
                var exited = process.WaitForExitAsync(cancellationToken);
                var tick = Task.Delay(FrpMonitorInterval, cancellationToken);
                if (await Task.WhenAny(exited, tick) == exited)
                {
                    var exitCode = 0;
                    try { exitCode = process.ExitCode; }
                    catch { return HarnessHiddenHostCli.ExitFailed; }
                    LogEarlyExitIfNeeded(process, exitCode);
                    return exitCode;
                }
                // 受控 Host 重启：只有没有任何运行中会话时同步服务才会调用一次；期间不中断编码任务。
                var restartedAuthority = appliedAuthority;
                async Task RestartHostAsync(string newAuthority, CancellationToken token)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    try { await process.WaitForExitAsync(token); } catch { }
                    var restarted = Launch(nodePath, dshEntryPath, permissionMode, newAuthority);
                    if (restarted is null) throw new InvalidOperationException("受控 Host 重启后未能启动新进程。");
                    process = restarted;
                    restartedAuthority = newAuthority;
                }

                try { await sync.CheckAsync(CreateSyncOptions(sync, RestartHostAsync)); } catch { }
                appliedAuthority = restartedAuthority;
            }
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

    /// <summary>构造一次同步检查的注入点：解析/patch/验证/重启全部走同一套可测入口。</summary>
    private static FrpAuthoritySyncOptions CreateSyncOptions(FrpAuthoritySyncService sync, Func<string, CancellationToken, Task>? restartHost) => new()
    {
        RestartAllowed = restartHost is not null,
        ResolveAuthorities = () => AuthoritiesResolver(),
        PatchPath = PatchPathResolver(),
        // 已验证入口不重复等待；新入口仍须连续稳定一个完整去抖窗口，
        // 避免 88frp 写入 runtime 配置的短暂中间态触发切换。
        DebounceWindow = FrpAuthorityDebounceWindow,
        DebounceConfirmedAuthority = sync.Snapshot().VerifiedAuthority,
        VerifyOriginAsync = OriginVerifier ?? ((authority, token) => VerifyOriginAsync(authority, token)),
        RestartHostAsync = restartHost
    };

    /// <summary>
    /// 默认公网 Origin 只读验证：以 <c>http://&lt;authority&gt;</c> 作 Origin，对公网地址调用
    /// 只读 session.list；成功后返回运行中托管会话数（用于安全窗口判断）。不发送任何凭据。
    /// </summary>
    public static async Task<FrpOriginVerification> VerifyOriginAsync(string authority, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authority)) return FrpOriginVerification.Reject("authority 为空。");
        var origin = "http://" + authority;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var rpc = new HarnessRpcClient(origin, http) { OriginHeader = origin };
            var running = await rpc.CountRunningSessionsAsync(cancellationToken);
            return FrpOriginVerification.Ok(running);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FrpOriginVerification.Reject($"公网 origin 验证超时（{origin}）。");
        }
        catch (Exception ex)
        {
            return FrpOriginVerification.Reject($"公网 origin 只读 RPC 失败（{origin}）：" + FrpAuthoritySyncService.Sanitize(ex.Message));
        }
    }


    private static Process? Launch(string nodePath, string dshEntryPath, string permissionMode, string? authority)
    {
        try { LastLaunchAuthority = authority; } catch { }
        return Launcher?.Invoke(nodePath, dshEntryPath)
            ?? DeepSeekHarnessProcess.LaunchWebHost(nodePath, dshEntryPath, permissionMode,
                string.IsNullOrWhiteSpace(authority) ? null : [authority]);
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
