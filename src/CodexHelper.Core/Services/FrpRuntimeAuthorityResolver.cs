using System.Net;

namespace CodexHelper.Core.Services;

/// <summary>从本机 88frp 运行时配置中安全解析转发到 DSH 回环 Web 端口的公网 authority。</summary>
public static class FrpRuntimeAuthorityResolver
{
    public static string? TryResolveDshWebAuthority(string? instancesRoot = null)
    {
        var authorities = ResolveDshWebAuthorities(instancesRoot);
        return authorities.Count == 1 ? authorities[0] : null;
    }

    /// <summary>
    /// 解析全部候选 authority（去重、最多 2 个）：0 个表示缺失/不完整，2 个表示多实例歧义。
    /// 调用方据此决定是否保留既有信任，绝不因暂时读不到配置而清空已生效信任。
    /// </summary>
    public static IReadOnlyList<string> ResolveDshWebAuthorities(string? instancesRoot = null)
    {
        try
        {
            instancesRoot ??= DefaultInstancesRoot();
            if (!Directory.Exists(instancesRoot)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(instancesRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(ReadAuthority)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>88frp 实例根目录（%LOCALAPPDATA%\88frp-node\data\instances）。</summary>
    public static string DefaultInstancesRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "88frp-node", "data", "instances");

    private static string? ReadAuthority(string folder)
    {
        try { return TryParseRuntimeToml(File.ReadAllText(Path.Combine(folder, "runtime-frpc.toml"))); }
        catch { return null; }
    }

    /// <summary>解析单份受控 runtime-frpc.toml；只取 serverAddr 与 loopback:3080 TCP proxy，不保留其他字段。</summary>
    public static string? TryParseRuntimeToml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? serverAddr = null;
        var matches = new List<string>();
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void CommitBlock()
        {
            if (!block.TryGetValue("type", out var type) || !string.Equals(type, "tcp", StringComparison.OrdinalIgnoreCase)
                || !block.TryGetValue("localIP", out var localIp) || !IPAddress.TryParse(localIp, out var ip) || !IPAddress.IsLoopback(ip)
                || !block.TryGetValue("localPort", out var localPort) || !string.Equals(localPort, "3080", StringComparison.Ordinal)
                || !block.TryGetValue("remotePort", out var remotePort) || !int.TryParse(remotePort, out var port)) return;
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
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static string? TryBuildAuthority(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return null;
        host = host.Trim();
        if (host.Contains("/", StringComparison.Ordinal) || host.Contains("@", StringComparison.Ordinal) || host.Any(char.IsWhiteSpace)) return null;
        var bare = host.Trim('[', ']');
        if (IPAddress.TryParse(bare, out var ip)) return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
        return Uri.CheckHostName(bare) == UriHostNameType.Dns ? bare.ToLowerInvariant() + ":" + port : null;
    }
}
