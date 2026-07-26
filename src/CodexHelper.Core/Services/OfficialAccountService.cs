using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// Safe account-switching behavior migrated from the MIT-licensed
/// codex-account-switcher project maintained by 2909272751.
/// </summary>
public sealed class OfficialAccountService
{
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("CodexAccountSwitcher-v1");
    private readonly string codexRoot;
    private readonly string authPath;
    private readonly string profileDirectory;
    private readonly string providerDirectory;
    private readonly string recoveryDirectory;
    private readonly string indexPath;
    private readonly JsonStore json = new();
    private readonly CodexProcessService processes;

    public OfficialAccountService(string rootPath, AppPaths appPaths, CodexProcessService processes)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("Codex 根目录不能为空。", nameof(rootPath));
        codexRoot = Path.GetFullPath(rootPath);
        authPath = Path.Combine(codexRoot, "auth.json");
        profileDirectory = Path.Combine(appPaths.VaultDirectory, "accounts");
        providerDirectory = Path.Combine(appPaths.VaultDirectory, "providers");
        recoveryDirectory = Path.Combine(appPaths.RecoveryDirectory, "accounts");
        indexPath = Path.Combine(appPaths.VaultDirectory, "connections.json");
        this.processes = processes;
        Directory.CreateDirectory(profileDirectory);
        Directory.CreateDirectory(recoveryDirectory);
    }

    public ConnectionIndex LoadIndex()
    {
        var index = json.LoadOrCreate(indexPath, () => new ConnectionIndex());
        var missing = index.Profiles.Where(ProfileFileIsMissing).ToList();
        if (missing.Count > 0)
        {
            foreach (var profile in missing) index.Profiles.Remove(profile);
            if (missing.Any(profile => string.Equals(profile.Id, index.ActiveProfileId, StringComparison.Ordinal))) index.ActiveProfileId = string.Empty;
            SaveIndex(index);
        }
        foreach (var profile in index.Profiles)
        {
            ValidateProfileId(profile.Id);
            profile.IsActive = string.Equals(profile.Id, index.ActiveProfileId, StringComparison.Ordinal);
        }
        return index;
    }

    private bool ProfileFileIsMissing(ConnectionProfile profile)
    {
        if (!Guid.TryParseExact(profile.Id, "N", out _)) return true;
        var path = profile.Kind == ConnectionKind.OfficialAccount
            ? ProfilePath(profile.Id)
            : Path.Combine(providerDirectory, profile.Id + ".dat");
        return !File.Exists(path);
    }

    public ConnectionProfile SaveCurrent(string label)
    {
        AssertSafeToWrite();
        if (string.IsNullOrWhiteSpace(label)) throw new InvalidOperationException("请填写账号名称。");
        var auth = ReadAndValidateAuth();
        try
        {
            var identity = ReadIdentity(auth);
            var identityHash = HashIdentity(identity);
            var index = LoadIndex();
            var normalizedLabel = label.Trim();
            var profile = index.Profiles.FirstOrDefault(item => item.Kind == ConnectionKind.OfficialAccount && string.Equals(item.Label, normalizedLabel, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                profile = new ConnectionProfile { Label = normalizedLabel, Kind = ConnectionKind.OfficialAccount };
                index.Profiles.Add(profile);
            }
            if (!string.IsNullOrWhiteSpace(profile.IdentityHash) && !string.IsNullOrWhiteSpace(identityHash) && !string.Equals(profile.IdentityHash, identityHash, StringComparison.Ordinal))
                throw new InvalidOperationException("该名称已经属于另一个 Codex 账号，请使用不同名称。");
            profile.IdentityHint = CreateIdentityHint(identity);
            profile.IdentityHash = identityHash;
            profile.UpdatedUtc = DateTime.UtcNow;
            SaveProtected(ProfilePath(profile.Id), auth);
            index.ActiveProfileId = profile.Id;
            SaveIndex(index);
            return profile;
        }
        finally { CryptographicOperations.ZeroMemory(auth); }
    }

    public void SwitchTo(string profileId)
    {
        AssertSafeToWrite();
        ValidateProfileId(profileId);
        var index = LoadIndex();
        var target = index.Profiles.FirstOrDefault(item => item.Kind == ConnectionKind.OfficialAccount && item.Id == profileId)
            ?? throw new InvalidOperationException("未找到目标官方账号档案。");
        var active = index.Profiles.FirstOrDefault(item => item.Id == index.ActiveProfileId);

        if (active is not null && File.Exists(authPath))
        {
            var current = ReadAndValidateAuth();
            try
            {
                var currentHash = HashIdentity(ReadIdentity(current));
                if (!string.IsNullOrWhiteSpace(active.IdentityHash) && !string.IsNullOrWhiteSpace(currentHash) && !string.Equals(active.IdentityHash, currentHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("当前 auth.json 与活动档案身份不一致，请先另存当前登录以避免覆盖其他账号。");
                SaveProtected(ProfilePath(active.Id), current);
                active.UpdatedUtc = DateTime.UtcNow;
            }
            finally { CryptographicOperations.ZeroMemory(current); }
        }

        BackupCurrentAuth();
        var selected = LoadProtected(ProfilePath(target.Id));
        byte[]? original = File.Exists(authPath) ? File.ReadAllBytes(authPath) : null;
        var previousActive = index.ActiveProfileId;
        try
        {
            ValidateAuth(selected);
            AtomicFile.WriteAllBytes(authPath, selected);
            index.ActiveProfileId = target.Id;
            target.UpdatedUtc = DateTime.UtcNow;
            SaveIndex(index);
            ArchiveModelCache();
        }
        catch
        {
            if (original is null)
            {
                if (File.Exists(authPath)) File.Delete(authPath);
            }
            else AtomicFile.WriteAllBytes(authPath, original);
            index.ActiveProfileId = previousActive;
            SaveIndex(index);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(selected);
            if (original is not null) CryptographicOperations.ZeroMemory(original);
        }
    }

    public void PrepareNewLogin()
    {
        AssertSafeToWrite();
        var index = LoadIndex();
        var active = index.Profiles.FirstOrDefault(item => item.Id == index.ActiveProfileId);
        if (File.Exists(authPath))
        {
            var current = ReadAndValidateAuth();
            try
            {
                if (active is not null)
                {
                    SaveProtected(ProfilePath(active.Id), current);
                    var identity = ReadIdentity(current);
                    active.IdentityHint = CreateIdentityHint(identity);
                    active.IdentityHash = HashIdentity(identity);
                    active.UpdatedUtc = DateTime.UtcNow;
                }
                SaveRecovery(current);
            }
            finally { CryptographicOperations.ZeroMemory(current); }
            File.Delete(authPath);
        }
        index.ActiveProfileId = string.Empty;
        SaveIndex(index);
        ArchiveModelCache();
    }

    public int ImportLegacyDirectory(string selectedPath)
    {
        var directory = ResolveLegacyDirectory(selectedPath);
        var legacyIndexPath = Path.Combine(directory, "index.json");
        if (!File.Exists(legacyIndexPath)) throw new FileNotFoundException("旧版目录缺少 index.json。", legacyIndexPath);
        var legacy = JsonSerializer.Deserialize<LegacyIndex>(File.ReadAllText(legacyIndexPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("旧版账号索引无效。");
        var current = LoadIndex();
        var imported = 0;
        foreach (var item in legacy.Profiles ?? new List<LegacyProfile>())
        {
            ValidateProfileId(item.Id);
            var source = Path.Combine(directory, "profiles", item.Id + ".dat");
            if (!File.Exists(source)) continue;
            var encrypted = File.ReadAllBytes(source);
            byte[] auth;
            try { auth = DpapiProtector.Unprotect(encrypted, LegacyEntropy); }
            catch (CryptographicException) { continue; }
            try
            {
                ValidateAuth(auth);
                var identity = ReadIdentity(auth);
                var hash = HashIdentity(identity);
                var existing = current.Profiles.FirstOrDefault(profile => profile.Kind == ConnectionKind.OfficialAccount && !string.IsNullOrWhiteSpace(hash) && profile.IdentityHash == hash);
                if (existing is null)
                {
                    existing = new ConnectionProfile
                    {
                        Label = UniqueLabel(current, string.IsNullOrWhiteSpace(item.Label) ? "导入账号" : item.Label),
                        Kind = ConnectionKind.OfficialAccount,
                        CreatedUtc = DateTime.UtcNow
                    };
                    current.Profiles.Add(existing);
                }
                existing.IdentityHint = CreateIdentityHint(identity);
                existing.IdentityHash = hash;
                existing.UpdatedUtc = DateTime.UtcNow;
                SaveProtected(ProfilePath(existing.Id), auth);
                imported++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(auth);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        SaveIndex(current);
        return imported;
    }

    public byte[] ExportDecryptedProfileForBundle(string profileId)
    {
        ValidateProfileId(profileId);
        var index = LoadIndex();
        if (!index.Profiles.Any(item => item.Kind == ConnectionKind.OfficialAccount && item.Id == profileId))
            throw new InvalidOperationException("未找到要导出的官方账号档案。");
        var auth = LoadProtected(ProfilePath(profileId));
        ValidateAuth(auth);
        return auth;
    }

    /// <summary>Persists a non-secret account health result produced by the direct usage check.</summary>
    public void UpdateVerification(string profileId, bool available, string statusMessage, OfficialAccountUsage? usage = null)
    {
        ValidateProfileId(profileId);
        var index = LoadIndex();
        var profile = index.Profiles.FirstOrDefault(item => item.Kind == ConnectionKind.OfficialAccount && item.Id == profileId)
            ?? throw new InvalidOperationException("未找到官方账号档案。");
        profile.LastVerifiedUtc = DateTime.UtcNow;
        profile.RequiresAttention = !available;
        profile.StatusMessage = statusMessage;
        profile.QuotaSummary = usage?.Summary ?? string.Empty;
        profile.QuotaCheckedUtc = usage is null ? null : DateTime.UtcNow;
        profile.UpdatedUtc = DateTime.UtcNow;
        SaveIndex(index);
    }

    public ConnectionProfile ImportDecryptedProfile(string label, ReadOnlySpan<byte> authBytes)
    {
        var auth = authBytes.ToArray();
        try
        {
            ValidateAuth(auth);
            var identity = ReadIdentity(auth);
            var hash = HashIdentity(identity);
            var index = LoadIndex();
            var existing = index.Profiles.FirstOrDefault(item => item.Kind == ConnectionKind.OfficialAccount && !string.IsNullOrEmpty(hash) && item.IdentityHash == hash);
            if (existing is null)
            {
                existing = new ConnectionProfile { Label = UniqueLabel(index, label), Kind = ConnectionKind.OfficialAccount };
                index.Profiles.Add(existing);
            }
            existing.IdentityHint = CreateIdentityHint(identity);
            existing.IdentityHash = hash;
            existing.UpdatedUtc = DateTime.UtcNow;
            SaveProtected(ProfilePath(existing.Id), auth);
            SaveIndex(index);
            return existing;
        }
        finally { CryptographicOperations.ZeroMemory(auth); }
    }

    /// <summary>Exports saved official-account profiles in a selected, documented account-file layout.</summary>
    public OfficialJsonExportResult ExportProfiles(string destinationDirectory, OfficialAccountExportFormat format)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("请选择导出目录。", nameof(destinationDirectory));
        var directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        var profiles = LoadIndex().Profiles.Where(item => item.Kind == ConnectionKind.OfficialAccount).OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var paths = new List<string>();
        if (format == OfficialAccountExportFormat.Sub2ApiJson)
        {
            var records = new List<JsonElement>();
            foreach (var profile in profiles)
            {
                var auth = ExportDecryptedProfileForBundle(profile.Id);
                try { records.Add(CreateSub2Record(profile.Label, auth)); }
                finally { CryptographicOperations.ZeroMemory(auth); }
            }
            var path = CreateUniqueJsonPath(directory, "Codex-Sub2API-accounts");
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            paths.Add(path);
            return new OfficialJsonExportResult(profiles.Count, paths, format);
        }
        foreach (var profile in profiles)
        {
            var auth = ExportDecryptedProfileForBundle(profile.Id);
            try
            {
                var path = CreateUniqueJsonPath(directory, profile.Label);
                var output = format == OfficialAccountExportFormat.CpaJson ? CreateCpaRecord(auth) : auth;
                try { AtomicFile.WriteAllBytes(path, output); }
                finally { if (!ReferenceEquals(output, auth)) CryptographicOperations.ZeroMemory(output); }
                paths.Add(path);
            }
            finally { CryptographicOperations.ZeroMemory(auth); }
        }
        return new OfficialJsonExportResult(paths.Count, paths, format);
    }

    /// <summary>Compatibility wrapper for existing callers that expect raw Codex auth.json files.</summary>
    public OfficialJsonExportResult ExportProfilesAsJson(string destinationDirectory) =>
        ExportProfiles(destinationDirectory, OfficialAccountExportFormat.OfficialCodexJson);

    /// <summary>Imports one or more standard Codex auth JSON files without activating any account.</summary>
    public OfficialJsonImportResult ImportProfilesFromJson(IEnumerable<string> paths)
    {
        var sources = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) throw new InvalidOperationException("请至少选择一个官方账号 JSON 文件。");
        var validated = new List<(string Path, byte[] Auth)>();
        try
        {
            foreach (var source in sources)
            {
                if (!string.Equals(Path.GetExtension(source), ".json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"不是 JSON 文件：{Path.GetFileName(source)}");
                if (!File.Exists(source)) throw new FileNotFoundException("找不到选择的 JSON 文件。", source);
                var bytes = File.ReadAllBytes(source);
                try
                {
                    var records = NormalizeImportedRecords(bytes);
                    foreach (var auth in records) validated.Add((source, auth));
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }

            var existingIds = LoadIndex().Profiles.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var newProfiles = 0;
            foreach (var (source, auth) in validated)
            {
                var profile = ImportDecryptedProfile(Path.GetFileNameWithoutExtension(source), auth);
                if (existingIds.Add(profile.Id)) newProfiles++;
            }
            return new OfficialJsonImportResult(validated.Count, newProfiles);
        }
        finally
        {
            foreach (var (_, auth) in validated) CryptographicOperations.ZeroMemory(auth);
        }
    }

    /// <summary>Safely removes an archived official account. Deleting the active profile also clears live auth.json.</summary>
    public void DeleteProfile(string profileId)
    {
        AssertSafeToWrite();
        ValidateProfileId(profileId);
        var index = LoadIndex();
        var profile = index.Profiles.FirstOrDefault(item => item.Kind == ConnectionKind.OfficialAccount && item.Id == profileId)
            ?? throw new InvalidOperationException("未找到要删除的官方账号档案。");
        var protectedPath = ProfilePath(profile.Id);
        if (File.Exists(protectedPath))
        {
            var archived = File.ReadAllBytes(protectedPath);
            try { AtomicFile.WriteAllBytes(Path.Combine(recoveryDirectory, "deleted-" + profile.Id + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".dat"), archived); }
            finally { CryptographicOperations.ZeroMemory(archived); }
        }
        if (string.Equals(index.ActiveProfileId, profileId, StringComparison.Ordinal))
        {
            BackupCurrentAuth();
            if (File.Exists(authPath)) File.Delete(authPath);
            index.ActiveProfileId = string.Empty;
            ArchiveModelCache();
        }
        index.Profiles.Remove(profile);
        SaveIndex(index);
        if (File.Exists(protectedPath)) File.Delete(protectedPath);
    }

    private static List<byte[]> NormalizeImportedRecords(ReadOnlySpan<byte> source)
    {
        var copy = source.ToArray();
        try
        {
            using var document = JsonDocument.Parse(copy);
            var roots = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { document.RootElement };
            if (roots.Count == 0) throw new InvalidDataException("账号 JSON 数组为空。");
            var result = new List<byte[]>();
            try
            {
                foreach (var root in roots)
                {
                    var auth = NormalizeImportedRecord(root);
                    ValidateAuth(auth);
                    result.Add(auth);
                }
                return result;
            }
            catch
            {
                foreach (var auth in result) CryptographicOperations.ZeroMemory(auth);
                throw;
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("账号文件不是有效 JSON。", ex); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    private static byte[] NormalizeImportedRecord(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("账号 JSON 条目必须是对象。");
        if (root.TryGetProperty("auth_mode", out _)) return Encoding.UTF8.GetBytes(root.GetRawText());
        if (!root.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "codex", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不支持的账号 JSON；仅支持 Codex 官方、CPA Codex 或 Sub2API Codex 格式。");
        return CreateOfficialRecord(root);
    }

    private static byte[] CreateOfficialRecord(JsonElement source)
    {
        var tokens = source.TryGetProperty("tokens", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : source;
        var access = ReadString(tokens, "access_token");
        var refresh = ReadString(tokens, "refresh_token");
        var idToken = ReadString(tokens, "id_token");
        if (string.IsNullOrWhiteSpace(access) && string.IsNullOrWhiteSpace(refresh))
            throw new InvalidDataException("账号 JSON 缺少 access_token 和 refresh_token。");
        var accountId = ReadString(source, "account_id");
        if (string.IsNullOrWhiteSpace(accountId)) accountId = ReadString(tokens, "account_id");
        var payload = new Dictionary<string, object?>
        {
            ["auth_mode"] = "chatgpt",
            ["account_id"] = accountId,
            ["tokens"] = new Dictionary<string, string?>
            {
                ["access_token"] = access, ["refresh_token"] = refresh, ["id_token"] = idToken, ["account_id"] = accountId
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static byte[] CreateCpaRecord(ReadOnlySpan<byte> auth)
    {
        using var document = JsonDocument.Parse(auth.ToArray());
        var root = document.RootElement;
        var tokens = root.TryGetProperty("tokens", out var value) ? value : root;
        var idToken = ReadString(tokens, "id_token");
        var accountId = ReadString(root, "account_id");
        var record = new Dictionary<string, object?>
        {
            ["type"] = "codex", ["account_id"] = accountId,
            ["access_token"] = ReadString(tokens, "access_token"), ["refresh_token"] = ReadString(tokens, "refresh_token"),
            ["id_token"] = idToken, ["email"] = TryReadJwtClaim(idToken, "email"), ["last_refresh"] = DateTime.UtcNow.ToString("O")
        };
        return JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonElement CreateSub2Record(string label, ReadOnlySpan<byte> auth)
    {
        using var document = JsonDocument.Parse(auth.ToArray());
        var root = document.RootElement;
        var tokens = root.TryGetProperty("tokens", out var value) ? value : root;
        var idToken = ReadString(tokens, "id_token");
        var record = new Dictionary<string, object?>
        {
            ["type"] = "codex", ["name"] = label, ["account_id"] = ReadString(root, "account_id"),
            ["email"] = TryReadJwtClaim(idToken, "email"),
            ["tokens"] = new Dictionary<string, string?> { ["access_token"] = ReadString(tokens, "access_token"), ["refresh_token"] = ReadString(tokens, "refresh_token"), ["id_token"] = idToken }
        };
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(record));
        return result.RootElement.Clone();
    }

    private static string ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private void AssertSafeToWrite()
    {
        if (!Directory.Exists(codexRoot)) throw new DirectoryNotFoundException("Codex 根目录不存在：" + codexRoot);
        if (IsLiveCodexRoot() && processes.GetRunningProcesses().Count > 0)
            throw new InvalidOperationException("Codex 仍在运行，请先安全退出后重试。");
    }

    private bool IsLiveCodexRoot() => string.Equals(
        codexRoot.TrimEnd(Path.DirectorySeparatorChar),
        CodexRootResolver.GetDefaultRoot().TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    private byte[] ReadAndValidateAuth()
    {
        if (!File.Exists(authPath)) throw new FileNotFoundException("auth.json 不存在，请先在 Codex 中完成官方账号登录。", authPath);
        var bytes = File.ReadAllBytes(authPath);
        ValidateAuth(bytes);
        return bytes;
    }

    public static void ValidateAuth(ReadOnlySpan<byte> bytes)
    {
        var copy = bytes.ToArray();
        try
        {
            using var document = JsonDocument.Parse(copy);
            if (!document.RootElement.TryGetProperty("auth_mode", out _)) throw new InvalidDataException();
        }
        catch (JsonException ex) { throw new InvalidDataException("auth.json 不是有效 JSON。", ex); }
        catch (InvalidDataException) { throw new InvalidDataException("auth.json 缺少 auth_mode，不是有效的 Codex 登录文件。"); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    private static string ReadIdentity(ReadOnlySpan<byte> bytes)
    {
        var copy = bytes.ToArray();
        try
        {
            using var document = JsonDocument.Parse(copy);
            var root = document.RootElement;
            if (root.TryGetProperty("account_id", out var account)) return account.GetString() ?? string.Empty;
            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                if (tokens.TryGetProperty("account_id", out account)) return account.GetString() ?? string.Empty;
                if (tokens.TryGetProperty("id_token", out var token))
                {
                    var email = TryReadJwtClaim(token.GetString(), "email");
                    if (!string.IsNullOrWhiteSpace(email)) return email;
                }
            }
            return string.Empty;
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    private static string TryReadJwtClaim(string? jwt, string claim)
    {
        try
        {
            var parts = jwt?.Split('.');
            if (parts is null || parts.Length < 2) return string.Empty;
            var value = parts[1].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(value));
            return payload.RootElement.TryGetProperty(claim, out var result) ? result.GetString() ?? string.Empty : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string CreateIdentityHint(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "身份未标识";
        if (identity.Contains('@'))
        {
            var parts = identity.Split('@', 2);
            var local = parts[0].Length <= 2 ? parts[0] : parts[0][..2] + "***";
            return local + "@" + parts[1];
        }
        return identity.Length <= 8 ? identity : "…" + identity[^8..];
    }

    private static string HashIdentity(string identity) => string.IsNullOrWhiteSpace(identity)
        ? string.Empty
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    private string ProfilePath(string id) => Path.Combine(profileDirectory, id + ".dat");

    private void SaveProtected(string path, ReadOnlySpan<byte> plain)
    {
        var encrypted = DpapiProtector.Protect(plain);
        try { AtomicFile.WriteAllBytes(path, encrypted); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    private static byte[] LoadProtected(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("加密账号档案缺失。", path);
        var encrypted = File.ReadAllBytes(path);
        try { return DpapiProtector.Unprotect(encrypted); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    private void BackupCurrentAuth()
    {
        if (!File.Exists(authPath)) return;
        var current = File.ReadAllBytes(authPath);
        try { ValidateAuth(current); SaveRecovery(current); }
        finally { CryptographicOperations.ZeroMemory(current); }
    }

    private void SaveRecovery(ReadOnlySpan<byte> value) =>
        SaveProtected(Path.Combine(recoveryDirectory, "auth-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".dat"), value);

    private void SaveIndex(ConnectionIndex index)
    {
        foreach (var profile in index.Profiles) profile.IsActive = profile.Id == index.ActiveProfileId;
        json.Save(indexPath, index);
    }

    private void ArchiveModelCache()
    {
        var cache = Path.Combine(codexRoot, "models_cache.json");
        if (!File.Exists(cache)) return;
        var directory = Path.Combine(recoveryDirectory, "model-cache");
        Directory.CreateDirectory(directory);
        try { File.Move(cache, Path.Combine(directory, "models-cache-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".json")); }
        catch (IOException) { }
    }

    private static string ResolveLegacyDirectory(string selectedPath)
    {
        var full = Path.GetFullPath(selectedPath);
        var candidates = new[] { full, Path.Combine(full, "account-switcher"), Path.Combine(full, ".codex", "account-switcher") };
        return candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "index.json")))
            ?? throw new DirectoryNotFoundException("没有找到旧版 account-switcher/index.json。");
    }

    private static string UniqueLabel(ConnectionIndex index, string requested)
    {
        var baseLabel = string.IsNullOrWhiteSpace(requested) ? "导入账号" : requested.Trim();
        var label = baseLabel;
        for (var number = 2; index.Profiles.Any(item => string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase)); number++) label = baseLabel + " " + number;
        return label;
    }

    private static string CreateUniqueJsonPath(string directory, string label)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var stem = new string((string.IsNullOrWhiteSpace(label) ? "官方账号" : label.Trim()).Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "官方账号";
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) stem = "账号-" + stem;
        var path = Path.Combine(directory, stem + ".json");
        for (var number = 2; File.Exists(path); number++) path = Path.Combine(directory, $"{stem} ({number}).json");
        return path;
    }

    private static void ValidateProfileId(string id)
    {
        if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("账号档案 ID 无效。");
    }

    private sealed class LegacyIndex
    {
        public List<LegacyProfile>? Profiles { get; set; }
    }

    private sealed class LegacyProfile
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }
}
