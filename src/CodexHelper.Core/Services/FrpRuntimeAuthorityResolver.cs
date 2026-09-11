using System.Net;

namespace CodexHelper.Core.Services;

/// <summary>从本机 88frp 运行时配置中安全解析转发到 DSH 回环 Web 端口的公网 authority。</summary>
public static class FrpRuntimeAuthorityResolver
{
    private static readonly string[] InstancesRelativeSegments = ["88frp-node", "data", "instances"];

    /// <summary>
    /// 活动 88FRP 探测开关（默认开启）。关闭时保持"静止双根扫描"的旧行为，
    /// 便于受限环境显式回退；<paramref name="instancesRoot"/> 显式路径场景本来就不探测。
    /// </summary>
    public static bool ActiveProbeEnabled { get; set; } = true;

    /// <summary>活动配置探测注入点（默认 <see cref="ActiveFrpRuntimeProbe"/>；测试可注入确定实现）。</summary>
    public static Func<CancellationToken, Task<ProbeResult>> ActiveProbe { get; set; }
        = static token => ActiveFrpRuntimeProbe.ResolveActiveConfigPathsAsync(token);

    /// <summary>静止根清单注入点（默认 <see cref="ResolveInstanceRoots"/>；测试可注入隔离根，不触碰真实 AppData）。</summary>
    public static Func<IReadOnlyList<string>> StaticInstanceRootsResolver { get; set; }
        = static () => ResolveInstanceRoots();

    /// <summary>
    /// 解析全部候选 authority（跨根去重、最多 2 个）：0 个表示缺失/不完整，2 个表示多实例歧义。
    /// 调用方据此决定是否保留既有信任，绝不因暂时读不到配置而清空已生效信任。
    /// <paramref name="instancesRoot"/> 非空时只扫描该显式根（测试隔离），不追加默认根、不查询真实进程。
    /// </summary>
    public static IReadOnlyList<string> ResolveDshWebAuthorities(string? instancesRoot = null)
        => ResolveDshWebAuthoritiesAsync(instancesRoot).GetAwaiter().GetResult().Authorities;

    /// <summary>
    /// 解析活动 88FRP 入口并给出可区分结果（活动唯一 / 活动歧义 / 活动但不可读 / 查不到活动进程 /
    /// 静态双根回退）。<see cref="DshWebAuthorityResolution.Attempted"/> 只表示"确实查到了活动 88FRP 进程"。
    /// </summary>
    public static Task<DshWebAuthorityResolution> ResolveDshWebAuthoritiesAsync(
        string? instancesRoot = null,
        CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            try
            {
                // 显式根 = 测试隔离语义：只扫描该目录，绝不查询真实进程。
                if (!string.IsNullOrWhiteSpace(instancesRoot))
                {
                    var explicitAuthorities = ScanStaticRoots([Path.GetFullPath(instancesRoot)]);
                    return explicitAuthorities.Count == 0
                        ? DshWebAuthorityResolution.Missing
                        : new DshWebAuthorityResolution(explicitAuthorities, false, DshWebAuthoritySource.Missing);
                }

                if (ActiveProbeEnabled)
                {
                    ProbeResult probe;
                    try { probe = await ActiveProbe(cancellationToken).ConfigureAwait(false) ?? ProbeResult.NotAttempted; }
                    catch (OperationCanceledException) { throw; }
                    catch { probe = ProbeResult.NotAttempted; }

                    if (probe.Attempted && probe.Paths.Count > 0)
                    {
                        // 有活动进程：只认其 -c/--config 实际指向的配置，忽略静止旧目录（旧 IP 不得覆盖新配置）。
                        var active = new List<string>();
                        var unreadable = false;
                        foreach (var path in probe.Paths)
                        {
                            // 去重后已得到 2 个不同 authority：歧义已确定，无需继续读。
                            if (active.Count == 2) break;
                            var read = ReadRuntimeConfig(path);
                            if (read.Status == RuntimeConfigReadStatus.Unreadable) { unreadable = true; continue; }
                            // 可读但不是 DSH 隧道（有效 serverAddr + 完整 proxy 结构）：不构成候选，也不代表未知。
                            if (read.Status == RuntimeConfigReadStatus.ValidNonDsh) continue;
                            if (!active.Contains(read.Authority!, StringComparer.OrdinalIgnoreCase)) active.Add(read.Authority!);
                        }

                        // 任何活动文件读失败/超限/结构不完整都"无法判断"：绝不能回退静止旧 IP，
                        // 也绝不能"仅凭另一条部分成功"就给出唯一 authority。
                        if (unreadable && active.Count < 2)
                            return new DshWebAuthorityResolution(Array.Empty<string>(), true, DshWebAuthoritySource.ActiveUnreadable);

                        if (active.Count > 0)
                            return new DshWebAuthorityResolution(active, true, DshWebAuthoritySource.Active);

                        // 活动配置全部可读、但没有一条是 DSH 隧道：交给静止双根回退（绝不猜测）。
                    }
                }

                var authorities = ScanStaticRoots(StaticInstanceRootsResolver() ?? Array.Empty<string>());
                return authorities.Count == 0
                    ? DshWebAuthorityResolution.Missing
                    : new DshWebAuthorityResolution(authorities, false, DshWebAuthoritySource.Missing);
            }
            catch (OperationCanceledException) { throw; }
            catch { return DshWebAuthorityResolution.Missing; }
        }, cancellationToken);

    /// <summary>唯一 authority；0 个（缺失）或 2 个（歧义）时返回 null，绝不猜测。</summary>
    public static string? TryResolveDshWebAuthority(string? instancesRoot = null)
    {
        var authorities = ResolveDshWebAuthorities(instancesRoot);
        return authorities.Count == 1 ? authorities[0] : null;
    }

    /// <summary>
    /// 默认 88frp 实例根目录候选：当前用户 <c>%LOCALAPPDATA%\88frp-node\data\instances</c>
    /// 与系统服务版 <c>%PROGRAMDATA%\88frp-node\data\instances</c>（88frp 系统服务把实例放在
    /// CommonApplicationData 下，只查用户根会漏掉服务版实例）。保持顺序并去重、忽略空值。
    /// </summary>
    public static IReadOnlyList<string> ResolveInstanceRoots()
        => BuildInstanceRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    /// <summary>纯函数：由两个应用数据根构造实例根列表（空值忽略、大小写去重、保持顺序）。</summary>
    public static IReadOnlyList<string> BuildInstanceRoots(string? localApplicationData, string? commonApplicationData)
    {
        var roots = new List<string>();
        foreach (var dataRoot in new[] { localApplicationData, commonApplicationData })
        {
            if (string.IsNullOrWhiteSpace(dataRoot)) continue;
            string root;
            try { root = Path.GetFullPath(Path.Combine(dataRoot.Trim(), Path.Combine(InstancesRelativeSegments))); }
            catch { continue; }
            if (roots.Contains(root, StringComparer.OrdinalIgnoreCase)) continue;
            roots.Add(root);
        }
        return roots;
    }

    /// <summary>该路径是否是 88frp 默认实例根之一（供诊断区分“未安装”与“读取失败”）。</summary>
    public static bool IsDefaultInstanceRoot(string root)
        => ResolveInstanceRoots().Any(candidate => string.Equals(Path.GetFullPath(root), candidate, StringComparison.OrdinalIgnoreCase));

    /// <summary>静止根扫描（"查不到活动进程"时的保守回退；单根失败不影响其他根）。</summary>
    private static List<string> ScanStaticRoots(IReadOnlyList<string> roots)
    {
        var authorities = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    var authority = ReadAuthority(folder);
                    if (string.IsNullOrWhiteSpace(authority)) continue;
                    if (authorities.Contains(authority, StringComparer.OrdinalIgnoreCase)) continue;
                    authorities.Add(authority);
                    if (authorities.Count == 2) return authorities;
                }
            }
            catch { /* 单根失败跳过，继续下一个根 */ }
        }
        return authorities;
    }

    private static string? ReadAuthority(string folder)
    {
        try { return TryParseRuntimeToml(File.ReadAllText(Path.Combine(folder, "runtime-frpc.toml"))); }
        catch { return null; }
    }

    /// <summary>
    /// 读取活动进程实际使用的配置（只读）。返回值把"读不了/无法判断"与"可读但不是 DSH 配置"
    /// 明确分开：前者必须保持未知，后者才允许静止双根保守回退。
    /// </summary>
    internal static RuntimeConfigRead ReadRuntimeConfig(string file)
    {
        string text;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 1024 * 1024) return new RuntimeConfigRead(RuntimeConfigReadStatus.Unreadable, null);
            text = File.ReadAllText(file);
        }
        catch { return new RuntimeConfigRead(RuntimeConfigReadStatus.Unreadable, null); }

        var parsed = ParseRuntimeToml(text);
        // 空文件、只有一半字段、结构不完整都"无法判断"，绝不能当成"可读的非 DSH 配置"。
        if (!parsed.StructurallyValid) return new RuntimeConfigRead(RuntimeConfigReadStatus.Unreadable, null);
        return parsed.Authority is null
            ? new RuntimeConfigRead(RuntimeConfigReadStatus.ValidNonDsh, null)
            : new RuntimeConfigRead(RuntimeConfigReadStatus.Dsh, parsed.Authority);
    }

    /// <summary>解析单份受控 runtime-frpc.toml；只取 serverAddr 与 loopback:3080 TCP proxy，不保留其他字段。</summary>
    public static string? TryParseRuntimeToml(string? text) => ParseRuntimeToml(text).Authority;

    /// <summary>
    /// 完整解析：区分"结构完整可读"与"读不出结论"。结构完整 = 有非空 <c>serverAddr</c>
    /// 且至少一个字段齐全（type/localIP/localPort/remotePort）的 <c>[[proxies]]</c> 块。
    /// </summary>
    internal static RuntimeTomlParse ParseRuntimeToml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new RuntimeTomlParse(false, null);
        string? serverAddr = null;
        var matches = new List<string>();
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var completeBlocks = 0;
        void CommitBlock()
        {
            if (block.Count == 0) return;
            if (!IsCompleteProxyBlock(block)) return;
            completeBlocks++;
            if (!string.Equals(block["type"], "tcp", StringComparison.OrdinalIgnoreCase)) return;
            if (!IPAddress.TryParse(block["localIP"], out var ip) || !IPAddress.IsLoopback(ip)) return;
            if (!string.Equals(block["localPort"], "3080", StringComparison.Ordinal)) return;
            if (!int.TryParse(block["remotePort"], out var port)) return;
            var authority = TryBuildAuthority(serverAddr, port);
            if (authority is not null) matches.Add(authority);
        }
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (string.Equals(line, "[[proxies]]", StringComparison.OrdinalIgnoreCase)) { CommitBlock(); block.Clear(); continue; }
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Split('#', 2)[0].Trim().Trim('\'', '"');
            if (block.Count == 0 && string.Equals(key, "serverAddr", StringComparison.OrdinalIgnoreCase)) serverAddr = value;
            else block[key] = value;
        }
        CommitBlock();
        var distinct = matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var structurallyValid = !string.IsNullOrWhiteSpace(serverAddr) && completeBlocks > 0;
        return new RuntimeTomlParse(structurallyValid, distinct.Length == 1 ? distinct[0] : null);
    }

    /// <summary>proxy 块字段是否齐全（type 非空、localIP 可解析为 IP、两个端口都是整数）。</summary>
    private static bool IsCompleteProxyBlock(IReadOnlyDictionary<string, string> block)
        => block.TryGetValue("type", out var type) && !string.IsNullOrWhiteSpace(type)
        && block.TryGetValue("localIP", out var localIp) && IPAddress.TryParse(localIp, out _)
        && block.TryGetValue("localPort", out var localPort) && int.TryParse(localPort, out _)
        && block.TryGetValue("remotePort", out var remotePort) && int.TryParse(remotePort, out _);

    private static string? TryBuildAuthority(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return null;
        host = host.Trim();
        if (host.Contains("/", StringComparison.Ordinal) || host.Contains("@", StringComparison.Ordinal) || host.Any(char.IsWhiteSpace)) return null;
        var bare = host.Trim('[', ']');
        if (IPAddress.TryParse(bare, out var ip)) return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
        return Uri.CheckHostName(bare) == UriHostNameType.Dns ? bare.ToLowerInvariant() + ":" + port : null;
    }

    /// <summary>单份活动配置的读取结论（三态：无法判断 / 可读但非 DSH / DSH）。</summary>
    internal enum RuntimeConfigReadStatus
    {
        /// <summary>不存在、读失败、超限或结构不完整：<b>无法判断</b>，调用方必须保持未知。</summary>
        Unreadable,
        /// <summary>结构完整可读，但不是指向本机回环 3080 的 DSH 隧道。</summary>
        ValidNonDsh,
        /// <summary>结构完整可读，且给出唯一 DSH 回环 authority。</summary>
        Dsh
    }

    /// <summary>一次活动配置读取的结论与（若有）authority。</summary>
    internal readonly record struct RuntimeConfigRead(RuntimeConfigReadStatus Status, string? Authority);

    /// <summary>一次 runtime-frpc.toml 解析：结构是否完整可读 + 唯一的 DSH authority（可能为 null）。</summary>
    internal readonly record struct RuntimeTomlParse(bool StructurallyValid, string? Authority);
}

/// <summary>本次解析的入口来源（诊断/状态用；<see cref="ActiveUnreadable"/> 表示必须保留既有信任，绝不回退旧 IP）。</summary>
public enum DshWebAuthoritySource
{
    /// <summary>未查到活动 88FRP 进程（或未探测）：按静止双根扫描/缺失处理。</summary>
    Missing,
    /// <summary>来自活动 88FRP/frpc 进程实际使用的配置。</summary>
    Active,
    /// <summary>存在活动配置路径但全部（或部分且不足以判定）不可读/写入中：返回缺失，绝不回退静止旧目录。</summary>
    ActiveUnreadable
}

/// <summary>
/// 一次 88FRP 入口解析结果：<paramref name="Attempted"/> 为 true 表示确实查到了活动 88FRP 进程
/// （此时 <paramref name="Authorities"/> 为空只可能是该活动配置不可读，调用方必须保留既有信任）。
/// </summary>
public sealed record DshWebAuthorityResolution(
    IReadOnlyList<string> Authorities,
    bool Attempted,
    DshWebAuthoritySource Source)
{
    /// <summary>未检测到任何入口（含未探测/无活动进程且静止根为空）。</summary>
    public static DshWebAuthorityResolution Missing { get; } = new(Array.Empty<string>(), false, DshWebAuthoritySource.Missing);
}
