using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// DSH 便携迁移 ZIP：导出 Skills、Agent 预设、用户插件与配置声明的版本化迁移包，
/// 导入前先解压到 Helper 临时 staging 校验（ZIP slip、重解析点、大小/文件数上限、manifest、
/// 哈希、声明），再受控复制并支持失败回滚。默认不覆盖已有文件。
/// 绝对排除：.credentials.yaml、任意 credential/token/session/cookie、sessions/、attachments/、
/// storages/、缓存、日志、临时文件、重解析点与插件依赖树。不得仅靠文件名，对声明标记的
/// secret 文件名与已知 DSH 凭据文件做硬排除。
/// </summary>
public sealed class DshTransferService
{
    /// <summary>迁移包格式版本（固定）。</summary>
    public const string ManifestVersion = "1.0";

    private const long MaxManifestBytes = 16L * 1024 * 1024;
    private const int MaxFiles = 100_000;
    private const long MaxSingleFileBytes = 512L * 1024 * 1024;
    private const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;

    private readonly AppPaths paths;

    public DshTransferService(AppPaths paths)
    {
        this.paths = paths;
        paths.EnsureCreated();
    }

    /// <summary>生成版本化迁移包文件名：codex-helper-dsh-transfer-v{版本}-{yyyyMMdd-HHmmss}.zip。</summary>
    public static string BuildFileName(string helperVersion)
    {
        var safeVersion = string.IsNullOrWhiteSpace(helperVersion) ? "0.0.0" : helperVersion.Trim().TrimStart('v');
        return $"codex-helper-dsh-transfer-v{safeVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
    }

    // ---- 导出 ----

    public async Task<DshTransferManifest> ExportAsync(
        DshTransferExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Components.Count == 0) throw new InvalidOperationException("没有可导出的 DSH 组件。");
        var destination = Path.GetFullPath(request.DestinationZipPath);
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DSH 迁移包必须使用 .zip 扩展名。");

        var work = CreateWorkDirectory("dsh-export");
        var zipPath = Path.Combine(work, "transfer.zip");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var manifest = new DshTransferManifest
        {
            TransferId = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            DeviceName = Environment.MachineName,
            CodexHelperVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            ManifestVersion = ManifestVersion
        };

        try
        {
            long totalBytes = 0;
            var fileCount = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var component in request.Components)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(component.RootPath)) continue;
                    var componentId = SafeComponentId(component);
                    var componentFiles = EnumerateMigratableFiles(component.RootPath, manifest.Issues, cancellationToken).ToList();
                    progress?.Report(new OperationProgress("导出", component.Name, 0, request.Components.Count, totalBytes, $"正在导出 {component.Name}"));
                    foreach (var (fullPath, relativePath, length) in componentFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (totalBytes > MaxTotalBytes - length || length > MaxSingleFileBytes)
                            throw new InvalidOperationException("DSH 迁移包超过大小安全上限。");
                        totalBytes += length;
                        fileCount++;
                        if (fileCount > MaxFiles) throw new InvalidOperationException("DSH 迁移包文件数量超过安全上限。");

                        var info = new FileInfo(fullPath);
                        var hash = Convert.ToHexString(await HashFileAsync(fullPath, cancellationToken)).ToLowerInvariant();
                        var entry = archive.CreateEntry($"payload/{componentId}/{NormalizeEntryPath(relativePath)}", CompressionLevel.Optimal);
                        entry.LastWriteTime = ClampZipTime(info.LastWriteTimeUtc);
                        await using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var target = entry.Open();
                        await source.CopyToAsync(target, cancellationToken);
                        manifest.Files.Add(new DshTransferFile(componentId, relativePath, hash, length, info.LastWriteTimeUtc));
                    }

                    // 配置声明副本（内嵌/侧车声明随包迁移，供另一台电脑预览配置需求）。
                    string? declarationRelative = null;
                    if (component.Declaration is not null)
                    {
                        declarationRelative = $"components/{componentId}.json";
                        var declarationEntry = archive.CreateEntry(declarationRelative, CompressionLevel.Optimal);
                        await using var declarationStream = declarationEntry.Open();
                        var bytes = JsonStore.Serialize(component.Declaration);
                        await declarationStream.WriteAsync(bytes, cancellationToken);
                    }
                    manifest.Components.Add(new DshTransferComponent(
                        componentId, component.Kind.ToString().ToLowerInvariant(), component.Name,
                        component.Version, NormalizeEntryPath(component.TargetRelativeRoot), declarationRelative));
                }
                if (manifest.Components.Count == 0)
                    throw new InvalidOperationException("没有可导出的 DSH 组件（所有组件根目录都不存在或为空）。");

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    var manifestBytes = JsonStore.Serialize(manifest);
                    await manifestStream.WriteAsync(manifestBytes, cancellationToken);
                }
            }

            File.Move(zipPath, temporary);
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(new OperationProgress("完成", destination, request.Components.Count, request.Components.Count, totalBytes, "DSH 迁移包已生成。"));
            return manifest;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            DeleteWorkDirectory(work);
        }
    }

    // ---- 预览 ----

    public async Task<DshTransferPreview> PreviewAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(bundlePath);
        var work = CreateWorkDirectory("dsh-preview");
        var zipPath = Path.Combine(work, "transfer.zip");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            var manifest = ReadAndValidateManifest(zipPath);
            return new DshTransferPreview(manifest, new FileInfo(source).Length, true);
        }
        finally { DeleteWorkDirectory(work); }
    }

    // ---- 导入 ----

    public async Task<DshTransferImportResult> ImportAsync(
        DshTransferImportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destinationHome = Path.GetFullPath(request.DestinationHome);
        Directory.CreateDirectory(destinationHome);
        var work = CreateWorkDirectory("dsh-import");
        var zipPath = Path.Combine(work, "transfer.zip");
        var stageRoot = Path.Combine(work, "stage");
        Directory.CreateDirectory(stageRoot);
        var issues = new List<OperationIssue>();
        var commitRecords = new List<CommitRecord>();

        try
        {
            using (var input = new FileStream(Path.GetFullPath(request.BundlePath), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            var manifest = ReadAndValidateManifest(zipPath);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var expected = manifest.Files.ToDictionary(file => file.ComponentId + "/" + NormalizeEntryPath(file.RelativePath), StringComparer.Ordinal);
                var stagedKeys = new HashSet<string>(StringComparer.Ordinal);
                var payloadEntries = archive.Entries
                    .Where(entry => entry.FullName.StartsWith("payload/", StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.Name))
                    .ToList();
                var staged = 0;
                foreach (var entry in payloadEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectSymlink(entry);
                    var parts = entry.FullName.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3) throw new InvalidDataException($"迁移包包含非法条目：{entry.FullName}");
                    var key = parts[1] + "/" + NormalizeEntryPath(parts[2]);
                    if (!expected.TryGetValue(key, out var metadata)) throw new InvalidDataException($"迁移包包含未登记文件：{entry.FullName}");
                    if (!stagedKeys.Add(key)) throw new InvalidDataException($"迁移包包含重复文件：{entry.FullName}");
                    if (entry.Length != metadata.Length) throw new InvalidDataException($"迁移文件声明长度不匹配：{entry.FullName}");
                    var stagedPath = PathSafety.CombineWithin(stageRoot, Path.Combine(parts[1], parts[2].Replace('/', Path.DirectorySeparatorChar)));
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                    progress?.Report(new OperationProgress("验证", entry.FullName, staged, expected.Count, 0, "正在校验迁移包内容"));
                    await using (var inputEntry = entry.Open())
                    await using (var outputFile = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await CopyBoundedAsync(inputEntry, outputFile, metadata.Length, cancellationToken);
                    }
                    var hash = Convert.ToHexString(await HashFileAsync(stagedPath, cancellationToken)).ToLowerInvariant();
                    if (!string.Equals(hash, metadata.ContentHash, StringComparison.Ordinal))
                        throw new InvalidDataException($"迁移文件哈希不匹配：{entry.FullName}");
                    staged++;
                }
                if (staged != expected.Count)
                    throw new InvalidDataException($"迁移包文件不完整：预期 {expected.Count}，实际 {staged}。");
            }

            var imported = 0;
            var skipped = 0;
            try
            {
                for (var index = 0; index < manifest.Files.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = manifest.Files[index];
                    var component = manifest.Components.FirstOrDefault(candidate => candidate.Id == file.ComponentId)
                        ?? throw new InvalidDataException($"迁移清单文件引用了不存在的组件：{file.ComponentId}");
                    var relative = NormalizeEntryPath(Path.Combine(component.TargetRelativeRoot, file.RelativePath));
                    var stagedPath = PathSafety.CombineWithin(stageRoot, Path.Combine(file.ComponentId, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    var destination = PathSafety.CombineWithin(destinationHome, relative.Replace('/', Path.DirectorySeparatorChar));
                    progress?.Report(new OperationProgress("导入", file.RelativePath, index, manifest.Files.Count, 0, $"正在导入 {component.DisplayName}"));
                    if (request.OnlyNewFiles && File.Exists(destination))
                    {
                        skipped++;
                        issues.Add(new OperationIssue(destination, "目标已存在，已保留本机文件。", true));
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
                    imported++;
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

            var pendingSetup = manifest.Components.Count(component =>
                component.Kind is "plugin" or "skill" or "preset" && FindDeclarationState(component) != "none");
            var outcome = imported == 0 && skipped > 0
                ? OperationOutcome.Success
                : issues.Any(issue => !issue.Message.Contains("已保留本机文件", StringComparison.Ordinal)) && imported > 0
                    ? OperationOutcome.PartialSuccess
                    : OperationOutcome.Success;
            return new DshTransferImportResult(outcome, imported, skipped, pendingSetup, 0, issues);
        }
        finally { DeleteWorkDirectory(work); }
    }

    private static string FindDeclarationState(DshTransferComponent component)
    {
        // 迁移包内组件的配置状态由声明副本决定；此处只用于导入完成统计。
        return component.DeclarationRelativePath is null ? "none" : "unknown";
    }

    // ---- 校验 ----

    private static DshTransferManifest ReadAndValidateManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntries = archive.Entries.Where(entry => string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal)).ToList();
        if (manifestEntries.Count != 1) throw new InvalidDataException("迁移包必须且只能包含一个 manifest.json。");
        var entry = manifestEntries[0];
        RejectSymlink(entry);
        if (entry.Length <= 0 || entry.Length > MaxManifestBytes) throw new InvalidDataException("迁移清单大小无效。");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var manifest = JsonStore.Deserialize<DshTransferManifest>(memory.ToArray());
        if (manifest.SchemaVersion != 1) throw new NotSupportedException($"不支持的迁移清单版本：{manifest.SchemaVersion}");
        if (!string.Equals(manifest.ManifestVersion, ManifestVersion, StringComparison.Ordinal))
            throw new NotSupportedException($"不支持的迁移包格式版本：{manifest.ManifestVersion}");
        if (manifest.Components.Count == 0) throw new InvalidDataException("迁移包不包含任何组件。");
        if (manifest.Files.Count > MaxFiles) throw new InvalidDataException("迁移清单文件数量超过安全上限。");

        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in manifest.Components)
        {
            if (!componentIds.Add(component.Id)) throw new InvalidDataException("迁移清单存在重复组件标识。");
            ValidateComponentId(component.Id);
            if (!string.Equals(component.Kind, "plugin", StringComparison.Ordinal)
                && !string.Equals(component.Kind, "skill", StringComparison.Ordinal)
                && !string.Equals(component.Kind, "preset", StringComparison.Ordinal)
                && !string.Equals(component.Kind, "bridge", StringComparison.Ordinal))
                throw new InvalidDataException($"迁移清单包含非法组件类型：{component.Kind}");
            NormalizeEntryPath(component.TargetRelativeRoot);
            if (component.DeclarationRelativePath is not null)
            {
                NormalizeEntryPath(component.DeclarationRelativePath);
                if (!component.DeclarationRelativePath.StartsWith("components/", StringComparison.Ordinal))
                    throw new InvalidDataException("迁移清单声明副本路径无效。");
            }
        }
        var fileKeys = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var file in manifest.Files)
        {
            if (!componentIds.Contains(file.ComponentId)) throw new InvalidDataException("迁移清单文件引用了不存在的组件。");
            var key = file.ComponentId + "/" + NormalizeEntryPath(file.RelativePath);
            if (!fileKeys.Add(key)) throw new InvalidDataException("迁移清单存在重复文件路径。");
            if (file.Length < 0 || file.Length > MaxSingleFileBytes || totalBytes > MaxTotalBytes - file.Length)
                throw new InvalidDataException("迁移清单文件大小超过安全上限。");
            totalBytes += file.Length;
            if (file.ContentHash.Length != 64 || file.ContentHash.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("迁移清单包含无效的 SHA-256 哈希。");
        }
        return manifest;
    }

    // ---- 文件收集与排除 ----

    /// <summary>
    /// 枚举允许迁移的文件：排除重解析点、插件依赖树（node_modules）、缓存、日志、临时文件、
    /// 会话/附件/storages，以及对声明标记的 secret 文件名与已知 DSH 凭据文件的硬排除。
    /// </summary>
    private static IEnumerable<(string FullPath, string RelativePath, long Length)> EnumerateMigratableFiles(
        string root, List<OperationIssue> issues, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(root);
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
                if (IsExcludedDirectoryName(info.Name)) continue;
                stack.Push(directory);
            }
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (IsExcludedFileName(file)) continue;
                yield return (file, Path.GetRelativePath(root, file), info.Length);
            }
        }
    }

    private static bool IsExcludedDirectoryName(string name)
    {
        if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "sessions", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "attachments", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "storages", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) return true;
        if (name is ".cache" or "__pycache__" or ".npm" or "logs" or "tmp" or "temp" or ".vscode") return true;
        return false;
    }

    private static bool IsExcludedFileName(string path)
    {
        var name = Path.GetFileName(path);
        if (DshComponentScanner.IsCredentialFileName(name)) return true;
        if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return true;
        if (name is ".DS_Store" or "Thumbs.db") return true;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
        if (name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        try
        {
            long copied = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (copied > expectedLength - read) throw new InvalidDataException("迁移文件解压后超过清单声明大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
            }
            if (copied != expectedLength) throw new InvalidDataException("迁移文件解压后小于清单声明大小。");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static string SafeComponentId(DshComponentInfo component)
    {
        var name = component.Name.Replace(' ', '-');
        var id = new string(name.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
        if (string.IsNullOrWhiteSpace(id)) id = "component";
        return id.Length > 64 ? id[..64] : id;
    }

    private static void ValidateComponentId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException($"迁移组件标识无效：{id}");
    }

    private static string NormalizeEntryPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/')
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"迁移相对路径无效：{relativePath}");
        return normalized;
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

    private sealed record CommitRecord(string DestinationPath, string? BackupPath, bool Existed);
}
