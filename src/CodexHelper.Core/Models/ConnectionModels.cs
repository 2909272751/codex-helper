namespace CodexHelper.Core.Models;

public enum ConnectionKind
{
    OfficialAccount,
    CustomApi,
    Sub2Api,
    ResponsesSubagent,
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
    public bool IsDefaultSubagent { get; set; }
    public int AgentContextWindow { get; set; } = 128_000;
    public bool RequiresAttention { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string QuotaSummary { get; set; } = string.Empty;
    public DateTime? QuotaCheckedUtc { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public decimal? PrimaryUsedPercent { get; set; }
    public decimal? SecondaryUsedPercent { get; set; }
    public DateTime? PrimaryResetsAtUtc { get; set; }
    public DateTime? SecondaryResetsAtUtc { get; set; }
    public string DisplayTarget => Kind == ConnectionKind.OfficialAccount ? IdentityHint : BaseUrl;
    public string DisplayKind => Kind switch
    {
        ConnectionKind.OfficialAccount => "官方账号",
        ConnectionKind.CustomApi => "原生 Responses API",
        ConnectionKind.Sub2Api => "Sub2API",
        ConnectionKind.ResponsesSubagent => "旧版子智能体档案",
        ConnectionKind.LegacyAgentProfile => "旧版档案",
        _ => Kind.ToString()
    };
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

public sealed class AccountHealthHistoryEntry
{
    public string ProfileId { get; set; } = string.Empty;
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
    public bool IsAvailable { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public decimal? PrimaryUsedPercent { get; set; }
    public decimal? SecondaryUsedPercent { get; set; }
}

public sealed class AccountHealthHistoryStore { public List<AccountHealthHistoryEntry> Entries { get; set; } = new(); }
