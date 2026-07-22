namespace CodexHelper.Core.Models;

public enum ProtectionKind
{
    Critical,
    Recommended,
    Optional,
    Reinstallable,
    Runtime
}

public sealed record DataInventoryItem(
    string Id,
    string DisplayName,
    string Path,
    ProtectionKind Kind,
    bool IncludedByDefault,
    long SizeBytes,
    int FileCount,
    string Description,
    bool Exists);

public sealed record ProjectInfo(
    string Name,
    string Path,
    bool IsGitRepository,
    bool HasAgentsFile,
    bool HasCodexConfig,
    DateTime LastWriteTimeUtc,
    bool IsProtected);

public sealed record OperationProgress(
    string Stage,
    string CurrentItem,
    long CompletedItems,
    long TotalItems,
    long ProcessedBytes,
    string Message);

public enum OperationOutcome
{
    Success,
    PartialSuccess,
    Failed,
    Cancelled
}

public sealed record OperationIssue(string Item, string Message, bool CanRetry);

