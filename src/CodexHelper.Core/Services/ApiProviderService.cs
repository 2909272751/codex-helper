using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// Provider profile and conservative configuration switching based on the
/// transaction and credential-helper design of codex-api-switcher.
/// </summary>
public sealed class ApiProviderService
{
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("CodexApiSwitcher-v1");
    private readonly string codexRoot;
    private readonly string configPath;
    private readonly string providerDirectory;
    private readonly string indexPath;
    private readonly string recoveryDirectory;
    private readonly string stableHelperPath;
    private readonly CodexProcessService processes;
    private readonly JsonStore json = new();
    private const string OpenCodeGoSkillName = "codex-helper-dual-model";

    public ApiProviderService(string rootPath, AppPaths paths, CodexProcessService processes)
    {
        codexRoot = Path.GetFullPath(rootPath);
        configPath = Path.Combine(codexRoot, "config.toml");
        providerDirectory = Path.Combine(paths.VaultDirectory, "providers");
        indexPath = Path.Combine(paths.VaultDirectory, "connections.json");
        recoveryDirectory = Path.Combine(paths.RecoveryDirectory, "provider-switches");
        stableHelperPath = Path.Combine(codexRoot, "codex-helper", "bin", "CodexHelperCredentialHelper.exe");
        this.processes = processes;
        Directory.CreateDirectory(providerDirectory);
        Directory.CreateDirectory(recoveryDirectory);
    }

    public ConnectionProfile SaveProfile(string label, ConnectionKind kind, string baseUrl, string model, string? apiKey)
    {
        if (kind is not (ConnectionKind.CustomApi or ConnectionKind.Sub2Api)) throw new ArgumentOutOfRangeException(nameof(kind));
        var cleanUrl = NormalizeBaseUrl(baseUrl);
        var cleanModel = model.Trim();
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(cleanModel)) throw new InvalidOperationException("名称和模型不能为空。");
        var index = LoadIndex();
        var profile = index.Profiles.FirstOrDefault(item => item.Kind == kind && string.Equals(item.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new ConnectionProfile { Label = label.Trim(), Kind = kind };
            index.Profiles.Add(profile);
        }
        if (!string.IsNullOrWhiteSpace(apiKey)) SaveSecret(profile.Id, apiKey.Trim());
        else if (!File.Exists(SecretPath(profile.Id))) throw new InvalidOperationException("首次保存 API 档案时必须填写 API Key。");
        profile.BaseUrl = cleanUrl;
        profile.Model = cleanModel;
        profile.UpdatedUtc = DateTime.UtcNow;
        profile.RequiresAttention = cleanUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLoopback(cleanUrl);
        profile.StatusMessage = profile.RequiresAttention ? "远程 HTTP 会明文传输凭据，切换前需要再次确认。" : "尚未验证";
        SaveIndex(index);
        return profile;
    }

    public ConnectionProfile SaveOpenCodeGoProfile(string label, string model, string? apiKey)
    {
        var cleanModel = model.Trim();
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(cleanModel)) throw new InvalidOperationException("名称和模型不能为空。");
        var index = LoadIndex();
        var profile = index.Profiles.FirstOrDefault(item => item.Kind == ConnectionKind.OpenCodeGo && string.Equals(item.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new ConnectionProfile { Label = label.Trim(), Kind = ConnectionKind.OpenCodeGo };
            index.Profiles.Add(profile);
        }
        if (!string.IsNullOrWhiteSpace(apiKey)) SaveSecret(profile.Id, apiKey.Trim());
        else if (!File.Exists(SecretPath(profile.Id))) throw new InvalidOperationException("首次保存 OpenCode Go 档案时必须填写 API Key。");
        profile.BaseUrl = OpenCodeGoExecutor.BaseUrl;
        profile.Model = cleanModel;
        profile.UpdatedUtc = DateTime.UtcNow;
        profile.RequiresAttention = false;
        profile.StatusMessage = "尚未验证；启用后不会替换 GPT 主模型";
        SaveIndex(index);
        return profile;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = RequireProvider(profileId);
        if (profile.Kind == ConnectionKind.OpenCodeGo)
            return await new OpenCodeGoExecutor().ListModelsAsync(ReadSecret(profileId), cancellationToken);
        using var client = CreateClient(ReadSecret(profileId));
        using var response = await client.GetAsync(profile.BaseUrl.TrimEnd('/') + "/models", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"读取模型失败（HTTP {(int)response.StatusCode}）：{Redact(body)}");
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> TestAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = RequireProvider(profileId);
        if (profile.Kind == ConnectionKind.OpenCodeGo)
        {
            var message = await new OpenCodeGoExecutor().TestAsync(ReadSecret(profileId), profile.Model, cancellationToken);
            var goIndex = LoadIndex();
            var goSaved = goIndex.Profiles.First(item => item.Id == profileId);
            goSaved.LastVerifiedUtc = DateTime.UtcNow;
            goSaved.StatusMessage = message;
            SaveIndex(goIndex);
            return message;
        }
        using var client = CreateClient(ReadSecret(profileId));
        var payload = JsonSerializer.Serialize(new
        {
            model = profile.Model,
            input = "Reply with OK.",
            stream = false,
            tools = new[] { new { type = "function", name = "codex_helper_probe", description = "Compatibility probe.", parameters = new { type = "object", properties = new { } } } }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, profile.BaseUrl.TrimEnd('/') + "/responses")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Responses 兼容性检测失败（HTTP {(int)response.StatusCode}）：{Redact(body)}");
        var index = LoadIndex();
        var saved = index.Profiles.First(item => item.Id == profileId);
        saved.LastVerifiedUtc = DateTime.UtcNow;
        saved.StatusMessage = "Responses API 验证通过";
        saved.RequiresAttention = false;
        SaveIndex(index);
        return saved.StatusMessage;
    }

    public Task<OpenCodeGoExecutionResult> ExecuteOpenCodeGoAsync(string profileId, string workspace, string instruction, string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        var profile = RequireProvider(profileId);
        if (profile.Kind != ConnectionKind.OpenCodeGo) throw new InvalidOperationException("所选档案不是 OpenCode Go 执行档案。");
        var model = string.IsNullOrWhiteSpace(modelOverride) ? profile.Model : modelOverride.Trim();
        return new OpenCodeGoExecutor().ExecuteAsync(new OpenCodeGoExecutionRequest(ReadSecret(profileId), model, Path.GetFullPath(workspace), instruction), cancellationToken);
    }

    public void SwitchTo(string profileId, string credentialHelperSourcePath)
    {
        AssertSafeToWrite();
        var profile = RequireProvider(profileId);
        if (profile.Kind == ConnectionKind.OpenCodeGo)
            throw new InvalidOperationException("OpenCode Go 是双模型执行端，不能替换 Codex 的 GPT 主模型。请使用“启用双模型执行”。");
        if (profile.RequiresAttention && profile.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLoopback(profile.BaseUrl))
            throw new InvalidOperationException("该档案使用远程 HTTP，可能明文传输 API Key。请改用 HTTPS 后再切换。");
        EnsureHelperInstalled(credentialHelperSourcePath);
        var lines = ReadValidatedConfig();
        var currentProvider = ReadTopLevel(lines, "model_provider");
        var currentModel = ReadTopLevel(lines, "model");
        var backup = CreateConfigBackup("pre-switch");
        CodexConversationSynchronizer? conversation = null;
        try
        {
            conversation = CodexConversationSynchronizer.BeginAndApply(codexRoot, recoveryDirectory, profile.Kind == ConnectionKind.Sub2Api ? "sub2api" : "custom");
            SetTopLevel(lines, "model_provider", profile.Kind == ConnectionKind.Sub2Api ? "sub2api" : "custom");
            SetTopLevel(lines, "model", profile.Model);
            RemoveManagedProviderSections(lines);
            AddProviderSection(lines, profile);
            WriteConfig(lines);
            var index = LoadIndex();
            foreach (var item in index.Profiles) item.IsActive = item.Id == profileId;
            index.ActiveProfileId = profileId;
            if (string.Equals(currentProvider, "openai", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(currentModel))
            {
                var metadata = json.LoadOrCreate(MetadataPath(), () => new ProviderMetadata());
                metadata.LastOfficialModel = currentModel;
                json.Save(MetadataPath(), metadata);
            }
            SaveIndex(index);
            conversation.Complete();
        }
        catch
        {
            conversation?.Rollback();
            File.Copy(backup, configPath, overwrite: true);
            throw;
        }
        finally { conversation?.Dispose(); }
    }

    public void SwitchToOfficial(string? model = null)
    {
        AssertSafeToWrite();
        var lines = ReadValidatedConfig();
        var metadata = json.LoadOrCreate(MetadataPath(), () => new ProviderMetadata());
        var targetModel = string.IsNullOrWhiteSpace(model) ? metadata.LastOfficialModel : model.Trim();
        if (string.IsNullOrWhiteSpace(targetModel)) targetModel = ReadTopLevel(lines, "model");
        if (string.IsNullOrWhiteSpace(targetModel)) throw new InvalidOperationException("无法确定官方模型，请先填写官方模型。");
        var backup = CreateConfigBackup("pre-official-switch");
        CodexConversationSynchronizer? conversation = null;
        try
        {
            conversation = CodexConversationSynchronizer.BeginAndApply(codexRoot, recoveryDirectory, "openai");
            SetTopLevel(lines, "model_provider", "openai");
            SetTopLevel(lines, "model", targetModel);
            WriteConfig(lines);
            var index = LoadIndex();
            index.ActiveProfileId = string.Empty;
            foreach (var item in index.Profiles) item.IsActive = false;
            SaveIndex(index);
            metadata.LastOfficialModel = targetModel;
            json.Save(MetadataPath(), metadata);
            conversation.Complete();
        }
        catch
        {
            conversation?.Rollback();
            File.Copy(backup, configPath, overwrite: true);
            throw;
        }
        finally { conversation?.Dispose(); }
    }

    public string EmitSecret(string profileId) => ReadSecret(profileId);

    public IReadOnlyList<ConnectionProfile> GetProfiles() => LoadIndex().Profiles
        .Where(item => item.Kind is ConnectionKind.CustomApi or ConnectionKind.Sub2Api or ConnectionKind.OpenCodeGo)
        .ToList();

    public void EnableDualModel(string profileId, string credentialHelperSourcePath)
    {
        AssertSafeToWrite();
        var profile = RequireProvider(profileId);
        if (profile.Kind != ConnectionKind.OpenCodeGo) throw new InvalidOperationException("请选择 OpenCode Go 执行档案。");
        EnsureHelperInstalled(credentialHelperSourcePath);
        var skillDirectory = Path.Combine(codexRoot, "skills", OpenCodeGoSkillName);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        var backup = CreateDualModelBackup(skillPath, "enable");
        try
        {
            Directory.CreateDirectory(skillDirectory);
            AtomicFile.WriteAllText(skillPath, BuildDualModelSkill(profile.Id, profile.Model));
            var index = LoadIndex();
            foreach (var item in index.Profiles.Where(item => item.Kind == ConnectionKind.OpenCodeGo)) item.IsDualModelEnabled = item.Id == profileId;
            index.Profiles.First(item => item.Id == profileId).StatusMessage = "双模型执行已启用；GPT 保持为主控";
            SaveIndex(index);
        }
        catch
        {
            RestoreDualModelBackup(skillPath, backup);
            throw;
        }
    }

    public void DisableDualModel()
    {
        AssertSafeToWrite();
        var skillPath = Path.Combine(codexRoot, "skills", OpenCodeGoSkillName, "SKILL.md");
        var backup = CreateDualModelBackup(skillPath, "disable");
        try
        {
            if (File.Exists(skillPath)) File.Delete(skillPath);
            var directory = Path.GetDirectoryName(skillPath)!;
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            var index = LoadIndex();
            foreach (var item in index.Profiles.Where(item => item.Kind == ConnectionKind.OpenCodeGo)) item.IsDualModelEnabled = false;
            SaveIndex(index);
        }
        catch
        {
            RestoreDualModelBackup(skillPath, backup);
            throw;
        }
    }

    public byte[] ExportSecretBytesForBundle(string profileId)
    {
        _ = RequireProvider(profileId);
        return ReadSecretBytes(profileId);
    }

    public ConnectionProfile ImportDecryptedProfile(string label, ConnectionKind kind, string baseUrl, string model, ReadOnlySpan<byte> secretBytes)
    {
        if (kind is not (ConnectionKind.CustomApi or ConnectionKind.Sub2Api or ConnectionKind.OpenCodeGo)) throw new InvalidDataException("API 连接档案类型无效。");
        if (secretBytes.IsEmpty) throw new InvalidDataException("API 连接档案缺少 API Key。");
        var profile = SaveProfileMetadata(UniqueLabel(LoadIndex(), label), kind, kind == ConnectionKind.OpenCodeGo ? OpenCodeGoExecutor.BaseUrl : baseUrl, model);
        SaveSecretBytes(profile.Id, secretBytes);
        return profile;
    }

    public int ImportLegacyDirectory(string selectedPath)
    {
        var directory = ResolveLegacyDirectory(selectedPath);
        var values = ReadLegacySettings(Path.Combine(directory, "settings.dat"));
        var imported = 0;
        imported += ImportLegacyProfile(directory, values, ConnectionKind.CustomApi, "旧版第三方 API", "url", "thirdModel", "credential.dat");
        imported += ImportLegacyProfile(directory, values, ConnectionKind.Sub2Api, "旧版 Sub2API", "sub2Url", "sub2Model", "sub2api-credential.dat");
        return imported;
    }

    private ConnectionProfile RequireProvider(string profileId)
    {
        ValidateProfileId(profileId);
        return LoadIndex().Profiles.FirstOrDefault(item => item.Id == profileId && item.Kind is ConnectionKind.CustomApi or ConnectionKind.Sub2Api or ConnectionKind.OpenCodeGo)
            ?? throw new InvalidOperationException("未找到 API 连接档案。");
    }

    private ConnectionIndex LoadIndex() => json.LoadOrCreate(indexPath, () => new ConnectionIndex());
    private void SaveIndex(ConnectionIndex index) => json.Save(indexPath, index);
    private string MetadataPath() => Path.Combine(providerDirectory, "metadata.json");
    private string SecretPath(string id) => Path.Combine(providerDirectory, id + ".dat");

    private void SaveSecret(string id, string secret)
    {
        ValidateProfileId(id);
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var encrypted = DpapiProtector.Protect(plain);
            try { AtomicFile.WriteAllBytes(SecretPath(id), encrypted); }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private string ReadSecret(string id)
    {
        var plain = ReadSecretBytes(id);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private byte[] ReadSecretBytes(string id)
    {
        ValidateProfileId(id);
        var encrypted = File.ReadAllBytes(SecretPath(id));
        try
        {
            return DpapiProtector.Unprotect(encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    private void SaveSecretBytes(string id, ReadOnlySpan<byte> secret)
    {
        ValidateProfileId(id);
        var encrypted = DpapiProtector.Protect(secret);
        try { AtomicFile.WriteAllBytes(SecretPath(id), encrypted); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    private ConnectionProfile SaveProfileMetadata(string label, ConnectionKind kind, string baseUrl, string model)
    {
        var cleanUrl = NormalizeBaseUrl(baseUrl);
        var cleanModel = model.Trim();
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(cleanModel)) throw new InvalidOperationException("名称和模型不能为空。");
        var index = LoadIndex();
        var profile = new ConnectionProfile { Label = label.Trim(), Kind = kind, BaseUrl = cleanUrl, Model = cleanModel };
        profile.RequiresAttention = cleanUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLoopback(cleanUrl);
        profile.StatusMessage = profile.RequiresAttention ? "远程 HTTP 连接需要改为 HTTPS 后才能切换。" : "已导入，尚未验证";
        index.Profiles.Add(profile);
        SaveIndex(index);
        return profile;
    }

    private string CreateDualModelBackup(string skillPath, string purpose)
    {
        Directory.CreateDirectory(recoveryDirectory);
        var backup = Path.Combine(recoveryDirectory, $"dual-model-{purpose}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.bak");
        if (File.Exists(skillPath)) File.Copy(skillPath, backup, overwrite: false);
        else File.WriteAllText(backup, "# Codex Helper dual-model skill did not exist before this operation.\n", Encoding.UTF8);
        return backup;
    }

    private static void RestoreDualModelBackup(string skillPath, string backup)
    {
        var contents = File.ReadAllText(backup, Encoding.UTF8);
        if (contents.StartsWith("# Codex Helper dual-model skill did not exist", StringComparison.Ordinal))
        {
            if (File.Exists(skillPath)) File.Delete(skillPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        AtomicFile.WriteAllText(skillPath, contents);
    }

    private string BuildDualModelSkill(string profileId, string model) => $$"""
---
name: codex-helper-dual-model
description: Use GPT as the planner and reviewer while delegating implementation and verification to the configured OpenCode Go model.
---

# Codex Helper Dual Model

Use this workflow when the user asks to save GPT/Codex usage through an external execution agent.

1. Inspect the request and repository. Write a compact task contract that includes the goal, acceptance criteria, relevant tests, and any constraints.
2. Save the contract to a temporary UTF-8 text file. Do not include secrets in it.
3. Run the command below, replacing `<contract-file>` with that file and `<workspace>` with the repository root:

```powershell
& '{{EscapePowerShell(stableHelperPath)}}' --root '{{EscapePowerShell(codexRoot)}}' --profile '{{profileId}}' --execute-go --model '{{EscapePowerShell(model)}}' --workspace '<workspace>' --instruction-file '<contract-file>'
```

4. The execution model is authorized by this enabled profile to edit, test, commit, and push by default. Wait for its evidence. Never include secrets in commands, commits, or output.
5. Independently inspect the final diff and test evidence. If it misses acceptance criteria, issue one targeted follow-up through the same command. After two failed repairs, take over with GPT.

The main Codex model remains the planner and final reviewer. Never print or request the Go API key.
""";

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private int ImportLegacyProfile(string directory, IReadOnlyDictionary<string, string> values, ConnectionKind kind, string label, string urlKey, string modelKey, string credentialName)
    {
        if (!values.TryGetValue(urlKey, out var url) || string.IsNullOrWhiteSpace(url) ||
            !values.TryGetValue(modelKey, out var model) || string.IsNullOrWhiteSpace(model)) return 0;
        var credentialPath = Path.Combine(directory, credentialName);
        if (!File.Exists(credentialPath)) return 0;
        var encrypted = File.ReadAllBytes(credentialPath);
        byte[] plain;
        try { plain = DpapiProtector.Unprotect(encrypted, LegacyEntropy); }
        catch (CryptographicException) { return 0; }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
        try
        {
            ImportDecryptedProfile(label, kind, url, model, plain);
            return 1;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static Dictionary<string, string> ReadLegacySettings(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            try { result[line[..separator]] = Encoding.UTF8.GetString(Convert.FromBase64String(line[(separator + 1)..])); }
            catch (FormatException) { }
        }
        return result;
    }

    private static string ResolveLegacyDirectory(string selectedPath)
    {
        var full = Path.GetFullPath(selectedPath);
        var candidates = new[] { full, Path.Combine(full, "api-switcher"), Path.Combine(full, ".codex", "api-switcher") };
        return candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "settings.dat")))
            ?? throw new DirectoryNotFoundException("没有找到旧版 api-switcher/settings.dat。");
    }

    private static string UniqueLabel(ConnectionIndex index, string requested)
    {
        var baseLabel = string.IsNullOrWhiteSpace(requested) ? "导入 API" : requested.Trim();
        var label = baseLabel;
        for (var number = 2; index.Profiles.Any(item => string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase)); number++) label = baseLabel + " " + number;
        return label;
    }

    private void AssertSafeToWrite()
    {
        if (!File.Exists(configPath)) throw new FileNotFoundException("Codex config.toml 不存在。", configPath);
        if (string.Equals(codexRoot.TrimEnd('\\'), CodexRootResolver.GetDefaultRoot().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && processes.GetRunningProcesses().Count > 0)
            throw new InvalidOperationException("Codex 仍在运行，请先安全退出后重试。");
    }

    private List<string> ReadValidatedConfig()
    {
        var lines = File.ReadAllLines(configPath, Encoding.UTF8).ToList();
        _ = TomlConfigurationDocument.Parse(lines);
        return lines;
    }

    private string CreateConfigBackup(string purpose)
    {
        Directory.CreateDirectory(recoveryDirectory);
        var destination = Path.Combine(recoveryDirectory, $"config-{purpose}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.toml");
        File.Copy(configPath, destination, overwrite: false);
        return destination;
    }

    private void WriteConfig(List<string> lines)
    {
        _ = TomlConfigurationDocument.Parse(lines);
        AtomicFile.WriteAllText(configPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private void EnsureHelperInstalled(string sourcePath)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("凭据助手程序不存在。", source);
        Directory.CreateDirectory(Path.GetDirectoryName(stableHelperPath)!);
        var temporary = stableHelperPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            if (File.Exists(stableHelperPath)) File.Replace(temporary, stableHelperPath, null, ignoreMetadataErrors: true);
            else File.Move(temporary, stableHelperPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void AddProviderSection(List<string> lines, ConnectionProfile profile)
    {
        var providerId = profile.Kind == ConnectionKind.Sub2Api ? "sub2api" : "custom";
        lines.Add(string.Empty);
        lines.Add($"[model_providers.{providerId}]");
        lines.Add($"name = \"{EscapeToml(profile.Label)}\"");
        lines.Add("wire_api = \"responses\"");
        lines.Add($"base_url = \"{EscapeToml(profile.BaseUrl)}\"");
        if (profile.Kind == ConnectionKind.Sub2Api) lines.Add("supports_websockets = false");
        lines.Add(string.Empty);
        lines.Add($"[model_providers.{providerId}.auth]");
        lines.Add($"command = \"{EscapeToml(stableHelperPath)}\"");
        lines.Add($"args = [\"--root\", \"{EscapeToml(codexRoot)}\", \"--profile\", \"{profile.Id}\"]");
        lines.Add("timeout_ms = 5000");
        lines.Add("refresh_interval_ms = 0");
    }

    private static void RemoveManagedProviderSections(List<string> lines)
    {
        var result = new List<string>();
        var skip = false;
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"^\s*\[([^\]]+)\]\s*$");
            if (match.Success)
            {
                var section = match.Groups[1].Value;
                skip = section is "model_providers.custom" or "model_providers.sub2api" ||
                       section.StartsWith("model_providers.custom.", StringComparison.Ordinal) ||
                       section.StartsWith("model_providers.sub2api.", StringComparison.Ordinal);
            }
            if (!skip) result.Add(line);
        }
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        lines.Clear();
        lines.AddRange(result);
    }

    private static string ReadTopLevel(List<string> lines, string key)
    {
        var firstSection = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\["));
        if (firstSection < 0) firstSection = lines.Count;
        var regex = new Regex("^\\s*" + Regex.Escape(key) + "\\s*=\\s*[\"'](.*?)[\"']\\s*(?:#.*)?$");
        for (var index = 0; index < firstSection; index++)
        {
            var match = regex.Match(lines[index]);
            if (match.Success) return match.Groups[1].Value;
        }
        return string.Empty;
    }

    private static void SetTopLevel(List<string> lines, string key, string value)
    {
        var firstSection = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\["));
        if (firstSection < 0) firstSection = lines.Count;
        var expression = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=");
        var replacement = $"{key} = \"{EscapeToml(value)}\"";
        var found = false;
        for (var index = 0; index < firstSection; index++)
        {
            if (!expression.IsMatch(lines[index])) continue;
            if (!found) { lines[index] = replacement; found = true; }
            else { lines.RemoveAt(index--); firstSection--; }
        }
        if (!found) lines.Insert(firstSection, replacement);
    }

    public static string NormalizeBaseUrl(string value)
    {
        var clean = (value ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Base URL 必须是有效的 HTTP/HTTPS 绝对地址。");
        if (!clean.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) clean += "/v1";
        return clean;
    }

    private static bool IsLoopback(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static HttpClient CreateClient(string key)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexHelper/0.1");
        return client;
    }

    private static string EscapeToml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Redact(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "无响应正文";
        var clean = value.Replace('\r', ' ').Replace('\n', ' ');
        return clean[..Math.Min(clean.Length, 300)];
    }

    private static void ValidateProfileId(string id)
    {
        if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("API 档案 ID 无效。");
    }

    private sealed class ProviderMetadata
    {
        public string LastOfficialModel { get; set; } = string.Empty;
    }
}
