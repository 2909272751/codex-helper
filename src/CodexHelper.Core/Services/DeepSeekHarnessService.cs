using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>DeepSeek Harness 环境诊断快照。诚实呈现缺失依赖与能力降级。</summary>
public sealed record DeepSeekHarnessStatus(
    bool NodeFound,
    string NodePath,
    string NodeSource,
    string NodeVersion,
    bool NodeVersionSupported,
    string NodeMessage,
    string FixedHarnessVersion,
    bool WebHostRunning,
    string WebUrl,
    string WebHostMessage,
    bool RelayCapable,
    bool RelayConfirmed,
    string RelayMessage,
    string DownloadUrl,
    bool EnableAllowed,
    string? DiscoveryNote = null,
    bool DshFound = false,
    string DshEntryPath = "",
    string DshSource = "",
    string DshVersion = "",
    bool DshVersionSupported = false,
    string DshMessage = "",
    HarnessVersionRiskLevel DshRisk = HarnessVersionRiskLevel.Invalid,
    HarnessStatusKind StatusKind = HarnessStatusKind.Unknown,
    bool CapabilityCli = false,
    bool CapabilityWebProfile = false,
    bool CapabilityHostReachable = false,
    bool RelaySubmitSupported = false,
    bool RelayEventStreamSupported = false,
    bool RelayCancelSupported = false,
    DateTime CapabilityTimestampUtc = default,
    string NodeFingerprint = "",
    string DshFingerprint = "",
    bool CapabilityContractProfile = false,
    string? ContractProfileMessage = null,
    bool CapabilityPermissionEnv = false,
    string? PermissionEnvMessage = null);

public sealed record HarnessHostReadyResult(bool Ready, bool Started, int ProcessId, string Message, DeepSeekHarnessStatus Status);

/// <summary>
/// DeepSeek Harness 环境诊断、Web Host 生命周期（启动/停止/重新检测）与中继能力探测。
/// 只做发现、诊断、引导与在用户已有环境中的启动；不安装 Node/npm 包/Harness。
/// 当前机器没有 Node 是正常降级场景：解释缺少什么并提供官方下载入口，开启按钮不误报成功。
/// </summary>
public sealed class DeepSeekHarnessService
{
    private readonly AppPaths paths;
    private readonly string webUrl;
    private Process? webHostProcess;
    private static readonly object HostLock = new();

    /// <summary>Node 候选发现器（测试注入用）。</summary>
    public Func<DeepSeekHarnessDiscovery>? DiscoveryFactory { get; init; }
    /// <summary>Node 版本探测（默认运行 node --version；测试可注入）。</summary>
    public Func<string, string>? NodeVersionReader { get; init; }
    /// <summary>dsh 包版本读取（默认读 package.json 的 version；测试可注入）。</summary>
    public Func<string, string>? DshVersionReader { get; init; }
    /// <summary>Web Host 端口探测（默认本机 GET；测试可注入）。</summary>
    public Func<string, int, CancellationToken, Task<bool>>? WebHostPortProbe { get; init; }
    /// <summary>中继能力探测（默认 rc.6 原生协议真实探测：提交/事件流/取消；测试可注入确定实现）。</summary>
    public Func<IDeepSeekHarnessRelay>? RelayFactory { get; init; }
    /// <summary>Web Host 启动器（默认通过绝对 node.exe + dsh 入口；测试可注入）。</summary>
    public Func<string, string, Process?>? WebHostLauncher { get; init; }
    /// <summary>dsh 主帮助输出读取（默认运行绝对 node.exe + 绝对 dsh 入口 --help；测试可注入）。
    /// 用于确认主帮助包含 web profile/command，属于无副作用探测。</summary>
    public Func<string, string, string>? WebProfileReader { get; init; }
    /// <summary>能力探测缓存工厂（默认使用 AppPaths.BaseDirectory 下文件缓存；测试可注入）。</summary>
    public Func<DeepSeekHarnessCapabilityCache>? CapabilityCacheFactory { get; init; }

    public DeepSeekHarnessService(AppPaths paths, string? webUrl = null)
    {
        this.paths = paths;
        this.webUrl = string.IsNullOrWhiteSpace(webUrl) ? DeepSeekHarnessVersions.WebHostDefaultUrl : webUrl;
    }

    /// <summary>外部诊断命令（node --version / dsh --help）的短超时：损坏环境不得让检测无限挂起。
    /// 超时只表示该次检测失败，不影响长任务执行策略。</summary>
    private static readonly TimeSpan DiagnosticProcessTimeout = TimeSpan.FromSeconds(10);

    /// <summary>执行完整环境诊断：Node 发现/版本规则、dsh 包发现/版本风险、能力探测（CLI/Web profile/Web Host/中继）。
    /// 版本只用于风险提示，不单独阻止启用；入口完整且能力通过时未知新版本也可用。
    /// 整个诊断（含文件系统发现与外部命令探测）在线程池执行，首次 await 前绝不占用调用方（UI）线程。</summary>
    public Task<DeepSeekHarnessStatus> DiagnoseAsync(string? userSelectedNodePath = null, string? userSelectedDshEntryPath = null, CancellationToken cancellationToken = default, bool forceRefresh = false)
        => Task.Run(() => DiagnoseCoreAsync(userSelectedNodePath, userSelectedDshEntryPath, cancellationToken, forceRefresh), cancellationToken);

    private async Task<DeepSeekHarnessStatus> DiagnoseCoreAsync(string? userSelectedNodePath, string? userSelectedDshEntryPath, CancellationToken cancellationToken, bool forceRefresh)
    {
        var discovery = DiscoveryFactory?.Invoke() ?? new DeepSeekHarnessDiscovery();
        var candidates = discovery.Discover(userSelectedNodePath);

        string? nodePath = null;
        HarnessNodeCandidate? node = null;
        foreach (var candidate in candidates)
        {
            var version = ReadNodeVersion(candidate.Path);
            // 优先：用户选择 > 常见位置 > PATH；同来源内版本受支持者优先。
            if (node is null || SourceRank(candidate.Source) < SourceRank(node.Source)
                || (candidate.Source == node.Source && DeepSeekHarnessProbe.IsNodeVersionSupported(version) && !DeepSeekHarnessProbe.IsNodeVersionSupported(node.Version)))
            {
                node = candidate with { Version = version };
            }
        }
        if (node is not null) { nodePath = node.Path; }

        var dshCandidates = discovery.DiscoverDsh(userSelectedDshEntryPath);
        HarnessDshCandidate? dsh = null;
        foreach (var candidate in dshCandidates)
        {
            var version = ReadDshVersion(candidate.PackageRoot);
            if (dsh is null || DshSourceRank(candidate.Source) < DshSourceRank(dsh.Source)
                || (candidate.Source == dsh.Source && DeepSeekHarnessProbe.IsDshVersionSupported(version) && !DeepSeekHarnessProbe.IsDshVersionSupported(dsh.Version)))
            {
                dsh = candidate with { Version = version };
            }
        }

        var nodeMessage = BuildNodeMessage(node);
        var dshMessage = BuildDshMessage(dsh);
        var nodeReady = node is not null && DeepSeekHarnessProbe.IsNodeVersionSupported(node.Version);
        var dshReady = dsh is not null && DeepSeekHarnessProbe.IsDshVersionSupported(dsh.Version);
        var risk = DeepSeekHarnessSemVer.EvaluateRisk(dsh?.Version);
        var isNewVersion = risk is HarnessVersionRiskLevel.SameSeries
            or HarnessVersionRiskLevel.NewMinor
            or HarnessVersionRiskLevel.CrossMajor;

        var capability = await ProbeCapabilitiesAsync(node, dsh, nodeReady && node is not null ? node.Version : string.Empty,
            dsh?.Version ?? string.Empty, forceRefresh, cancellationToken);
        // 入口完整（CLI + Web profile 通过）才允许开启并启动 Web Host；中继实时协作另计。
        var enableAllowed = nodeReady && dshReady && capability.EntryComplete;
        var statusKind = capability.EvaluateStatusKind(isNewVersion);

        var note = BuildDiscoveryNote(candidates, dshCandidates);

        // 合同 profile/preset 覆盖能力：仅当 dsh 入口存在且官方 standard preset 结构可识别时确认，
        // 未确认时 UI 必须诚实显示降级到 standard，绝不虚报 codex-contract 可用。
        var profileSupported = false;
        string? profileMessage = null;
        if (dsh is not null)
        {
            var profileService = new HarnessContractProfileService();
            profileSupported = profileService.IsSupported(dsh.EntryPath);
            profileMessage = profileSupported
                ? "DSH 原生 preset 覆盖可用：codex-contract 合同模式可生成；结构不兼容时自动降级 standard。"
                : "DSH 未确认支持 Helper 的 preset 覆盖（standard 组合缺失或 persona 已变化），合同模式将降级为 standard。";
        }
        else
        {
            profileMessage = "缺少可用的 dsh 入口，无法确认 preset 覆盖能力，合同模式将降级为 standard。";
        }

        // 权限环境能力：Helper 启动的 Web Host 可携带受控环境变量（DSH_PERMISSION_MODE/DSH_APPROVAL_POLICY），
        // 且绝不出现在命令行或日志；外部已运行的 Host 无法注入环境，会话仍按 DSH 默认权限执行（诚实说明）。
        var permissionEnv = capability.HostReachable || enableAllowed;
        var permissionEnvMessage = permissionEnv
            ? "权限通过 Helper 托管 Host 的受控进程环境传递（DSH_PERMISSION_MODE/DSH_APPROVAL_POLICY），不进入命令行或日志；外部 Host 无法注入环境时按 DSH 默认权限执行。"
            : "Web Host 未运行且不可由 Helper 启动，权限环境暂未生效。";

        return new DeepSeekHarnessStatus(
            NodeFound: node is not null,
            NodePath: nodePath ?? string.Empty,
            NodeSource: node is null ? string.Empty : DescribeSource(node.Source),
            NodeVersion: node?.Version ?? string.Empty,
            NodeVersionSupported: nodeReady,
            NodeMessage: nodeMessage,
            FixedHarnessVersion: $"{DeepSeekHarnessVersions.FixedPackage}@{DeepSeekHarnessVersions.FixedVersion}",
            WebHostRunning: capability.HostReachable,
            WebUrl: webUrl,
            WebHostMessage: capability.HostReachableMessage,
            RelayCapable: capability.RelayCapable,
            RelayConfirmed: capability.RelayConfirmed,
            RelayMessage: capability.RelayMessage,
            DownloadUrl: DeepSeekHarnessVersions.NodeDownloadUrl,
            EnableAllowed: enableAllowed,
            DiscoveryNote: note,
            DshFound: dsh is not null,
            DshEntryPath: dsh?.EntryPath ?? string.Empty,
            DshSource: dsh is null ? string.Empty : DescribeDshSource(dsh.Source),
            DshVersion: dsh?.Version ?? string.Empty,
            DshVersionSupported: dshReady,
            DshMessage: dshMessage,
            DshRisk: risk,
            StatusKind: statusKind,
            CapabilityCli: capability.Cli,
            CapabilityWebProfile: capability.WebProfile,
            CapabilityHostReachable: capability.HostReachable,
            RelaySubmitSupported: capability.RelaySubmit,
            RelayEventStreamSupported: capability.RelayEvents,
            RelayCancelSupported: capability.RelayCancel,
            CapabilityTimestampUtc: capability.TimestampUtc,
            NodeFingerprint: capability.NodeFingerprint,
            DshFingerprint: capability.DshFingerprint,
            CapabilityContractProfile: profileSupported,
            ContractProfileMessage: profileMessage,
            CapabilityPermissionEnv: permissionEnv,
            PermissionEnvMessage: permissionEnvMessage);
    }

    /// <summary>
    /// 执行能力探测：入口能力（CLI 由 node --version、Web profile 由 dsh --help 确认主帮助含 web）可缓存；
    /// Web Host 可达性与中继能力属实时状态，每次都现算。探测完全无副作用：不安装、不更新、不启动真实 Host。
    /// </summary>
    private async Task<HarnessCapabilityResult> ProbeCapabilitiesAsync(
        HarnessNodeCandidate? node, HarnessDshCandidate? dsh, string nodeVersion, string dshVersion,
        bool forceRefresh, CancellationToken cancellationToken)
    {
        if (node is null || dsh is null)
        {
            var hostRunning0 = await IsWebHostRunningAsync(cancellationToken);
            var relay0 = await (RelayFactory?.Invoke() ?? new DeepSeekHarnessRelayProbe(webUrl)).ProbeCapabilitiesAsync(cancellationToken);
            return new HarnessCapabilityResult(
                Cli: false,
                CliMessage: "缺少可用的 Node 可执行文件，无法探测 CLI。",
                WebProfile: false,
                WebProfileMessage: "缺少可用的 dsh 入口，无法确认 web profile。",
                HostReachable: hostRunning0,
                HostReachableMessage: hostRunning0 ? $"Harness Web Host 正在监听 {webUrl}。" : "Harness Web Host 未运行。",
                RelaySubmit: relay0.SubmitSupported,
                RelayEvents: relay0.EventStreamSupported,
                RelayCancel: relay0.CancelSupported,
                RelayConfirmed: relay0.Confirmed,
                RelayMessage: relay0.Message,
                TimestampUtc: DateTime.UtcNow,
                NodeFingerprint: string.Empty,
                DshFingerprint: string.Empty);
        }

        var cache = CapabilityCacheFactory?.Invoke() ?? new DeepSeekHarnessCapabilityCache(paths);
        var (key, nodeFingerprint, dshFingerprint) = cache.BuildFingerprint(node.Path, dsh.EntryPath, dshVersion, nodeVersion);
        var probedEntry = false;
        bool cli; string cliMessage; bool webProfile; string webProfileMessage;
        if (!forceRefresh && cache.TryLoad(key, out var cached) && !string.IsNullOrEmpty(cached.NodeFingerprint))
        {
            cli = cached.Cli; cliMessage = cached.CliMessage;
            webProfile = cached.WebProfile; webProfileMessage = cached.WebProfileMessage;
        }
        else
        {
            probedEntry = true;
            cli = !string.IsNullOrWhiteSpace(nodeVersion);
            cliMessage = cli ? "Node CLI 可执行且版本可读。" : "Node CLI 无法执行或版本不可读。";
            var help = ReadWebProfile(node.Path, dsh.EntryPath);
            webProfile = !string.IsNullOrWhiteSpace(help) && help.IndexOf("web", StringComparison.OrdinalIgnoreCase) >= 0;
            webProfileMessage = webProfile
                ? "dsh 主帮助包含 web profile/command，确认可启动 Web。"
                : "dsh 主帮助未包含 web profile/command，视为入口不完整。";
        }

        var webHostRunning = await IsWebHostRunningAsync(cancellationToken);
        var relay = await (RelayFactory?.Invoke() ?? new DeepSeekHarnessRelayProbe(webUrl)).ProbeCapabilitiesAsync(cancellationToken);
        var result = new HarnessCapabilityResult(
            Cli: cli, CliMessage: cliMessage,
            WebProfile: webProfile, WebProfileMessage: webProfileMessage,
            HostReachable: webHostRunning,
            HostReachableMessage: webHostRunning ? $"Harness Web Host 正在监听 {webUrl}。" : "Harness Web Host 未运行。",
            RelaySubmit: relay.SubmitSupported,
            RelayEvents: relay.EventStreamSupported,
            RelayCancel: relay.CancelSupported,
            RelayConfirmed: relay.Confirmed,
            RelayMessage: relay.Message,
            TimestampUtc: DateTime.UtcNow,
            NodeFingerprint: nodeFingerprint,
            DshFingerprint: dshFingerprint);
        if (probedEntry)
        {
            try { cache.Store(key, result); } catch { /* 缓存写入失败不阻断诊断 */ }
        }
        return result;
    }

    private string ReadWebProfile(string nodePath, string dshEntryPath)
    {
        if (WebProfileReader is not null) return WebProfileReader(nodePath, dshEntryPath);
        try
        {
            var result = DeepSeekHarnessProcess.RunAsync(nodePath, new[] { dshEntryPath, "--help" }, timeout: DiagnosticProcessTimeout).GetAwaiter().GetResult();
            return (result.StdOut ?? string.Empty) + (result.StdErr ?? string.Empty);
        }
        catch { return string.Empty; }
    }

    /// <summary>启动 Helper 管理的 Web Host（仅监听本机回环），复用已运行的 Host；支持后续停止。
    /// 统一使用绝对 node.exe + 绝对 dsh 入口，不依赖 PATH 或 dsh.cmd 自行找 node。
    /// 权限模式通过受控进程环境变量（DSH_PERMISSION_MODE/DSH_APPROVAL_POLICY）传递，绝不出现在命令行。</summary>
    public Process? StartWebHost(string nodePath, string dshEntryPath, string? permissionMode = null)
    {
        if (string.IsNullOrWhiteSpace(nodePath) || string.IsNullOrWhiteSpace(dshEntryPath)) return null;
        lock (HostLock)
        {
            if (webHostProcess is not null && !webHostProcess.HasExited) return webHostProcess;
            var process = WebHostLauncher?.Invoke(nodePath, dshEntryPath) ?? LaunchDshWeb(nodePath, dshEntryPath, permissionMode);
            if (process is null) return null;
            webHostProcess = process;
        }
        return webHostProcess;
    }

    /// <summary>Ensure the local Harness Web Host is reachable, starting it from discovered absolute paths when needed.
    /// 权限模式从持久化设置读取并注入启动环境（受控进程环境变量，不进入命令行/日志）。</summary>
    public async Task<HarnessHostReadyResult> EnsureWebHostReadyAsync(
        string? userSelectedNodePath = null,
        string? userSelectedDshEntryPath = null,
        CancellationToken cancellationToken = default,
        string? permissionMode = null)
    {
        var status = await DiagnoseAsync(userSelectedNodePath, userSelectedDshEntryPath, cancellationToken);
        if (status.WebHostRunning)
            return new(true, false, 0, "Harness Web Host 已在运行。", status);
        if (!status.EnableAllowed)
            return new(false, false, 0, "无法启动 Harness Web Host：Node 或 dsh 入口未就绪。", status);

        var process = StartWebHost(status.NodePath, status.DshEntryPath, permissionMode);
        if (process is null)
            return new(false, false, 0, "Harness Web Host 进程启动失败。", status);

        var processId = 0;
        try { processId = process.Id; } catch { }
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsWebHostRunningAsync(cancellationToken))
            {
                var ready = await DiagnoseAsync(userSelectedNodePath, userSelectedDshEntryPath, cancellationToken, forceRefresh: true);
                return new(true, true, processId, "Harness Web Host 已自动启动并通过健康检查。", ready);
            }
            try
            {
                if (process.HasExited)
                {
                    // 进程刚退出时输出可能尚未排空完：有界等待后读取尾部，构建统一中文诊断（清洗+脱敏+插件分类）。
                    await DeepSeekHarnessProcess.WaitOutputDrainedAsync(process, TimeSpan.FromSeconds(2));
                    var exitCode = 0;
                    try { exitCode = process.ExitCode; } catch { }
                    var output = DeepSeekHarnessProcess.GetCapturedOutput(process);
                    var message = HarnessOutputDiagnostics.BuildExitSummary(exitCode, output?.StdoutTail, output?.StderrTail);
                    return new(false, true, processId, message, status);
                }
            }
            catch { }
            await Task.Delay(250, cancellationToken);
        }

        return new(false, true, processId, "Harness Web Host 已启动，但 10 秒内未通过健康检查。", status);
    }

    /// <summary>停止 Helper 启动的 Web Host（终止进程树）。</summary>
    public void StopWebHost()
    {
        lock (HostLock)
        {
            if (webHostProcess is null) return;
            try { if (!webHostProcess.HasExited) webHostProcess.Kill(entireProcessTree: true); } catch { }
            try { webHostProcess.Dispose(); } catch { }
            webHostProcess = null;
        }
    }

    private async Task<bool> IsWebHostRunningAsync(CancellationToken cancellationToken)
    {
        var probe = new DeepSeekHarnessWebHostProbe { PortProbe = WebHostPortProbe };
        try { return await probe.IsWebHostRunningAsync(webUrl, DeepSeekHarnessVersions.WebHostDefaultPort, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private string ReadNodeVersion(string path)
    {
        if (NodeVersionReader is not null) return NodeVersionReader(path);
        try
        {
            var result = DeepSeekHarnessProcess.RunAsync(path, new[] { "--version" }, timeout: DiagnosticProcessTimeout).GetAwaiter().GetResult();
            return CleanVersion(result.StdOut);
        }
        catch { return string.Empty; }
    }

    private string ReadDshVersion(string packageRoot)
    {
        if (DshVersionReader is not null) return DshVersionReader(packageRoot);
        try
        {
            var packageJson = System.IO.Path.Combine(packageRoot, "package.json");
            if (!File.Exists(packageJson)) return string.Empty;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packageJson));
            if (document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == System.Text.Json.JsonValueKind.String)
                return version.GetString() ?? string.Empty;
            return string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string CleanVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.TrimStart('\uFEFF').Trim();
    }

    private static string BuildNodeMessage(HarnessNodeCandidate? node)
    {
        if (node is null) return $"未找到 Node.js。请访问 {DeepSeekHarnessVersions.NodeDownloadUrl} 下载并安装（需 {22}.19+ 或 {24}.0+），然后重新检测。";
        var versionOk = DeepSeekHarnessProbe.IsNodeVersionSupported(node.Version);
        return versionOk
            ? $"Node.js {node.Version}（{DescribeSource(node.Source)}）满足版本规则。"
            : $"Node.js {node.Version} 不满足版本规则（需 {22}.19+ 或 {24}.0+）。";
    }

    private static string BuildDshMessage(HarnessDshCandidate? dsh)
    {
        if (dsh is null) return $"未找到 dsh 包。请安装 {DeepSeekHarnessVersions.FixedPackage}@{DeepSeekHarnessVersions.FixedVersion} 或更新版本，可访问 {DeepSeekHarnessVersions.HarnessPackageUrl}。";
        if (string.IsNullOrWhiteSpace(dsh.Version)) return $"已找到 dsh 入口，但无法读取 package.json 版本，视为入口损坏。";
        var risk = DeepSeekHarnessSemVer.EvaluateRisk(dsh.Version);
        return risk == HarnessVersionRiskLevel.Invalid
            ? $"dsh 版本“{dsh.Version}”非法或损坏，不得作为实际版本（禁止静默使用 latest）。"
            : $"dsh {dsh.Version}（{DescribeDshSource(dsh.Source)}）：{DeepSeekHarnessSemVer.Describe(risk)}。";
    }

    private static string? BuildDiscoveryNote(IReadOnlyList<HarnessNodeCandidate> nodeCandidates, IReadOnlyList<HarnessDshCandidate> dshCandidates)
    {
        if (nodeCandidates.Count == 0 && dshCandidates.Count == 0)
            return "未发现 Node 与 dsh。这是正常降级场景：请安装 Node.js 与 dsh 后重新检测。";
        if (nodeCandidates.Count == 0)
            return "未发现 Node 可执行文件。这是正常降级场景：请安装 Node.js 后重新检测。";
        if (dshCandidates.Count == 0)
            return "已发现 Node，但未发现 dsh 包。请安装 @deepseek-ai/dsh 后重新检测。";
        return null;
    }

    private static string DescribeSource(HarnessNodeSource source) => source switch
    {
        HarnessNodeSource.UserSelected => "用户指定",
        HarnessNodeSource.CommonLocation => "常见安装位置",
        _ => "PATH"
    };

    private static string DescribeDshSource(HarnessDshSource source) => source switch
    {
        HarnessDshSource.UserSelected => "用户指定",
        HarnessDshSource.CommonLocation => "常见全局 npm 根",
        _ => "PATH"
    };

    private static int SourceRank(HarnessNodeSource source) => source switch
    {
        HarnessNodeSource.UserSelected => 0,
        HarnessNodeSource.CommonLocation => 1,
        _ => 2
    };

    private static int DshSourceRank(HarnessDshSource source) => source switch
    {
        HarnessDshSource.UserSelected => 0,
        HarnessDshSource.CommonLocation => 1,
        _ => 2
    };

    /// <summary>
    /// 默认 Web Host 启动：用绝对 node.exe 运行绝对 dsh 入口启动 `dsh web`，仅监听本机回环。
    /// 不调用裸 dsh，也不依赖 PATH 或 dsh.cmd 能自行找到 node。参数经 ArgumentList 传递。
    /// 启动失败（dsh 未安装/预览协议不可确认）不会误报成功；stdout/stderr 异步读取防死锁。
    /// </summary>
    private static Process? LaunchDshWeb(string nodePath, string dshEntryPath, string? permissionMode = null)
        => DeepSeekHarnessProcess.LaunchWebHost(nodePath, dshEntryPath, permissionMode);
}

/// <summary>进程执行助手：参数安全、异步读取、进程树停止。无 shell 字符串拼接传参。</summary>
public static class DeepSeekHarnessProcess
{
    public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// 构建 Web Host 启动信息：绝对 node.exe + 绝对 dsh 入口运行 `dsh web --host 127.0.0.1`，
    /// 无窗口（CreateNoWindow）、不经过 Shell（UseShellExecute=false）、UTF-8 输出重定向。
    /// 可独立测试（验证隐藏宿主不弹窗等要求）。
    /// </summary>
    public static ProcessStartInfo BuildWebHostStartInfo(string nodePath, string dshEntryPath, string? permissionMode = null)
    {
        var start = new ProcessStartInfo(nodePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dshEntryPath);
        start.ArgumentList.Add("web");
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(DeepSeekHarnessVersions.WebHostBindAddress);
        var permission = HarnessExecutionOptions.NormalizePermission(permissionMode);
        start.Environment["DSH_PERMISSION_MODE"] = permission;
        if (permission == "danger-full-access") start.Environment["DSH_APPROVAL_POLICY"] = "never";
        return start;
    }

    /// <summary>
    /// 启动 Web Host 进程（无窗口、参数安全）；失败返回 null。
    /// stdout/stderr 异步排空，避免管道缓冲阻塞；同时保留有界尾部供启动失败诊断使用，
    /// 不保存完整输出（原始 stderr 绝不写入日志）。
    /// </summary>
    public static Process? LaunchWebHost(string nodePath, string dshEntryPath, string? permissionMode = null)
    {
        try
        {
            var process = new Process { StartInfo = BuildWebHostStartInfo(nodePath, dshEntryPath, permissionMode) };
            if (!process.Start()) return null;
            var output = new HarnessProcessOutput();
            CapturedOutputs.Add(process, output);
            _ = output.DrainAsync(process);
            return process;
        }
        catch { return null; }
    }

    /// <summary>已启动进程的输出捕获表：进程实例弱引用（不泄漏），仅 LaunchWebHost 启动的进程有捕获。</summary>
    private static readonly ConditionalWeakTable<Process, HarnessProcessOutput> CapturedOutputs = new();

    /// <summary>读取进程的输出捕获（仅 LaunchWebHost 启动的进程；未知进程返回 null）。</summary>
    public static HarnessProcessOutput? GetCapturedOutput(Process process)
        => process is not null && CapturedOutputs.TryGetValue(process, out var output) ? output : null;

    /// <summary>等待输出排空完成（有界等待，避免进程刚退出时尾部尚未读完的竞态）。</summary>
    public static async Task WaitOutputDrainedAsync(Process process, TimeSpan timeout)
    {
        var output = GetCapturedOutput(process);
        if (output is null) return;
        try { await Task.WhenAny(output.DrainTask, Task.Delay(timeout)).ConfigureAwait(false); }
        catch { /* 等待失败不阻断诊断 */ }
    }

    /// <summary>运行进程，参数经 ArgumentList 安全传递；带超时与进程树终止，避免死锁/僵尸。</summary>
    public static async Task<ProcessRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        if (timeout is not null)
        {
            var timeoutTask = Task.Delay(timeout.Value, cancellationToken);
            Task completed;
            try { completed = await Task.WhenAny(exitTask, timeoutTask); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            finally { if (!exitTask.IsCompleted) try { process.Kill(entireProcessTree: true); } catch { } }
            if (completed != exitTask)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return new ProcessRunResult(-1, string.Empty, $"命令超时（{timeout.Value.TotalSeconds:0} 秒）后已终止。");
            }
        }
        else await exitTask;
        return new ProcessRunResult(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>
/// Web Host 输出捕获：后台异步排空 stdout/stderr（避免管道缓冲阻塞），仅保留有界尾部
/// （<see cref="HarnessOutputDiagnostics.CaptureTailChars"/>），不保存完整输出。
/// 排空任务与进程生命周期一致；进程退出后尾部即可读取，供启动失败诊断使用。
/// </summary>
public sealed class HarnessProcessOutput
{
    private readonly StringBuilder stdoutTail = new();
    private readonly StringBuilder stderrTail = new();
    private readonly object gate = new();
    private Task? drainTask;

    /// <summary>已捕获的 stdout 有界尾部（原始内容，未清洗/脱敏）。</summary>
    public string StdoutTail { get { lock (gate) return stdoutTail.ToString(); } }

    /// <summary>已捕获的 stderr 有界尾部（原始内容，未清洗/脱敏）。</summary>
    public string StderrTail { get { lock (gate) return stderrTail.ToString(); } }

    /// <summary>排空任务（未开始时为已完成任务）。</summary>
    public Task DrainTask { get { lock (gate) return drainTask ?? Task.CompletedTask; } }

    /// <summary>开始异步排空；同一实例只排空一次。</summary>
    public Task DrainAsync(Process process)
    {
        lock (gate)
        {
            if (drainTask is not null) return drainTask;
            drainTask = DrainCoreAsync(process);
            return drainTask;
        }
    }

    private async Task DrainCoreAsync(Process process)
    {
        var stdout = ReadTailAsync(process.StandardOutput, stdoutTail);
        var stderr = ReadTailAsync(process.StandardError, stderrTail);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
    }

    private async Task ReadTailAsync(StreamReader reader, StringBuilder tail)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0) break;
                lock (gate)
                {
                    tail.Append(buffer, 0, read);
                    if (tail.Length > HarnessOutputDiagnostics.CaptureTailChars)
                        tail.Remove(0, tail.Length - HarnessOutputDiagnostics.CaptureTailChars);
                }
            }
        }
        catch { /* 输出读取失败不阻断宿主 */ }
    }
}

/// <summary>
/// Web Host 启动输出诊断纯函数：清洗（去 ANSI/控制字符）、脱敏（API Key/Authorization/Bearer/credentials 等）、
/// 插件失败分类与中文退出摘要。不依赖进程与文件系统，可独立单元测试。
/// 摘要有界且脱敏，绝不记录或展示敏感值，也绝不写原始完整 stderr。
/// </summary>
public static class HarnessOutputDiagnostics
{
    /// <summary>输出捕获保留的原始尾部字符上限。</summary>
    public const int CaptureTailChars = 4000;
    /// <summary>中文摘要内嵌尾部字符上限。</summary>
    public const int MaxTailChars = 1200;

    /// <summary>插件失败识别结果：包名可缺失（无法提取时为空串）。</summary>
    public sealed record PluginFailure(string Package, string Detail);

    /// <summary>清洗：去除 ANSI 转义序列（CSI/OSC）与控制字符（保留 \n \t \r）。</summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var inEscape = false;
        var osc = false;
        foreach (var c in text)
        {
            if (inEscape)
            {
                if (c == '\u001b') continue;
                if (osc) { if (c == '\u0007') { inEscape = false; osc = false; } continue; }
                if (c == '[') continue;
                if (c == ']') { osc = true; continue; }
                if (c >= 0x40 && c <= 0x7E) { inEscape = false; continue; }
                continue;
            }
            if (c == '\u001b') { inEscape = true; continue; }
            if (c < 0x20 && c != '\n' && c != '\r' && c != '\t') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>脱敏：替换 Authorization/Bearer、api_key/access token/refresh token/credentials/secret/password/token、sk-* 等敏感值。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = Regex.Replace(text, @"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s,;""']+", "$1[REDACTED]");
        result = Regex.Replace(result, @"(?i)((?:api[_-]?key|access[_-]?token|refresh[_-]?token|credentials|secret|password|token)\s*[:=]\s*[""']?)[^\s,;""']+", "$1[REDACTED]");
        result = Regex.Replace(result, @"\bsk-[A-Za-z0-9_-]{8,}\b", "[REDACTED]");
        return result;
    }

    /// <summary>清洗并脱敏（摘要/日志写入前的统一入口）。</summary>
    public static string CleanAndRedact(string text) => Redact(Clean(text));

    /// <summary>
    /// 识别插件加载失败：匹配 "plugin tree failed to load" / "entry did not activate"，
    /// 并尽力从 node_modules 路径或 npm 作用域包名中提取插件包名。
    /// </summary>
    public static PluginFailure? TryDetectPluginFailure(string cleaned)
    {
        if (string.IsNullOrEmpty(cleaned)) return null;
        string? detail = null;
        var hit = false;
        foreach (var rawLine in cleaned.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Contains("plugin tree failed to load", StringComparison.OrdinalIgnoreCase)
                || line.Contains("entry did not activate", StringComparison.OrdinalIgnoreCase))
            {
                hit = true;
                detail = line.Length > 300 ? line[..300] : line;
                break;
            }
        }
        if (!hit) return null;
        return new PluginFailure(ExtractPackageName(cleaned) ?? string.Empty, detail ?? string.Empty);
    }

    /// <summary>构建中文退出摘要（清洗+脱敏+插件分类+有界尾部）。exitCode 为 null 表示未知。</summary>
    public static string BuildExitSummary(int? exitCode, string? stdout, string? stderr)
    {
        var stdoutClean = CleanAndRedact(stdout ?? string.Empty);
        var stderrClean = CleanAndRedact(stderr ?? string.Empty);
        var tail = CombineTail(stdoutClean, stderrClean);
        var codeText = exitCode is null ? "未知" : exitCode.Value.ToString();
        var tailPart = tail.Length == 0 ? string.Empty : " 输出尾部：" + Truncate(tail, MaxTailChars);
        var plugin = TryDetectPluginFailure(stderrClean + Environment.NewLine + stdoutClean);
        if (plugin is not null)
        {
            var package = string.IsNullOrWhiteSpace(plugin.Package) ? "未知插件" : "插件「" + plugin.Package + "」";
            return $"Harness Web Host 启动后提前退出（退出码 {codeText}），检测到插件加载失败（plugin tree failed to load / entry did not activate），可能涉及{package}。请在 DSH Web/Profile 中停用或修复该插件后重试。" + tailPart;
        }
        if (tail.Length == 0)
            return $"Harness Web Host 启动后提前退出（退出码 {codeText}），未产生任何输出。";
        return $"Harness Web Host 启动后提前退出（退出码 {codeText}）。" + tailPart;
    }

    private static string? ExtractPackageName(string cleaned)
    {
        var match = Regex.Match(cleaned, @"node_modules[/\\](@[^/\\]+[/\\][^/\\\s""'()]+|[^/\\\s""'()]+)", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value.Replace('\\', '/');
        match = Regex.Match(cleaned, @"\b@[a-z0-9-]+/[a-z0-9-_.]+", RegexOptions.IgnoreCase);
        if (match.Success) return match.Value;
        match = Regex.Match(cleaned, @"\b[a-z0-9-]*plugin[a-z0-9-]*\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    private static string CombineTail(string stdoutClean, string stderrClean)
    {
        var combined = (string.IsNullOrWhiteSpace(stdoutClean) ? string.Empty : "stdout: " + stdoutClean.Trim() + Environment.NewLine)
                     + (string.IsNullOrWhiteSpace(stderrClean) ? string.Empty : "stderr: " + stderrClean.Trim());
        return Truncate(combined.TrimEnd(), CaptureTailChars);
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return "…" + text[^maxChars..];
    }
}
