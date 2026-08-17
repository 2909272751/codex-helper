using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;
using CodexHelper.Core.Security;

namespace CodexHelper.Core.Services;

public sealed class BackupRepository
{
    private static readonly HashSet<string> DevelopmentExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".gradle", ".idea", ".vs", ".vscode", "bin", "obj", "build", "target",
        ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".next", ".nuxt", ".cache"
    };

    private readonly string root;
    private readonly string metadataPath;
    private readonly string indexPath;
    private readonly string snapshotsDirectory;
    private readonly string blobsDirectory;
    private readonly string lockPath;

    public BackupRepository(string repositoryRoot)
    {
        root = Path.GetFullPath(repositoryRoot);
        metadataPath = Path.Combine(root, "repository.json");
        indexPath = Path.Combine(root, "index.chidx");
        snapshotsDirectory = Path.Combine(root, "snapshots");
        blobsDirectory = Path.Combine(root, "blobs");
        lockPath = Path.Combine(root, ".write.lock");
    }

    public string Root => root;

    public void Initialize()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(snapshotsDirectory);
        Directory.CreateDirectory(blobsDirectory);
        if (File.Exists(metadataPath)) return;

        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var metadata = new RepositoryMetadata
            {
                RepositoryId = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                ProtectedMasterKey = Convert.ToBase64String(DpapiProtector.Protect(key))
            };
            new JsonStore().Save(metadataPath, metadata);
            SaveEncrypted(indexPath, new SnapshotIndex(), key, "snapshot-index");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public IReadOnlyList<SnapshotSummary> ListSnapshots()
    {
        var key = LoadMasterKey();
        try { return LoadIndex(key).Snapshots.OrderByDescending(item => item.CreatedUtc).ToList(); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public SnapshotManifest LoadManifest(string snapshotId)
    {
        ValidateIdentifier(snapshotId);
        var key = LoadMasterKey();
        try
        {
            return LoadEncrypted<SnapshotManifest>(Path.Combine(snapshotsDirectory, snapshotId + ".chsnap"), key, snapshotId);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<SnapshotResult> CreateSnapshotAsync(
        string label,
        IReadOnlyList<BackupSource> sources,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0) throw new InvalidOperationException("没有可备份的数据源。");
        PathSafety.EnsureRepositoryOutsideSources(root, sources.Select(item => item.Path));
        Initialize();

        await using var repositoryLock = AcquireLock();
        var key = LoadMasterKey();
        var snapshotId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        var manifest = new SnapshotManifest
        {
            Id = snapshotId,
            Label = string.IsNullOrWhiteSpace(label) ? "手动保护" : label.Trim(),
            CreatedUtc = DateTime.UtcNow,
            DeviceName = Environment.MachineName,
            CodexHelperVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            Sources = sources.Select(source => new SnapshotSource(source.Id, source.DisplayName, Path.GetFullPath(source.Path))).ToList()
        };

        try
        {
            var files = EnumerateSources(sources, manifest.Issues, cancellationToken).ToList();
            long processed = 0;
            long newStored = 0;
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = files[index];
                progress?.Report(new OperationProgress("备份", candidate.FullPath, index, files.Count, processed, $"正在保护 {candidate.Source.DisplayName}"));
                try
                {
                    var infoBefore = new FileInfo(candidate.FullPath);
                    var hash = await ComputeStableHashAsync(candidate.FullPath, cancellationToken);
                    var blobId = Convert.ToHexString(HMACSHA256.HashData(key, hash)).ToLowerInvariant();
                    var blobPath = GetBlobPath(blobId);
                    if (!File.Exists(blobPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
                        var temporary = blobPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            await ChunkedEncryptedFile.EncryptWithKeyAsync(candidate.FullPath, temporary, key, cancellationToken);
                            if (!File.Exists(blobPath)) File.Move(temporary, blobPath);
                            else File.Delete(temporary);
                            newStored += infoBefore.Length;
                        }
                        finally
                        {
                            if (File.Exists(temporary)) File.Delete(temporary);
                        }
                    }

                    manifest.Files.Add(new SnapshotFile(
                        candidate.Source.Id,
                        candidate.RelativePath,
                        blobId,
                        Convert.ToHexString(hash).ToLowerInvariant(),
                        infoBefore.Length,
                        infoBefore.LastWriteTimeUtc));
                    processed += infoBefore.Length;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    manifest.Issues.Add(new OperationIssue(candidate.FullPath, SafeMessage(ex), true));
                }
            }

            manifest.TotalBytes = manifest.Files.Sum(file => file.Length);
            manifest.NewStoredBytes = newStored;
            manifest.Outcome = manifest.Files.Count == 0
                ? OperationOutcome.Failed
                : manifest.Issues.Count == 0 ? OperationOutcome.Success : OperationOutcome.PartialSuccess;

            SaveEncrypted(Path.Combine(snapshotsDirectory, snapshotId + ".chsnap"), manifest, key, snapshotId);
            var summary = new SnapshotSummary(snapshotId, manifest.Label, manifest.CreatedUtc, manifest.Files.Count, manifest.TotalBytes, newStored, manifest.Outcome, manifest.Issues.Count);
            var indexData = LoadIndex(key);
            indexData.Snapshots.Add(summary);
            SaveEncrypted(indexPath, indexData, key, "snapshot-index");
            progress?.Report(new OperationProgress("完成", string.Empty, files.Count, files.Count, processed, "快照已完成并验证清单。"));
            return new SnapshotResult(manifest, summary);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var manifest = LoadManifest(request.SnapshotId);
        var selected = request.SourceIds is { Count: > 0 }
            ? manifest.Files.Where(file => request.SourceIds.Contains(file.SourceId)).ToList()
            : manifest.Files;
        var issues = new List<OperationIssue>();
        var restored = 0;
        long restoredBytes = 0;
        var key = LoadMasterKey();
        try
        {
            for (var index = 0; index < selected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = selected[index];
                var sourceTarget = request.SourceTargetMap is not null && request.SourceTargetMap.TryGetValue(file.SourceId, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
                    ? mapped
                    : file.SourceId;
                var destination = PathSafety.CombineWithin(destinationRoot, Path.Combine(sourceTarget, file.RelativePath));
                progress?.Report(new OperationProgress("恢复", file.RelativePath, index, selected.Count, restoredBytes, "正在解密并校验文件"));
                try
                {
                    if (File.Exists(destination))
                    {
                        if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                        {
                            issues.Add(new OperationIssue(destination, "目标文件是符号链接或重解析点，已拒绝覆盖。", false));
                            continue;
                        }
                        if (!request.OverwriteExisting)
                        {
                            issues.Add(new OperationIssue(destination, "目标已存在，已安全跳过。", true));
                            continue;
                        }
                    }
                    var reparseDirectory = FindReparsePointDirectory(destinationRoot, destination);
                    if (reparseDirectory is not null)
                    {
                        issues.Add(new OperationIssue(destination, $"目标路径包含符号链接/重解析点目录，已拒绝：{reparseDirectory}", false));
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".restore.tmp";
                    try
                    {
                        await ChunkedEncryptedFile.DecryptWithKeyAsync(GetBlobPath(file.BlobId), temporary, key, cancellationToken);
                        string actualHash;
                        await using (var verifyStream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream, cancellationToken)).ToLowerInvariant();
                        }
                        if (!string.Equals(actualHash, file.ContentHash, StringComparison.Ordinal)) throw new InvalidDataException("恢复文件哈希校验失败。");
                        if (File.Exists(destination)) File.Delete(destination);
                        File.Move(temporary, destination);
                        File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
                        restored++;
                        restoredBytes += file.Length;
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    issues.Add(new OperationIssue(file.RelativePath, SafeMessage(ex), true));
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        var outcome = restored == 0 && issues.Count > 0
            ? OperationOutcome.Failed
            : issues.Count == 0 ? OperationOutcome.Success : OperationOutcome.PartialSuccess;
        return new RestoreResult(outcome, restored, restoredBytes, issues);
    }

    private SnapshotIndex LoadIndex(byte[] key) => File.Exists(indexPath)
        ? LoadEncrypted<SnapshotIndex>(indexPath, key, "snapshot-index")
        : new SnapshotIndex();

    private byte[] LoadMasterKey()
    {
        if (!File.Exists(metadataPath)) throw new DirectoryNotFoundException("备份仓库尚未初始化。");
        var metadata = new JsonStore().LoadOrCreate<RepositoryMetadata>(metadataPath, () => throw new InvalidDataException("备份仓库元数据缺失。"));
        var key = DpapiProtector.Unprotect(Convert.FromBase64String(metadata.ProtectedMasterKey));
        if (key.Length != 32) throw new CryptographicException("备份仓库主密钥长度无效。");
        return key;
    }

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(root);
        try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("另一个 Codex Helper 任务正在写入这个备份仓库。", ex); }
    }

    private string GetBlobPath(string blobId)
    {
        ValidateIdentifier(blobId);
        return Path.Combine(blobsDirectory, blobId[..2], blobId + ".chblob");
    }

    private static IEnumerable<FileCandidate> EnumerateSources(IReadOnlyList<BackupSource> sources, List<OperationIssue> issues, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source.Path);
            if (File.Exists(full))
            {
                yield return new FileCandidate(source, full, Path.GetFileName(full));
                continue;
            }
            if (!Directory.Exists(full))
            {
                issues.Add(new OperationIssue(source.DisplayName, "数据源不存在。", true));
                continue;
            }

            var excluded = new HashSet<string>(source.AdditionalExcludedDirectoryNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (source.UseDevelopmentExcludes) excluded.UnionWith(DevelopmentExcludedDirectories);
            if (string.Equals(source.Id, "skills", StringComparison.OrdinalIgnoreCase)) excluded.Add(".system");
            var stack = new Stack<string>();
            stack.Push(full);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = stack.Pop();
                string[] directories;
                string[] files;
                try
                {
                    directories = Directory.GetDirectories(current);
                    files = Directory.GetFiles(current);
                }
                catch (Exception ex)
                {
                    issues.Add(new OperationIssue(current, SafeMessage(ex), true));
                    continue;
                }
                foreach (var directory in directories)
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || excluded.Contains(info.Name)) continue;
                    stack.Push(directory);
                }
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (IsExcludedFileName(info.Name, source.AdditionalExcludedFileNames)) continue;
                    yield return new FileCandidate(source, file, Path.GetRelativePath(full, file));
                }
            }
        }
    }

    private static async Task<byte[]> ComputeStableHashAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = new FileInfo(path);
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            var after = new FileInfo(path);
            if (after.Length == length && after.LastWriteTimeUtc == modified) return hash;
        }
        throw new IOException("文件在读取期间持续变化，已跳过以避免不一致备份。");
    }

    private static void SaveEncrypted<T>(string path, T value, byte[] key, string associatedData)
    {
        var plain = JsonStore.Serialize(value);
        try { AtomicFile.WriteAllBytes(path, CryptoEnvelope.Encrypt(plain, key, Encoding.UTF8.GetBytes(associatedData))); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static T LoadEncrypted<T>(string path, byte[] key, string associatedData)
    {
        var envelope = File.ReadAllBytes(path);
        var plain = CryptoEnvelope.Decrypt(envelope, key, Encoding.UTF8.GetBytes(associatedData));
        try { return JsonStore.Deserialize<T>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>
    /// 从恢复根到目标父目录逐级检查中间目录是否为符号链接/重解析点（junction 等），
    /// 防止恢复写入逃逸到 DSH Home 之外；恢复根自身不受限制（可能位于 OneDrive 等重解析点下）。
    /// </summary>
    private static string? FindReparsePointDirectory(string destinationRoot, string destination)
    {
        var relative = Path.GetRelativePath(destinationRoot, Path.GetDirectoryName(destination) ?? destination);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".") return null;
        var current = destinationRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current)) continue;
            var info = new DirectoryInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return current;
        }
        return null;
    }

    /// <summary>
    /// 文件级排除匹配：以 '~' 开头按前缀匹配（Office/编辑器临时文件），以 '.' 开头按后缀匹配
    /// （扩展名，如 .tmp），其余按文件名精确匹配；大小写不敏感。
    /// </summary>
    private static bool IsExcludedFileName(string name, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return false;
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            if (pattern.StartsWith('~'))
            {
                if (name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (pattern.Length > 1 && pattern[0] == '.')
            {
                if (name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string SafeMessage(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有读取或写入权限。",
        PathTooLongException => "路径超过系统允许长度。",
        IOException => ex.Message,
        _ => ex.Message
    };

    private static void ValidateIdentifier(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("备份标识无效。");
    }

    private sealed record FileCandidate(BackupSource Source, string FullPath, string RelativePath);

    private sealed class RepositoryMetadata
    {
        public int SchemaVersion { get; set; } = 1;
        public string RepositoryId { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public string ProtectedMasterKey { get; set; } = string.Empty;
    }
}
