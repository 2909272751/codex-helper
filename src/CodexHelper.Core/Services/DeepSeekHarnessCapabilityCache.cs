using System.Text;
using System.Text.Json;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// Harness 能力探测结果的通俗状态。用于 UI 通俗文案；跨主版本风险由
/// <see cref="HarnessVersionRiskLevel.CrossMajor"/> 在 UI 中额外醒目提示。
/// </summary>
public enum HarnessStatusKind
{
    /// <summary>尚未完成探测。</summary>
    Unknown,
    /// <summary>入口完整且中继能力全部确认，可实时协作。</summary>
    Usable,
    /// <summary>能打开 Web Host，但自动中继（提交/事件/取消）未全部确认。</summary>
    WebOnly,
    /// <summary>新版本且入口/能力通过探测，可使用。</summary>
    NewVersionVerified,
    /// <summary>新版本但入口/能力未通过探测。</summary>
    NewVersionFailed,
    /// <summary>安装损坏（入口不完整或版本非法/无法解析）。</summary>
    Broken
}

/// <summary>
/// 可扩展的 Harness 能力探测结果：CLI、WebProfile、HostReachable、中继三能力，
/// 以及消息、时间戳与入口指纹。中继能力（RelaySubmit/RelayEvents/RelayCancel）全部确认
/// 才允许宣称"实时协作可用"。
/// </summary>
public sealed record HarnessCapabilityResult(
    bool Cli,
    string CliMessage,
    bool WebProfile,
    string WebProfileMessage,
    bool HostReachable,
    string HostReachableMessage,
    bool RelaySubmit,
    bool RelayEvents,
    bool RelayCancel,
    bool RelayConfirmed,
    string RelayMessage,
    DateTime TimestampUtc,
    string NodeFingerprint,
    string DshFingerprint)
{
    /// <summary>入口是否完整：CLI 与 Web profile 均通过（代表能启动 Web）。</summary>
    public bool EntryComplete => Cli && WebProfile;

    /// <summary>自动中继是否全部确认（提交 + 事件流 + 取消）。</summary>
    public bool RelayCapable => RelayConfirmed && RelaySubmit && RelayEvents && RelayCancel;

    /// <summary>计算通俗状态。isNewVersion 表示 dsh 版本相对基线是否为未验证新版本。</summary>
    public HarnessStatusKind EvaluateStatusKind(bool isNewVersion)
    {
        if (!EntryComplete) return isNewVersion ? HarnessStatusKind.NewVersionFailed : HarnessStatusKind.Broken;
        if (RelayCapable) return isNewVersion ? HarnessStatusKind.NewVersionVerified : HarnessStatusKind.Usable;
        return HarnessStatusKind.WebOnly;
    }
}

/// <summary>
/// Harness 能力探测结果缓存。缓存键至少包含 node 绝对路径、node 文件时间/大小、
/// dsh 入口绝对路径、package.json/入口文件时间/大小与实际版本；升级或文件变化自动失效。
/// 缓存损坏、无权限、旧结构均安全重建（视为未命中重新探测）。绝不在缓存中写入凭据、
/// 环境变量值或任务正文。读取/写入全部可注入，测试不依赖真实文件系统。
/// </summary>
public sealed class DeepSeekHarnessCapabilityCache
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>文件信息读取（默认 FileInfo）。返回 (长度, LastWriteTimeUtc.Ticks)。</summary>
    public Func<string, (long Length, long LastWriteUtcTicks)> FileInfoReader { get; init; } = ReadFileInfo;
    /// <summary>用于把能力结果序列化写入的存储（默认 AtomicFile）。</summary>
    public Action<string, byte[]>? StorageWriter { get; init; }
    /// <summary>读取缓存文件原始字节（默认 File.ReadAllBytes；缺失/失败返回 null）。</summary>
    public Func<string, byte[]?>? StorageReader { get; init; }

    private readonly string filePath;

    public DeepSeekHarnessCapabilityCache(AppPaths paths)
    {
        filePath = Path.Combine(paths.BaseDirectory, "harness-capability.json");
    }

    public DeepSeekHarnessCapabilityCache(string filePath)
    {
        this.filePath = filePath;
    }

    /// <summary>
    /// 计算缓存键与入口指纹。指纹覆盖 node/dsh 入口路径、文件时间/大小、package.json 信息及实际版本。
    /// </summary>
    public (string Key, string NodeFingerprint, string DshFingerprint) BuildFingerprint(
        string nodePath, string dshEntryPath, string dshVersion, string nodeVersion)
    {
        var nodeInfo = FileInfoReader(nodePath);
        var nodeFingerprint = Fingerprint(nodePath, nodeInfo.Length, nodeInfo.LastWriteUtcTicks, nodeVersion);
        var dshEntryInfo = FileInfoReader(dshEntryPath);
        var packageJson = ResolvePackageRoot(dshEntryPath) is { } root
            ? Path.Combine(root, "package.json")
            : string.Empty;
        var packageInfo = string.IsNullOrEmpty(packageJson)
            ? (Length: 0L, LastWriteUtcTicks: 0L)
            : FileInfoReader(packageJson);
        var dshFingerprint = Fingerprint(dshEntryPath, dshEntryInfo.Length, dshEntryInfo.LastWriteUtcTicks,
            packageInfo.Length, packageInfo.LastWriteUtcTicks, dshVersion);
        var key = Fingerprint("capability-v1", nodePath, nodeInfo.Length, nodeInfo.LastWriteUtcTicks,
            dshEntryPath, dshEntryInfo.Length, dshEntryInfo.LastWriteUtcTicks,
            packageInfo.Length, packageInfo.LastWriteUtcTicks, dshVersion, nodeVersion);
        return (key, nodeFingerprint, dshFingerprint);
    }

    /// <summary>按键读取缓存能力结果；命中且为当前 schema 时返回 true。损坏/缺失/旧结构返回 false（安全重建）。</summary>
    public bool TryLoad(string key, out HarnessCapabilityResult result)
    {
        result = null!;
        try
        {
            var raw = StorageReader?.Invoke(filePath) ?? (File.Exists(filePath) ? File.ReadAllBytes(filePath) : null);
            if (raw is null || raw.Length == 0) return false;
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("version", out var versionElement)
                || versionElement.GetInt32() != SchemaVersion) return false;
            if (!document.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Object) return false;
            if (!entries.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object) return false;
            if (!entry.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object) return false;
            var parsed = caps.Deserialize<HarnessCapabilityResult>(Options);
            if (parsed is null) return false;
            result = parsed;
            return true;
        }
        catch { return false; }
    }

    /// <summary>写入（或更新）某个键的能力结果，原子落盘。绝不写入凭据/环境变量/任务正文。</summary>
    public void Store(string key, HarnessCapabilityResult capabilities)
    {
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            var raw = StorageReader?.Invoke(filePath) ?? (File.Exists(filePath) ? File.ReadAllBytes(filePath) : null);
            if (raw is not null && raw.Length > 0)
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.TryGetProperty("entries", out var existing)
                    && existing.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in existing.EnumerateObject())
                        entries[property.Name] = ReadEntry(property.Value);
                }
            }
        }
        catch { entries.Clear(); } // 损坏/无权限 → 安全重建

        var entry = new Dictionary<string, object>
        {
            ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
            ["capabilities"] = capabilities
        };
        entries[key] = entry;
        var payload = new Dictionary<string, object>
        {
            ["version"] = SchemaVersion,
            ["entries"] = entries
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Options);
        if (StorageWriter is not null) StorageWriter(filePath, json);
        else AtomicFile.WriteAllBytes(filePath, json);
    }

    private static object ReadEntry(JsonElement element)
    {
        var timestamp = element.TryGetProperty("timestampUtc", out var t) ? t.GetString() ?? "" : "";
        return new Dictionary<string, object>
        {
            ["timestampUtc"] = timestamp,
            ["capabilities"] = element.TryGetProperty("capabilities", out var c) ? c : JsonDocument.Parse("{}").RootElement
        };
    }

    private static string? ResolvePackageRoot(string entry)
    {
        var lib = Path.GetDirectoryName(entry);
        return string.IsNullOrEmpty(lib) ? null : Path.GetDirectoryName(lib);
    }

    private static (long Length, long LastWriteUtcTicks) ReadFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? (Length: info.Length, LastWriteUtcTicks: info.LastWriteTimeUtc.Ticks)
                : (Length: 0L, LastWriteUtcTicks: 0L);
        }
        catch { return (Length: 0L, LastWriteUtcTicks: 0L); }
    }

    private static string Fingerprint(params object?[] parts)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var sb = new StringBuilder();
        foreach (var part in parts) sb.Append(part?.ToString()).Append('\u001f');
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
