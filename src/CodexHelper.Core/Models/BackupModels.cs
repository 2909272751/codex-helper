namespace CodexHelper.Core.Models;

public sealed record BackupSource(
    string Id,
    string DisplayName,
    string Path,
    bool UseDevelopmentExcludes = false,
    IReadOnlyList<string>? AdditionalExcludedDirectoryNames = null);

public sealed class SnapshotManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string CodexHelperVersion { get; set; } = string.Empty;
    public List<SnapshotSource> Sources { get; set; } = new();
    public List<SnapshotFile> Files { get; set; } = new();
    public List<OperationIssue> Issues { get; set; } = new();
    public OperationOutcome Outcome { get; set; }
    public long TotalBytes { get; set; }
    public long NewStoredBytes { get; set; }
}

public sealed record SnapshotSource(string Id, string DisplayName, string OriginalPath);

public sealed record SnapshotFile(
    string SourceId,
    string RelativePath,
    string BlobId,
    string ContentHash,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record SnapshotSummary(
    string Id,
    string Label,
    DateTime CreatedUtc,
    int FileCount,
    long TotalBytes,
    long NewStoredBytes,
    OperationOutcome Outcome,
    int IssueCount);

public sealed class SnapshotIndex
{
    public int SchemaVersion { get; set; } = 1;
    public List<SnapshotSummary> Snapshots { get; set; } = new();
}

public sealed record SnapshotResult(SnapshotManifest Manifest, SnapshotSummary Summary);

public sealed record RestoreRequest(
    string SnapshotId,
    string DestinationRoot,
    IReadOnlyCollection<string>? SourceIds = null,
    bool OverwriteExisting = false);

public sealed record RestoreResult(
    OperationOutcome Outcome,
    int RestoredFiles,
    long RestoredBytes,
    IReadOnlyList<OperationIssue> Issues);

