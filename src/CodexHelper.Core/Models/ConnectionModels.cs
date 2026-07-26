namespace CodexHelper.Core.Models;

public enum ConnectionKind
{
    OfficialAccount,
    CustomApi,
    Sub2Api,
    /// <summary>Read-only compatibility value left by an early dual-agent preview.</summary>
    LegacyAgentProfile
}

/// <summary>External account-file layouts that Codex Helper can exchange.</summary>
public enum OfficialAccountExportFormat
{
    OfficialCodexJson,
    CpaJson,
    Sub2ApiJson
}

public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = string.Empty;
    public ConnectionKind Kind { get; set; }
    public string IdentityHint { get; set; } = string.Empty;
    public string IdentityHash { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastVerifiedUtc { get; set; }
    public bool IsActive { get; set; }
    public bool RequiresAttention { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string QuotaSummary { get; set; } = string.Empty;
    public DateTime? QuotaCheckedUtc { get; set; }
    public string DisplayTarget => Kind == ConnectionKind.OfficialAccount ? IdentityHint : BaseUrl;
    public string DisplayQuota => string.IsNullOrWhiteSpace(QuotaSummary) ? "未查询" : QuotaSummary;
}

public sealed class ConnectionIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string ActiveProfileId { get; set; } = string.Empty;
    public List<ConnectionProfile> Profiles { get; set; } = new();
}

public sealed record CodexProcessInfo(int Id, string Name);

public sealed record OfficialJsonExportResult(int ExportedCount, IReadOnlyList<string> Paths, OfficialAccountExportFormat Format = OfficialAccountExportFormat.OfficialCodexJson);

public sealed record OfficialJsonImportResult(int ImportedCount, int NewProfiles);

public sealed record OfficialAccountUsage(
    string Summary,
    string Plan,
    decimal? PrimaryUsedPercent,
    decimal? SecondaryUsedPercent,
    DateTime? PrimaryResetsAtUtc,
    DateTime? SecondaryResetsAtUtc);
