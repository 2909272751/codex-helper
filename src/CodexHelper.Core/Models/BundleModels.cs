namespace CodexHelper.Core.Models;

public sealed record BundleExportItem(
    string Id,
    string Category,
    string DisplayName,
    string Path);

public sealed record BundleExportRequest(
    string DestinationPath,
    string Password,
    IReadOnlyList<BundleExportItem> Items,
    IReadOnlyList<BundleVirtualFile>? VirtualFiles = null);

public sealed record BundleVirtualFile(
    string ItemId,
    string Category,
    string DisplayName,
    string RelativePath,
    byte[] Content,
    DateTime LastWriteTimeUtc);

public sealed record BundleVirtualContent(
    BundleManifestItem Item,
    BundleFileEntry File,
    byte[] Content);

public sealed class BundleManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string BundleId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string CodexHelperVersion { get; set; } = string.Empty;
    public List<BundleManifestItem> Items { get; set; } = new();
    public List<BundleFileEntry> Files { get; set; } = new();
    public List<OperationIssue> Issues { get; set; } = new();
}

public sealed record BundleManifestItem(
    string Id,
    string Category,
    string DisplayName,
    string OriginalPath,
    int FileCount,
    long TotalBytes);

public sealed record BundleFileEntry(
    string ItemId,
    string RelativePath,
    string ContentHash,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record BundlePreview(
    BundleManifest Manifest,
    long EncryptedSize,
    bool PasswordVerified);

public sealed record BundleImportRequest(
    string BundlePath,
    string Password,
    string DestinationRoot,
    IReadOnlyCollection<string>? SelectedItemIds = null,
    bool OverwriteExisting = false);

public sealed record BundleImportResult(
    OperationOutcome Outcome,
    int ImportedFiles,
    long ImportedBytes,
    IReadOnlyList<OperationIssue> Issues);
