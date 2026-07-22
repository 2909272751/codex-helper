using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;
using CodexHelper.Core.Security;

namespace CodexHelper.Core.Services;

public sealed class BundleService
{
    private readonly AppPaths paths;

    public BundleService(AppPaths paths)
    {
        this.paths = paths;
        paths.EnsureCreated();
    }

    public async Task<BundleManifest> ExportAsync(
        BundleExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var virtualFiles = request.VirtualFiles ?? Array.Empty<BundleVirtualFile>();
        if (request.Items.Count == 0 && virtualFiles.Count == 0) throw new InvalidOperationException("没有选择要导出的内容。");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 10)
            throw new ArgumentException("迁移口令至少需要 10 个字符。", nameof(request));
        var destination = Path.GetFullPath(request.DestinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".chbundle", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("批量迁移包必须使用 .chbundle 扩展名。");

        var work = CreateWorkDirectory("export");
        var zipPath = Path.Combine(work, "payload.zip");
        var encryptedTemporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var manifest = new BundleManifest
        {
            BundleId = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            DeviceName = Environment.MachineName,
            CodexHelperVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"
        };

        try
        {
            var candidates = EnumerateItems(request.Items, manifest.Issues, cancellationToken).ToList();
            var allIds = request.Items.Select(item => item.Id).Concat(virtualFiles.Select(item => item.ItemId)).ToList();
            if (allIds.Distinct(StringComparer.Ordinal).Count() != allIds.Count) throw new InvalidOperationException("批量导出项目标识重复。");
            await using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                long processedBytes = 0;
                for (var index = 0; index < candidates.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = candidates[index];
                    progress?.Report(new OperationProgress("打包", candidate.FullPath, index, candidates.Count, processedBytes, $"正在导出 {candidate.Item.DisplayName}"));
                    try
                    {
                        var info = new FileInfo(candidate.FullPath);
                        var hash = await HashFileAsync(candidate.FullPath, cancellationToken);
                        var entryName = $"payload/{candidate.Item.Id}/{NormalizeEntryPath(candidate.RelativePath)}";
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        entry.LastWriteTime = ClampZipTime(info.LastWriteTimeUtc);
                        await using var source = new FileStream(candidate.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var target = entry.Open();
                        await source.CopyToAsync(target, cancellationToken);
                        manifest.Files.Add(new BundleFileEntry(candidate.Item.Id, candidate.RelativePath, Convert.ToHexString(hash).ToLowerInvariant(), info.Length, info.LastWriteTimeUtc));
                        processedBytes += info.Length;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        manifest.Issues.Add(new OperationIssue(candidate.FullPath, ex.Message, true));
                    }
                }

                foreach (var virtualFile in virtualFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateItemId(virtualFile.ItemId);
                    var relative = NormalizeEntryPath(virtualFile.RelativePath);
                    var entry = archive.CreateEntry($"payload/{virtualFile.ItemId}/{relative}", CompressionLevel.NoCompression);
                    entry.LastWriteTime = ClampZipTime(virtualFile.LastWriteTimeUtc);
                    await using var target = entry.Open();
                    await target.WriteAsync(virtualFile.Content, cancellationToken);
                    var hash = SHA256.HashData(virtualFile.Content);
                    manifest.Files.Add(new BundleFileEntry(virtualFile.ItemId, virtualFile.RelativePath, Convert.ToHexString(hash).ToLowerInvariant(), virtualFile.Content.LongLength, virtualFile.LastWriteTimeUtc));
                }

                foreach (var item in request.Items)
                {
                    var files = manifest.Files.Where(file => string.Equals(file.ItemId, item.Id, StringComparison.Ordinal)).ToList();
                    manifest.Items.Add(new BundleManifestItem(item.Id, item.Category, item.DisplayName, Path.GetFullPath(item.Path), files.Count, files.Sum(file => file.Length)));
                }
                foreach (var group in virtualFiles.GroupBy(item => new { item.ItemId, item.Category, item.DisplayName }))
                {
                    var files = manifest.Files.Where(file => file.ItemId == group.Key.ItemId).ToList();
                    manifest.Items.Add(new BundleManifestItem(group.Key.ItemId, group.Key.Category, group.Key.DisplayName, "受保护的内存数据", files.Count, files.Sum(file => file.Length)));
                }

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                var manifestBytes = JsonStore.Serialize(manifest);
                await manifestStream.WriteAsync(manifestBytes, cancellationToken);
                CryptographicOperations.ZeroMemory(manifestBytes);
            }

            progress?.Report(new OperationProgress("加密", destination, candidates.Count, candidates.Count, manifest.Files.Sum(file => file.Length), "正在使用 Argon2id 和 AES-256-GCM 加密迁移包"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await ChunkedEncryptedFile.EncryptPortableAsync(zipPath, encryptedTemporary, request.Password, cancellationToken);
            File.Move(encryptedTemporary, destination, overwrite: true);
            progress?.Report(new OperationProgress("完成", destination, candidates.Count, candidates.Count, manifest.Files.Sum(file => file.Length), "迁移包已重新打开并通过加密结构校验。"));
            return manifest;
        }
        finally
        {
            foreach (var virtualFile in virtualFiles) CryptographicOperations.ZeroMemory(virtualFile.Content);
            if (File.Exists(encryptedTemporary)) File.Delete(encryptedTemporary);
            DeleteWorkDirectory(work);
        }
    }

    public async Task<IReadOnlyList<BundleVirtualContent>> ReadVirtualFilesAsync(
        string bundlePath,
        string password,
        string category,
        CancellationToken cancellationToken = default)
    {
        var work = CreateWorkDirectory("virtual-read");
        var zipPath = Path.Combine(work, "payload.zip");
        try
        {
            await ChunkedEncryptedFile.DecryptPortableAsync(bundlePath, zipPath, password, cancellationToken);
            var manifest = ReadAndValidateManifest(zipPath);
            var items = manifest.Items.Where(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)).ToDictionary(item => item.Id, StringComparer.Ordinal);
            var files = manifest.Files.Where(file => items.ContainsKey(file.ItemId)).ToDictionary(file => FileKey(file.ItemId, file.RelativePath), StringComparer.Ordinal);
            var result = new List<BundleVirtualContent>();
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name) && entry.FullName.StartsWith("payload/", StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectSymlink(entry);
                var parts = entry.FullName.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !items.TryGetValue(parts[1], out var item)) continue;
                var relative = parts[2].Replace('/', Path.DirectorySeparatorChar);
                if (!files.TryGetValue(FileKey(parts[1], relative), out var metadata)) throw new InvalidDataException("迁移包虚拟文件未登记。");
                await using var stream = entry.Open();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);
                var content = memory.ToArray();
                var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (content.LongLength != metadata.Length || hash != metadata.ContentHash)
                {
                    CryptographicOperations.ZeroMemory(content);
                    throw new InvalidDataException("迁移包虚拟文件哈希不匹配。");
                }
                result.Add(new BundleVirtualContent(item, metadata, content));
            }
            if (result.Count != files.Count)
            {
                foreach (var content in result) CryptographicOperations.ZeroMemory(content.Content);
                throw new InvalidDataException("迁移包虚拟文件不完整。");
            }
            return result;
        }
        finally { DeleteWorkDirectory(work); }
    }

    public async Task<BundlePreview> PreviewAsync(string bundlePath, string password, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(bundlePath);
        var work = CreateWorkDirectory("preview");
        var zipPath = Path.Combine(work, "payload.zip");
        try
        {
            await ChunkedEncryptedFile.DecryptPortableAsync(source, zipPath, password, cancellationToken);
            var manifest = ReadAndValidateManifest(zipPath);
            return new BundlePreview(manifest, new FileInfo(source).Length, true);
        }
        finally { DeleteWorkDirectory(work); }
    }

    public async Task<BundleImportResult> ImportAsync(
        BundleImportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var work = CreateWorkDirectory("import");
        var zipPath = Path.Combine(work, "payload.zip");
        var stageRoot = Path.Combine(work, "stage");
        Directory.CreateDirectory(stageRoot);
        var issues = new List<OperationIssue>();
        var commitRecords = new List<CommitRecord>();

        try
        {
            await ChunkedEncryptedFile.DecryptPortableAsync(request.BundlePath, zipPath, request.Password, cancellationToken);
            var manifest = ReadAndValidateManifest(zipPath);
            var selectedIds = request.SelectedItemIds is { Count: > 0 }
                ? request.SelectedItemIds.ToHashSet(StringComparer.Ordinal)
                : manifest.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var expected = manifest.Files
                .Where(file => selectedIds.Contains(file.ItemId))
                .ToDictionary(file => FileKey(file.ItemId, file.RelativePath), StringComparer.Ordinal);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var payloadEntries = archive.Entries.Where(entry => entry.FullName.StartsWith("payload/", StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.Name)).ToList();
                var staged = 0;
                foreach (var entry in payloadEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectSymlink(entry);
                    var parts = entry.FullName.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3 || !selectedIds.Contains(parts[1])) continue;
                    var relative = parts[2].Replace('/', Path.DirectorySeparatorChar);
                    var key = FileKey(parts[1], relative);
                    if (!expected.TryGetValue(key, out var metadata)) throw new InvalidDataException($"迁移包包含未登记文件：{entry.FullName}");
                    var stagedPath = PathSafety.CombineWithin(stageRoot, Path.Combine(parts[1], relative));
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                    progress?.Report(new OperationProgress("验证", entry.FullName, staged, expected.Count, 0, "正在解密并校验批量导入内容"));
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await input.CopyToAsync(output, cancellationToken);
                    }
                    var info = new FileInfo(stagedPath);
                    if (info.Length != metadata.Length) throw new InvalidDataException($"迁移文件长度不匹配：{entry.FullName}");
                    var hash = Convert.ToHexString(await HashFileAsync(stagedPath, cancellationToken)).ToLowerInvariant();
                    if (!string.Equals(hash, metadata.ContentHash, StringComparison.Ordinal)) throw new InvalidDataException($"迁移文件哈希不匹配：{entry.FullName}");
                    staged++;
                }
                if (staged != expected.Count) throw new InvalidDataException($"迁移包文件不完整：预期 {expected.Count}，实际 {staged}。");
            }

            var selectedFiles = manifest.Files.Where(file => selectedIds.Contains(file.ItemId)).ToList();
            long importedBytes = 0;
            var importedFiles = 0;
            try
            {
                for (var index = 0; index < selectedFiles.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = selectedFiles[index];
                    var stagedPath = PathSafety.CombineWithin(stageRoot, Path.Combine(file.ItemId, file.RelativePath));
                    var destination = PathSafety.CombineWithin(destinationRoot, Path.Combine(file.ItemId, file.RelativePath));
                    progress?.Report(new OperationProgress("导入", file.RelativePath, index, selectedFiles.Count, importedBytes, "正在提交批量导入事务"));
                    if (File.Exists(destination) && !request.OverwriteExisting)
                    {
                        issues.Add(new OperationIssue(destination, "目标已存在，已安全跳过。", true));
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var adjacentTemporary = destination + "." + Guid.NewGuid().ToString("N") + ".import.tmp";
                    var adjacentBackup = destination + "." + Guid.NewGuid().ToString("N") + ".rollback.bak";
                    File.Copy(stagedPath, adjacentTemporary, overwrite: false);
                    if (File.Exists(destination))
                    {
                        File.Replace(adjacentTemporary, destination, adjacentBackup, ignoreMetadataErrors: true);
                        commitRecords.Add(new CommitRecord(destination, adjacentBackup, true));
                    }
                    else
                    {
                        File.Move(adjacentTemporary, destination);
                        commitRecords.Add(new CommitRecord(destination, null, false));
                    }
                    File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
                    importedFiles++;
                    importedBytes += file.Length;
                }
            }
            catch
            {
                Rollback(commitRecords);
                throw;
            }

            foreach (var record in commitRecords)
            {
                if (record.BackupPath is not null && File.Exists(record.BackupPath)) File.Delete(record.BackupPath);
            }
            var outcome = importedFiles == 0 && issues.Count > 0
                ? OperationOutcome.Failed
                : issues.Count == 0 ? OperationOutcome.Success : OperationOutcome.PartialSuccess;
            return new BundleImportResult(outcome, importedFiles, importedBytes, issues);
        }
        finally { DeleteWorkDirectory(work); }
    }

    private static BundleManifest ReadAndValidateManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("迁移包缺少 manifest.json。");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var manifest = JsonStore.Deserialize<BundleManifest>(memory.ToArray());
        if (manifest.SchemaVersion != 1) throw new NotSupportedException($"不支持的迁移清单版本：{manifest.SchemaVersion}");
        if (manifest.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Items.Count)
            throw new InvalidDataException("迁移清单存在重复项目标识。");
        foreach (var item in manifest.Items) ValidateItemId(item.Id);
        return manifest;
    }

    private static IEnumerable<ExportCandidate> EnumerateItems(IReadOnlyList<BundleExportItem> items, List<OperationIssue> issues, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ValidateItemId(item.Id);
            if (!ids.Add(item.Id)) throw new InvalidOperationException($"导出项目标识重复：{item.Id}");
            var full = Path.GetFullPath(item.Path);
            if (File.Exists(full))
            {
                yield return new ExportCandidate(item, full, Path.GetFileName(full));
                continue;
            }
            if (!Directory.Exists(full))
            {
                issues.Add(new OperationIssue(item.DisplayName, "导出源不存在。", true));
                continue;
            }

            var stack = new Stack<string>();
            stack.Push(full);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = stack.Pop();
                string[] directories;
                string[] files;
                try { directories = Directory.GetDirectories(current); files = Directory.GetFiles(current); }
                catch (Exception ex) { issues.Add(new OperationIssue(current, ex.Message, true)); continue; }
                foreach (var directory in directories)
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (string.Equals(item.Category, "skills", StringComparison.OrdinalIgnoreCase) && string.Equals(info.Name, ".system", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(directory);
                }
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    yield return new ExportCandidate(item, file, Path.GetRelativePath(full, file));
                }
            }
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private string CreateWorkDirectory(string purpose)
    {
        var directory = Path.Combine(paths.TempDirectory, purpose + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void DeleteWorkDirectory(string directory)
    {
        if (!PathSafety.IsWithin(directory, paths.TempDirectory)) throw new InvalidOperationException("拒绝清理临时目录范围外路径。");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static void Rollback(IEnumerable<CommitRecord> records)
    {
        foreach (var record in records.Reverse())
        {
            try
            {
                if (record.Existed)
                {
                    if (File.Exists(record.DestinationPath)) File.Delete(record.DestinationPath);
                    if (record.BackupPath is not null && File.Exists(record.BackupPath)) File.Move(record.BackupPath, record.DestinationPath);
                }
                else if (File.Exists(record.DestinationPath)) File.Delete(record.DestinationPath);
            }
            catch { }
        }
    }

    private static void RejectSymlink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000) throw new InvalidDataException($"迁移包不允许符号链接：{entry.FullName}");
    }

    private static DateTimeOffset ClampZipTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        if (utc.Year < 1980) utc = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (utc.Year > 2107) utc = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private static string NormalizeEntryPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"迁移相对路径无效：{relativePath}");
        return normalized;
    }

    private static string FileKey(string itemId, string relativePath) => itemId + "/" + NormalizeEntryPath(relativePath);

    private static void ValidateItemId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException($"迁移项目标识无效：{id}");
    }

    private sealed record ExportCandidate(BundleExportItem Item, string FullPath, string RelativePath);
    private sealed record CommitRecord(string DestinationPath, string? BackupPath, bool Existed);
}
