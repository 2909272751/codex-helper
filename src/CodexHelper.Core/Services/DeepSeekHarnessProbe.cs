using System.Net.Http;
using System.Text.RegularExpressions;

namespace CodexHelper.Core.Services;

/// <summary>官方 Harness 版本参照与常量。Harness 可用性由实际入口与能力探测决定，不再用版本硬白名单。</summary>
public static class DeepSeekHarnessVersions
{
    /// <summary>官方 Harness 包名。</summary>
    public const string FixedPackage = "@deepseek-ai/dsh";
    /// <summary>已知基线版本（开发者预览）。仅用作版本风险分级的参照与诊断，不单独阻止能力通过的新版本。</summary>
    public const string FixedVersion = "0.1.0-rc.5";
    /// <summary>Web Host 默认仅监听本机回环地址。</summary>
    public const string WebHostBindAddress = "127.0.0.1";
    public const int WebHostDefaultPort = 3080;
    public static string WebHostDefaultUrl => $"http://{WebHostBindAddress}:{WebHostDefaultPort}";
    /// <summary>官方 Node.js 下载入口（可复制/打开）。</summary>
    public const string NodeDownloadUrl = "https://nodejs.org/en/download";
    /// <summary>官方 Harness 包下载/查看入口（可复制/打开）。</summary>
    public const string HarnessPackageUrl = "https://www.npmjs.com/package/@deepseek-ai/dsh";
}

/// <summary>
/// Node 版本规则与 Web Host / 中继能力探测。纯静态版本规则可独立测试；
/// Web Host 与中继探测全部可注入，测试不依赖真实 Harness 或真实网络。
/// </summary>
public sealed class DeepSeekHarnessProbe
{
    /// <summary>
    /// Node 最低版本规则：&gt;=22.19.0（LTS 分支）或 &gt;=24.0.0（当前分支）。
    /// 其它主版本（含 23.x 非 LTS）不在白名单内，视为不支持。无法解析视为不支持。
    /// </summary>
    public static bool IsNodeVersionSupported(string? version)
    {
        var parts = ParseVersion(version);
        if (parts is null) return false;
        var (major, minor, patch) = parts.Value;
        if (major == 22) return minor > 19 || (minor == 19 && patch >= 0);
        if (major >= 24) return true;
        return false;
    }

    /// <summary>解析 "v22.19.0" 形如的主/次/修订号；失败返回 null。</summary>
    public static (int Major, int Minor, int Patch)? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var match = Regex.Match(version.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)");
        if (!match.Success) return null;
        if (int.TryParse(match.Groups[1].Value, out var major)
            && int.TryParse(match.Groups[2].Value, out var minor)
            && int.TryParse(match.Groups[3].Value, out var patch))
            return (major, minor, patch);
        return null;
    }

    /// <summary>描述版本规则约束，用于 UI 诊断文案。</summary>
    public static string VersionRuleText =>
        $"Node 需为 {22}.19+（LTS）或 {24}.0+（当前）；dsh 以已知基线 {DeepSeekHarnessVersions.FixedPackage}@{DeepSeekHarnessVersions.FixedVersion} 为参照做版本风险提示，入口完整且能力通过时新版本即可用，禁止静默使用 latest。";

    /// <summary>
    /// 判断已安装的 dsh 版本是否"可探测"：能解析为合法 SemVer 且非 latest。
    /// 版本不再单独阻止启用——入口完整且必要能力通过时，未知新版本也可使用；
    /// 是否可用由能力探测决定，版本只用于风险提示与诊断。
    /// </summary>
    public static bool IsDshVersionSupported(string? version)
        => DeepSeekHarnessSemVer.EvaluateRisk(version) != HarnessVersionRiskLevel.Invalid;
}

/// <summary>解析后的语义版本（SemVer 2.0：主.次.修订[-预发布][+构建]）。</summary>
public sealed record DshSemVersion(int Major, int Minor, int Patch, string PreRelease, string Build)
{
    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(PreRelease)) text += "-" + PreRelease;
        if (!string.IsNullOrEmpty(Build)) text += "+" + Build;
        return text;
    }
}

/// <summary>
/// dsh 版本相对已知基线的风险级别。版本只用于风险提示与诊断，不单独阻止启用；
/// 跨主版本在 UI 中应显示更醒目的警告。
/// </summary>
public enum HarnessVersionRiskLevel
{
    /// <summary>恰好等于已知基线版本。</summary>
    KnownBaseline,
    /// <summary>同主.次系列的新版本（0.1.x 的任意 patch/prerelease）。</summary>
    SameSeries,
    /// <summary>跨 minor 的未验证新版本（同主版本、次版本不同）。</summary>
    NewMinor,
    /// <summary>跨主版本（major 不同）的未验证新版本。</summary>
    CrossMajor,
    /// <summary>非法/损坏或无法解析；"latest" 不得作为实际版本。</summary>
    Invalid
}

/// <summary>Harness 版本语义解析与风险分级。纯静态可独立测试。</summary>
public static partial class DeepSeekHarnessSemVer
{
    private static readonly System.Text.RegularExpressions.Regex SemVerRegex = new(
        @"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>严格解析完整 SemVer（含 prerelease 与 build metadata）；失败返回 null。</summary>
    public static DshSemVersion? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var match = SemVerRegex.Match(version.Trim());
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch))
            return null;
        return new DshSemVersion(major, minor, patch, match.Groups[4].Value, match.Groups[5].Value);
    }

    /// <summary>
    /// 计算 dsh 版本相对基线（默认已知基线 <see cref="DeepSeekHarnessVersions.FixedVersion"/>）的风险级别。
    /// 无法解析或 "latest" 视为 <see cref="HarnessVersionRiskLevel.Invalid"/>。
    /// </summary>
    public static HarnessVersionRiskLevel EvaluateRisk(string? version, string? baseline = null)
    {
        var actual = Parse(version);
        if (actual is null) return HarnessVersionRiskLevel.Invalid;
        var trimmed = version!.Trim();
        if (string.Equals(trimmed, "latest", StringComparison.OrdinalIgnoreCase)) return HarnessVersionRiskLevel.Invalid;
        var baseVersion = Parse(baseline ?? DeepSeekHarnessVersions.FixedVersion);
        if (baseVersion is null) return HarnessVersionRiskLevel.Invalid;
        if (actual.Major == baseVersion.Major && actual.Minor == baseVersion.Minor && actual.Patch == baseVersion.Patch
            && string.Equals(actual.PreRelease, baseVersion.PreRelease, StringComparison.Ordinal)
            && string.Equals(actual.Build, baseVersion.Build, StringComparison.Ordinal))
            return HarnessVersionRiskLevel.KnownBaseline;
        if (actual.Major == baseVersion.Major && actual.Minor == baseVersion.Minor) return HarnessVersionRiskLevel.SameSeries;
        if (actual.Major == baseVersion.Major) return HarnessVersionRiskLevel.NewMinor;
        return HarnessVersionRiskLevel.CrossMajor;
    }

    /// <summary>风险级别的人类可读文案（用于 UI 诊断）。</summary>
    public static string Describe(HarnessVersionRiskLevel risk) => risk switch
    {
        HarnessVersionRiskLevel.KnownBaseline => "已知基线版本",
        HarnessVersionRiskLevel.SameSeries => "同系列新版本",
        HarnessVersionRiskLevel.NewMinor => "跨次版本（未验证）",
        HarnessVersionRiskLevel.CrossMajor => "跨主版本（未验证）",
        _ => "非法或损坏"
    };
}

/// <summary>Harness 中继能力探测结果：任务提交 / 事件流 / 取消是否已由运行时能力探测确认。</summary>
public sealed record HarnessRelayCapability(
    bool SubmitSupported,
    bool EventStreamSupported,
    bool CancelSupported,
    bool Confirmed,
    string Message)
{
    /// <summary>仅当三项能力全部确认时才允许声称"可实时协作"。</summary>
    public bool CanLiveCollaborate => Confirmed && SubmitSupported && EventStreamSupported && CancelSupported;
}

/// <summary>
/// Harness 会话/任务中继抽象。实现只应在"实际完成 Host 能力探测且确认支持任务提交、
/// 事件流与取消"时才报告可实时协作；任何一项无法确认时诚实降级并给出具体原因。
/// 默认实现 <see cref="DeepSeekHarnessRelayProbe"/> 通过 rc.6 原生协议真实探测（见 DeepSeekHarnessRpc.cs）。
/// </summary>
public interface IDeepSeekHarnessRelay
{
    /// <summary>探测 Host 能力。未确认时 Confirmed=false。</summary>
    Task<HarnessRelayCapability> ProbeCapabilitiesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Web Host 端口可达性探测器（本机回环探测，可注入避免真实网络依赖）。</summary>
public sealed class DeepSeekHarnessWebHostProbe
{
    /// <summary>端口连通探测（默认 GET 本机 URL；测试可注入）。</summary>
    public Func<string, int, CancellationToken, Task<bool>>? PortProbe { get; init; }

    public async Task<bool> IsWebHostRunningAsync(string url = "", int port = DeepSeekHarnessVersions.WebHostDefaultPort, CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrWhiteSpace(url) ? DeepSeekHarnessVersions.WebHostDefaultUrl : url;
        try
        {
            if (PortProbe is not null) return await PortProbe(url, port, cancellationToken);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(target, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}
