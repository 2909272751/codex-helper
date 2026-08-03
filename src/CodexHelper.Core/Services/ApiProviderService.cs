using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        if (!IsApiProfile(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
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
        profile.AgentContextWindow = IsOfficialDeepSeek(cleanUrl, cleanModel) ? 1_048_576 : Math.Max(profile.AgentContextWindow, 128_000);
        profile.UpdatedUtc = DateTime.UtcNow;
        profile.RequiresAttention = cleanUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLoopback(cleanUrl);
        profile.StatusMessage = profile.RequiresAttention ? "远程 HTTP 会明文传输凭据，切换前需要再次确认。" : "尚未验证";
        SaveIndex(index);
        return profile;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = RequireProvider(profileId);
        EnsureSafeRemoteEndpoint(profile);
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
        EnsureSafeRemoteEndpoint(profile);
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

    /// <summary>
    /// Keeps the current Codex main model untouched and adds a narrowly scoped
    /// DeepSeek coding agent.  Task content is delivered through a per-task
    /// absolute pointer under an explicitly selected relay root, rather than
    /// relying on the encrypted child-task payload or a fixed workspace file.
    /// </summary>
    public void EnableDeepSeekPlanWorker(string profileId, string credentialHelperSourcePath, string reasoningEffort = "high")
    {
        AssertSafeToWrite();
        reasoningEffort = NormalizeDeepSeekReasoningEffort(reasoningEffort);
        var profile = RequireProvider(profileId);
        if (profile.Kind != ConnectionKind.CustomApi || !IsOfficialDeepSeek(profile.BaseUrl, profile.Model))
            throw new InvalidOperationException("请选择已验证的 DeepSeek 官方 Responses 档案（deepseek-v4-flash）。这不会切换 Codex 主模型。");

        var lines = ReadValidatedConfig();
        if (!UsesOfficialMainModel(lines))
            throw new InvalidOperationException("请先将 Codex 切回官方 GPT 主模型，再启用 DeepSeek 开发协作。该模式不会替换主模型。");
        var metadata = json.LoadOrCreate(MetadataPath(), () => new ProviderMetadata());
        var catalogPlan = PrepareCatalogPlan(lines, metadata, enableDeepSeek: true);
        var agentPath = Path.Combine(codexRoot, "agents", "deepseek_plan_worker.toml");
        var guidancePath = Path.Combine(codexRoot, "AGENTS.md");
        var snapshots = CapturePlanWorkerFiles(agentPath, guidancePath);
        try
        {
            EnsureHelperInstalled(credentialHelperSourcePath);
            RemoveLegacySubagentResidue(lines);
            RemovePlanWorkerProviderSections(lines);
            AddPlanWorkerProviderSection(lines, profile);
            SetSectionValue(lines, "agents", "enabled", "true");
            SetSectionValue(lines, "agents", "max_concurrent_threads_per_session", "1");
            ApplyCatalogPlan(lines, metadata, catalogPlan);
            WriteManagedPlanWorker(agentPath, profile.Model, reasoningEffort);
            WritePlanRelayGuidance(guidancePath);
            metadata.PlanWorkerActive = true;
            metadata.PlanWorkerProfileId = profile.Id;
            SaveIndex(LoadIndex());
            json.Save(MetadataPath(), metadata);
            WriteConfig(lines);
        }
        catch
        {
            RestoreSnapshots(snapshots);
            throw;
        }
    }

    public void DisableDeepSeekPlanWorker()
    {
        AssertSafeToWrite();
        var lines = ReadValidatedConfig();
        var metadata = json.LoadOrCreate(MetadataPath(), () => new ProviderMetadata());
        if (!UsesOfficialMainModel(lines))
            throw new InvalidOperationException("当前主模型不是官方 GPT。为避免影响正在使用的主模型，请先切回官方 GPT 后再关闭 DeepSeek 开发协作。");
        var agentPath = Path.Combine(codexRoot, "agents", "deepseek_plan_worker.toml");
        var guidancePath = Path.Combine(codexRoot, "AGENTS.md");
        var snapshots = CapturePlanWorkerFiles(agentPath, guidancePath);
        try
        {
            RemoveLegacySubagentResidue(lines);
            RemovePlanWorkerProviderSections(lines);
            RemoveManagedPlanWorker(agentPath);
            RemovePlanRelayGuidance(guidancePath);
            SetSectionValue(lines, "agents", "enabled", "false");
            ApplyCatalogPlan(lines, metadata, PrepareCatalogPlan(lines, metadata, enableDeepSeek: false));
            metadata.PlanWorkerActive = false;
            metadata.PlanWorkerProfileId = string.Empty;
            json.Save(MetadataPath(), metadata);
            WriteConfig(lines);
        }
        catch
        {
            RestoreSnapshots(snapshots);
            throw;
        }
    }

    public bool IsDeepSeekPlanWorkerEnabled()
    {
        var agentPath = Path.Combine(codexRoot, "agents", "deepseek_plan_worker.toml");
        return File.Exists(agentPath) && File.ReadAllText(agentPath).Contains("CODEX-HELPER-DEEPSEEK-PLAN-WORKER", StringComparison.Ordinal);
    }

    public string GetDeepSeekPlanWorkerReasoningEffort()
    {
        var agentPath = Path.Combine(codexRoot, "agents", "deepseek_plan_worker.toml");
        if (!IsManagedPlanWorker(agentPath)) return "high";
        var match = Regex.Match(File.ReadAllText(agentPath), "^model_reasoning_effort\\s*=\\s*\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Multiline);
        return match.Success ? NormalizeDeepSeekReasoningEffort(match.Groups["value"].Value) : "high";
    }

    public void UpdateDeepSeekPlanWorkerReasoningEffort(string reasoningEffort)
    {
        AssertSafeToWrite();
        reasoningEffort = NormalizeDeepSeekReasoningEffort(reasoningEffort);
        var agentPath = Path.Combine(codexRoot, "agents", "deepseek_plan_worker.toml");
        if (!IsManagedPlanWorker(agentPath)) throw new InvalidOperationException("请先开启 DeepSeek 编码子智能体，再调整思考强度。");
        var content = File.ReadAllText(agentPath);
        var modelMatch = Regex.Match(content, "^model\\s*=\\s*\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Multiline);
        if (!modelMatch.Success) throw new InvalidDataException("DeepSeek 子智能体配置不完整，无法读取模型名称。");
        var snapshot = new SwitchFileSnapshot(agentPath, true, BackupIfExists(agentPath, "deepseek-reasoning-effort"));
        try
        {
            WriteManagedPlanWorker(agentPath, modelMatch.Groups["value"].Value, reasoningEffort);
        }
        catch
        {
            RestoreSnapshots(new[] { snapshot });
            throw;
        }
    }

    public void SwitchTo(string profileId, string credentialHelperSourcePath)
    {
        AssertSafeToWrite();
        var profile = RequireProvider(profileId);
        if (profile.Kind == ConnectionKind.ResponsesSubagent)
            throw new InvalidOperationException("这是旧版子智能体档案。请先在连接中心修复为普通 Responses API 档案。");
        var usesRemoteHttp = profile.RequiresAttention && profile.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLoopback(profile.BaseUrl);
        if (usesRemoteHttp)
            throw new InvalidOperationException("该档案使用远程 HTTP，可能明文传输 API Key。请改用 HTTPS 后再切换。");
        var lines = ReadValidatedConfig();
        var currentProvider = ReadTopLevel(lines, "model_provider");
        var currentModel = ReadTopLevel(lines, "model");
        var metadata = json.LoadOrCreate(MetadataPath(), () => new ProviderMetadata());
        var catalogPlan = PrepareCatalogPlan(lines, metadata, IsOfficialDeepSeek(profile.BaseUrl, profile.Model));
        var snapshots = CaptureSwitchFiles("pre-switch");
        CodexConversationSynchronizer? conversation = null;
        try
        {
            EnsureHelperInstalled(credentialHelperSourcePath);
            conversation = CodexConversationSynchronizer.BeginAndApply(codexRoot, recoveryDirectory, profile.Kind == ConnectionKind.Sub2Api ? "sub2api" : "custom");
            SetTopLevel(lines, "model_provider", profile.Kind == ConnectionKind.Sub2Api ? "sub2api" : "custom");
            SetTopLevel(lines, "model", profile.Model);
            RemoveManagedProviderSections(lines);
            AddProviderSection(lines, profile);
            ApplyCatalogPlan(lines, metadata, catalogPlan);
            WriteConfig(lines);
            var index = LoadIndex();
            foreach (var item in index.Profiles) item.IsActive = item.Id == profileId;
            index.ActiveProfileId = profileId;
            if (string.Equals(currentProvider, "openai", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(currentModel))
                metadata.LastOfficialModel = currentModel;
            SaveIndex(index);
            json.Save(MetadataPath(), metadata);
            conversation.Complete();
        }
        catch
        {
            conversation?.Rollback();
            RestoreSnapshots(snapshots);
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
        var catalogPlan = PrepareCatalogPlan(lines, metadata, false);
        var snapshots = CaptureSwitchFiles("pre-official-switch");
        CodexConversationSynchronizer? conversation = null;
        try
        {
            conversation = CodexConversationSynchronizer.BeginAndApply(codexRoot, recoveryDirectory, "openai");
            SetTopLevel(lines, "model_provider", "openai");
            SetTopLevel(lines, "model", targetModel);
            ApplyCatalogPlan(lines, metadata, catalogPlan);
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
            RestoreSnapshots(snapshots);
            throw;
        }
        finally { conversation?.Dispose(); }
    }

    public string EmitSecret(string profileId) => ReadSecret(profileId);

    public IReadOnlyList<ConnectionProfile> GetProfiles() => LoadIndex().Profiles
        .Where(item => IsApiProfile(item.Kind))
        .ToList();

    public IReadOnlyList<ConnectionProfile> GetDeepSeekPlanWorkerProfiles() => LoadIndex().Profiles
        .Where(item => item.Kind == ConnectionKind.CustomApi && IsOfficialDeepSeek(item.BaseUrl, item.Model))
        .OrderByDescending(item => item.LastVerifiedUtc)
        .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Removes an inactive API profile after retaining a DPAPI-protected recovery copy.</summary>
    public void DeleteProfile(string profileId)
    {
        ValidateProfileId(profileId);
        var index = LoadIndex();
        var profile = index.Profiles.FirstOrDefault(item => item.Id == profileId && item.Kind != ConnectionKind.OfficialAccount)
            ?? throw new InvalidOperationException("未找到要删除的 API 连接档案。");
        if (string.Equals(index.ActiveProfileId, profileId, StringComparison.Ordinal))
            throw new InvalidOperationException("该 API 档案正在使用。请先切换到其他连接或官方模式后再删除。");
        var secretPath = SecretPath(profileId);
        if (File.Exists(secretPath))
        {
            var encrypted = File.ReadAllBytes(secretPath);
            try
            {
                var deletedDirectory = Path.Combine(recoveryDirectory, "deleted-profiles");
                Directory.CreateDirectory(deletedDirectory);
                AtomicFile.WriteAllBytes(Path.Combine(deletedDirectory, profileId + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".dat"), encrypted);
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        index.Profiles.Remove(profile);
        SaveIndex(index);
        if (File.Exists(secretPath)) File.Delete(secretPath);
    }

    public byte[] ExportSecretBytesForBundle(string profileId)
    {
        _ = RequireProvider(profileId);
        return ReadSecretBytes(profileId);
    }

    public ConnectionProfile ImportDecryptedProfile(string label, ConnectionKind kind, string baseUrl, string model, ReadOnlySpan<byte> secretBytes)
    {
        if (!IsApiProfile(kind)) throw new InvalidDataException("API 连接档案类型无效。");
        if (secretBytes.IsEmpty) throw new InvalidDataException("API 连接档案缺少 API Key。");
        var profile = SaveProfileMetadata(UniqueLabel(LoadIndex(), label), kind, baseUrl, model);
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
        return LoadIndex().Profiles.FirstOrDefault(item => item.Id == profileId && IsApiProfile(item.Kind))
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
        var sourceDirectory = Path.GetDirectoryName(source)!;
        var destinationDirectory = Path.GetDirectoryName(stableHelperPath)!;
        var companions = new[]
        {
            "CodexHelperCredentialHelper.dll", "CodexHelperCredentialHelper.deps.json",
            "CodexHelperCredentialHelper.runtimeconfig.json", "CodexHelper.Core.dll",
            "Konscious.Security.Cryptography.Argon2.dll", "Konscious.Security.Cryptography.Blake2.dll",
            "System.Security.Cryptography.ProtectedData.dll"
        };
        foreach (var name in companions)
        {
            var candidate = Path.Combine(sourceDirectory, name);
            if (!File.Exists(candidate)) throw new FileNotFoundException($"凭据助手缺少运行依赖：{name}", candidate);
        }
        Directory.CreateDirectory(destinationDirectory);
        ReplaceFileAtomically(source, Path.Combine(destinationDirectory, "CodexHelperCredentialHelper.exe"));
        foreach (var name in companions)
            ReplaceFileAtomically(Path.Combine(sourceDirectory, name), Path.Combine(destinationDirectory, name));
    }

    private static void ReplaceFileAtomically(string source, string destination)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            if (File.Exists(destination)) File.Replace(temporary, destination, null, ignoreMetadataErrors: true);
            else File.Move(temporary, destination);
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

    private void AddPlanWorkerProviderSection(List<string> lines, ConnectionProfile profile)
    {
        const string providerId = "deepseek_plan_worker";
        lines.Add(string.Empty);
        lines.Add($"[model_providers.{providerId}]");
        lines.Add("name = \"Codex Helper DeepSeek plan worker\"");
        lines.Add("wire_api = \"responses\"");
        lines.Add($"base_url = \"{EscapeToml(profile.BaseUrl)}\"");
        lines.Add("supports_websockets = false");
        lines.Add(string.Empty);
        lines.Add($"[model_providers.{providerId}.auth]");
        lines.Add($"command = \"{EscapeToml(stableHelperPath)}\"");
        lines.Add($"args = [\"--root\", \"{EscapeToml(codexRoot)}\", \"--profile\", \"{profile.Id}\"]");
        lines.Add("timeout_ms = 5000");
        lines.Add("refresh_interval_ms = 0");
    }

    private void WriteManagedPlanWorker(string path, string model, string reasoningEffort)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = $$""""
# CODEX-HELPER-DEEPSEEK-PLAN-WORKER
name = "deepseek_plan_worker"
description = "Implements the current Codex Helper relay task with DeepSeek; use for bounded coding work after the parent has prepared a brief."
model_provider = "deepseek_plan_worker"
model = "{{EscapeToml(model)}}"
model_reasoning_effort = "{{NormalizeDeepSeekReasoningEffort(reasoningEffort)}}"
model_context_window = 1048576
model_auto_compact_token_limit = 800000
model_auto_compact_token_limit_scope = "total"
tool_output_token_limit = 8000
developer_instructions = """
You are the implementation worker. Do not rely on the spawn task body: it may be unavailable.

The wake-up message contains exactly the task id and the absolute path to that task's pointer JSON, for example: <taskId> <absolutePathToTaskPointer.json>. Read the absolute pointer path from the wake-up message. Never infer the pointer from the child working directory and never scan sibling workspaces for a pointer.

Load the pointer JSON and validate every field before acting:
- taskId must exactly match the wake-up task id;
- relayRoot, taskFile, and resultFile must be canonical absolute paths, and taskFile and resultFile must remain inside the same canonical <relayRoot>/.codex-helper-runtime root as relayRoot;
- status must be "ready";
- checksum must match the first 12 hexadecimal characters of SHA-256 over the UTF-8 bytes of the task brief at taskFile.
Then read the complete task brief from taskFile in full. If the pointer, the task brief, or any validation step is missing, stale, malformed, or not ready, stop and report the exact missing condition; do not invent a task.

Execute the task directly with the native Codex tools. Make real edits and run the stated checks. Do not create nested agents. Do not perform visual acceptance from screenshots or image files; collect factual evidence only and leave visual review to the GPT parent.

Before finishing, write a concise result record to the pointer's absolute resultFile, then update that same pointer in place to status "completed" or "blocked". Never reuse current-task.json or any stale pointer. Include changed files, commands actually run, results, remaining risks, and the result file path. Never claim completion without real tool evidence.
"""
"""";
        AtomicFile.WriteAllText(path, content + Environment.NewLine);
    }

    private static bool IsManagedPlanWorker(string path) => File.Exists(path) &&
        File.ReadAllText(path).Contains("CODEX-HELPER-DEEPSEEK-PLAN-WORKER", StringComparison.Ordinal);

    private static void RemoveManagedPlanWorker(string path)
    {
        if (IsManagedPlanWorker(path)) File.Delete(path);
    }

    private static string NormalizeDeepSeekReasoningEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "max" => "max",
        _ => throw new InvalidOperationException("DeepSeek 思考强度仅支持：低、中、高或最大。")
    };

    private static void WritePlanRelayGuidance(string path)
    {
        const string start = "<!-- CODEX-HELPER-DEEPSEEK-PLAN-RELAY-START -->";
        const string end = "<!-- CODEX-HELPER-DEEPSEEK-PLAN-RELAY-END -->";
        var block = $$"""
{{start}}
While this mode is enabled, every implementation task that changes project files (new features, fixes, refactors, tests, builds, or deliverables) MUST use `deepseek_plan_worker`. GPT owns analysis, the complete brief, and acceptance; it must not implement the project work itself and must not spawn the normal built-in worker. Pure questions, explanations, and read-only reviews stay in GPT.

Use one relay task at a time. Each relay task requires exactly one immutable pointer under an explicitly selected relay root. Resolve the authoritative relay root yourself, record it as a canonical absolute path, and keep every file under `<relayRoot>/.codex-helper-runtime`:
- task brief: `<relayRoot>/.codex-helper-runtime/tasks/task-<id>.md`
- unique pointer: `<relayRoot>/.codex-helper-runtime/pointers/task-<id>.json`
- result record: `<relayRoot>/.codex-helper-runtime/results/result-<id>.md`

The pointer must include at least: taskId; absolute taskFile; absolute resultFile; absolute relayRoot; status "ready"; createdUtc; and a checksum (first 12 hexadecimal characters of SHA-256 over the UTF-8 bytes of the task brief). All paths must be canonical absolute paths and must remain inside the same canonical `<relayRoot>/.codex-helper-runtime` root. Do not resolve `<workspace>` from the child session and do not reuse a shared pointer such as current-task.json.

Mandatory sequencing — do not skip or reorder:
1. Resolve and record the authoritative relay root as a canonical absolute path.
2. Write the complete, self-contained task brief to `<relayRoot>/.codex-helper-runtime/tasks/task-<id>.md`.
3. Atomically write the unique pointer to `<relayRoot>/.codex-helper-runtime/pointers/task-<id>.json` with the schema above.
4. Read both files back and verify the task id, checksum, canonical path containment, and "ready" status.
5. Only after verification, spawn `deepseek_plan_worker` with a wake-up message that contains only the task id and the absolute pointer path — never the task body.
Never spawn the worker first and repair the pointer afterward.

Wait for the worker. Read its result record and inspect the actual files and test output before accepting. For visual UI work, GPT performs visual acceptance; the DeepSeek worker may capture evidence but must not judge screenshots. Use one relay task at a time. Never reuse a stale pointer or accept a task without a matching result record.
{{end}}
""";
        var current = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var expression = new Regex(Regex.Escape(start) + ".*?" + Regex.Escape(end), RegexOptions.Singleline);
        var updated = expression.IsMatch(current) ? expression.Replace(current, block.TrimEnd()) : (current.TrimEnd() + Environment.NewLine + Environment.NewLine + block.TrimEnd()).TrimStart();
        AtomicFile.WriteAllText(path, updated + Environment.NewLine);
    }

    private static void RemovePlanRelayGuidance(string path)
    {
        const string start = "<!-- CODEX-HELPER-DEEPSEEK-PLAN-RELAY-START -->";
        const string end = "<!-- CODEX-HELPER-DEEPSEEK-PLAN-RELAY-END -->";
        if (!File.Exists(path)) return;
        var current = File.ReadAllText(path);
        var updated = Regex.Replace(current, Regex.Escape(start) + ".*?" + Regex.Escape(end), string.Empty, RegexOptions.Singleline).Trim();
        if (updated.Length == 0) File.Delete(path); else AtomicFile.WriteAllText(path, updated + Environment.NewLine);
    }

    private static void RemovePlanWorkerProviderSections(List<string> lines)
    {
        var result = new List<string>();
        var skip = false;
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"^\s*\[([^\]]+)\]\s*$");
            if (match.Success) skip = match.Groups[1].Value.StartsWith("model_providers.deepseek_plan_worker", StringComparison.Ordinal);
            if (!skip) result.Add(line);
        }
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        lines.Clear(); lines.AddRange(result);
    }

    private IReadOnlyList<SwitchFileSnapshot> CapturePlanWorkerFiles(string agentPath, string guidancePath)
    {
        var files = new[] { configPath, indexPath, MetadataPath(), Path.Combine(codexRoot, "codex-helper-model-catalog.json"), agentPath, Path.Combine(codexRoot, "agents", "worker.toml"), guidancePath }
            .Concat(CredentialHelperDestinationFiles());
        return files.Select(path => new SwitchFileSnapshot(path, File.Exists(path), BackupIfExists(path, "deepseek-plan-worker-" + Path.GetFileNameWithoutExtension(path)))).ToList();
    }

    private IEnumerable<string> CredentialHelperDestinationFiles()
    {
        var directory = Path.GetDirectoryName(stableHelperPath)!;
        return new[]
        {
            "CodexHelperCredentialHelper.exe", "CodexHelperCredentialHelper.dll", "CodexHelperCredentialHelper.deps.json",
            "CodexHelperCredentialHelper.runtimeconfig.json", "CodexHelper.Core.dll",
            "Konscious.Security.Cryptography.Argon2.dll", "Konscious.Security.Cryptography.Blake2.dll",
            "System.Security.Cryptography.ProtectedData.dll"
        }.Select(name => Path.Combine(directory, name));
    }

    private void RemoveLegacySubagentResidue(List<string> lines)
    {
        RemoveResponsesSubagentSections(lines);
        RemoveManagedWorkerConfiguration(Path.Combine(codexRoot, "agents", "worker.toml"));
        RemoveDelegationGuidance(Path.Combine(codexRoot, "AGENTS.md"));
        var index = LoadIndex();
        var obsolete = index.Profiles.Where(item => item.Kind == ConnectionKind.ResponsesSubagent).ToList();
        foreach (var profile in obsolete)
        {
            var secret = SecretPath(profile.Id);
            _ = BackupIfExists(secret, "removed-legacy-subagent-secret");
            if (File.Exists(secret)) File.Delete(secret);
            index.Profiles.Remove(profile);
        }
        if (obsolete.Count > 0) SaveIndex(index);
    }

    private CatalogPlan PrepareCatalogPlan(List<string> lines, ProviderMetadata metadata, bool enableDeepSeek)
    {
        var helperCatalog = Path.Combine(codexRoot, "codex-helper-model-catalog.json");
        var configured = ReadTopLevel(lines, "model_catalog_json");
        if (!enableDeepSeek) return new CatalogPlan(false, helperCatalog, null, null);

        var continuingTakeover = metadata.HelperCatalogActive && IsSameCodexPath(configured, helperCatalog);
        if (metadata.HelperCatalogActive && !continuingTakeover)
            throw new InvalidOperationException("检测到模型目录接管状态与 config.toml 不一致。请先切回官方模式完成恢复，再重试。");

        string sourcePath;
        if (continuingTakeover)
        {
            sourcePath = helperCatalog;
        }
        else
        {
            metadata.OriginalModelCatalogJson = IsSameCodexPath(configured, helperCatalog) ? string.Empty : configured;
            sourcePath = ResolveCatalogSource(configured, helperCatalog);
        }

        var merged = BuildDeepSeekCatalog(sourcePath);
        return new CatalogPlan(true, helperCatalog, merged, sourcePath);
    }

    private void ApplyCatalogPlan(List<string> lines, ProviderMetadata metadata, CatalogPlan plan)
    {
        if (plan.EnableDeepSeek)
        {
            if (plan.Content is null || plan.SourcePath is null) throw new InvalidOperationException("DeepSeek 模型目录计划不完整。");
            if (!IsSameCodexPath(plan.SourcePath, plan.HelperCatalogPath))
                metadata.CatalogSourceBackup = BackupIfExists(plan.SourcePath, "model-catalog-source") ?? string.Empty;
            AtomicFile.WriteAllText(plan.HelperCatalogPath, plan.Content);
            SetTopLevel(lines, "model_catalog_json", plan.HelperCatalogPath);
            metadata.HelperCatalogActive = true;
            metadata.HelperCatalogMarker = "codex-helper-deepseek-v1";
            metadata.CatalogSourcePath = plan.SourcePath;
            return;
        }

        var configured = ReadTopLevel(lines, "model_catalog_json");
        if (metadata.HelperCatalogActive)
        {
            if (!IsSameCodexPath(configured, plan.HelperCatalogPath))
                throw new InvalidOperationException("模型目录已被外部修改，Helper 不会覆盖它。请确认 config.toml 后重试。");
            if (string.IsNullOrWhiteSpace(metadata.OriginalModelCatalogJson)) RemoveTopLevelValue(lines, "model_catalog_json");
            else SetTopLevel(lines, "model_catalog_json", metadata.OriginalModelCatalogJson);
        }
        else if (IsSameCodexPath(configured, plan.HelperCatalogPath))
        {
            RemoveTopLevelValue(lines, "model_catalog_json");
        }

        if ((metadata.HelperCatalogActive || IsSameCodexPath(configured, plan.HelperCatalogPath)) && File.Exists(plan.HelperCatalogPath))
            File.Delete(plan.HelperCatalogPath);
        metadata.HelperCatalogActive = false;
        metadata.HelperCatalogMarker = string.Empty;
        metadata.OriginalModelCatalogJson = string.Empty;
        metadata.CatalogSourcePath = string.Empty;
        metadata.CatalogSourceBackup = string.Empty;
    }

    private string ResolveCatalogSource(string configured, string helperCatalog)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = ResolveCodexPath(configured);
            if (!File.Exists(configuredPath)) throw new FileNotFoundException("config.toml 指向的模型目录不存在，已取消切换。", configuredPath);
            return configuredPath;
        }

        var candidates = new[]
        {
            helperCatalog,
            Path.Combine(codexRoot, "models.json"),
            Path.Combine(codexRoot, "models_cache.json"),
            Path.Combine(codexRoot, "cache", "models.json"),
            Path.Combine(codexRoot, "cache", "models_cache.json")
        };
        var source = candidates.FirstOrDefault(File.Exists);
        if (source is not null) return source;

        var legacyCache = Path.Combine(codexRoot, "account-switcher", "cache-backups");
        if (Directory.Exists(legacyCache))
        {
            source = Directory.EnumerateFiles(legacyCache, "models_cache*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (source is not null) return source;
        }
        throw new InvalidOperationException("未找到包含完整 Codex harness 字段的本机模型目录。请先用官方模型启动一次 Codex，再重试 DeepSeek 切换。");
    }

    private static string BuildDeepSeekCatalog(string sourcePath)
    {
        const long maximumCatalogBytes = 16L * 1024 * 1024;
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumCatalogBytes)
            throw new InvalidDataException("模型目录大小无效，已取消切换。");
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(sourcePath, Encoding.UTF8)) as JsonObject ?? throw new InvalidDataException(); }
        catch (JsonException ex) { throw new InvalidDataException("模型目录 JSON 已损坏，已取消切换。", ex); }
        var models = root["models"] as JsonArray ?? throw new InvalidDataException("模型目录缺少 models 数组，已取消切换。");
        var template = models.OfType<JsonObject>()
            .Where(model => model["slug"]?.GetValue<string>()?.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) == true)
            .FirstOrDefault(HasRequiredHarnessFields)
            ?? throw new InvalidDataException("模型目录中没有包含完整 Codex harness 字段的 GPT 模板，已取消切换。");

        var deepSeek = (JsonObject)template.DeepClone();
        deepSeek["slug"] = "deepseek-v4-flash";
        deepSeek["display_name"] = "DeepSeek V4 Flash";
        deepSeek["description"] = "DeepSeek 官方 Responses 模型（Codex Helper 临时目录）";
        deepSeek["default_reasoning_level"] = "high";
        deepSeek["supported_reasoning_levels"] = new JsonArray(
            new JsonObject { ["effort"] = "low", ["description"] = "更快响应，较少推理" },
            new JsonObject { ["effort"] = "medium", ["description"] = "均衡速度与推理深度" },
            new JsonObject { ["effort"] = "high", ["description"] = "适合日常编码与复杂任务" },
            new JsonObject { ["effort"] = "max", ["description"] = "最深推理，适合困难任务" });
        deepSeek["visibility"] = "list";
        deepSeek["supported_in_api"] = true;
        deepSeek["apply_patch_tool_type"] = "freeform";
        deepSeek["web_search_tool_type"] = "text";
        deepSeek["context_window"] = 1_048_576;
        deepSeek["max_context_window"] = 1_048_576;
        deepSeek["effective_context_window_percent"] = 95;
        deepSeek["auto_compact_token_limit"] = 900_000;
        deepSeek["input_modalities"] = new JsonArray(JsonValue.Create("text"));
        deepSeek["supports_image_detail_original"] = false;
        deepSeek["supports_search_tool"] = true;
        deepSeek["use_responses_lite"] = false;
        deepSeek["tool_mode"] = null;
        deepSeek.Remove("additional_speed_tiers");
        deepSeek.Remove("service_tiers");

        for (var index = models.Count - 1; index >= 0; index--)
            if (models[index] is JsonObject model && string.Equals(model["slug"]?.GetValue<string>(), "deepseek-v4-flash", StringComparison.OrdinalIgnoreCase))
                models.RemoveAt(index);
        models.Add(deepSeek);
        root["fetched_at"] = DateTime.UtcNow.ToString("O");
        root["codex_helper"] = new JsonObject
        {
            ["marker"] = "codex-helper-deepseek-v1",
            ["purpose"] = "temporary-merged-model-catalog"
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static bool HasRequiredHarnessFields(JsonObject model)
    {
        static bool HasString(JsonObject value, string name) => value[name] is JsonValue node && node.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text);
        return HasString(model, "base_instructions") && HasString(model, "shell_type") && HasString(model, "apply_patch_tool_type") &&
               HasString(model, "web_search_tool_type") && model["model_messages"] is JsonObject && model["truncation_policy"] is JsonObject &&
               model["supported_reasoning_levels"] is JsonArray;
    }

    private IReadOnlyList<SwitchFileSnapshot> CaptureSwitchFiles(string purpose)
    {
        var files = new[] { configPath, indexPath, MetadataPath(), Path.Combine(codexRoot, "codex-helper-model-catalog.json") }
            .Concat(CredentialHelperDestinationFiles());
        return files.Select(path => new SwitchFileSnapshot(path, File.Exists(path), BackupIfExists(path, purpose + "-" + Path.GetFileNameWithoutExtension(path)))).ToList();
    }

    private static void RestoreSnapshots(IEnumerable<SwitchFileSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots.Reverse())
        {
            if (!snapshot.Existed)
            {
                if (File.Exists(snapshot.Path)) File.Delete(snapshot.Path);
                continue;
            }
            if (snapshot.BackupPath is null) throw new InvalidOperationException("切换回滚备份缺失。");
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
            File.Copy(snapshot.BackupPath, snapshot.Path, overwrite: true);
        }
    }

    private string ResolveCodexPath(string value) => Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(codexRoot, value));
    private bool IsSameCodexPath(string left, string right) => !string.IsNullOrWhiteSpace(left) && string.Equals(ResolveCodexPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes obsolete third-party native worker configuration that receives encrypted, empty task payloads.</summary>
    public string CleanUnsupportedNativeSubagent(string profileId)
    {
        AssertSafeToWrite();
        var profile = RequireProvider(profileId);
        if (profile.Kind is not (ConnectionKind.ResponsesSubagent or ConnectionKind.CustomApi))
            throw new InvalidOperationException("请选择旧版 Responses 子智能体档案。");
        // Cleanup only updates local files and never sends a request.
        var lines = ReadValidatedConfig();
        var configBackup = CreateConfigBackup("pre-unsupported-native-subagent-cleanup");
        var agentConfigPath = Path.Combine(codexRoot, "agents", "worker.toml");
        var guidancePath = Path.Combine(codexRoot, "AGENTS.md");
        var catalogPath = Path.Combine(codexRoot, "codex-helper-model-catalog.json");
        var workerBackup = BackupIfExists(agentConfigPath, "worker");
        var guidanceBackup = BackupIfExists(guidancePath, "agents-guidance");
        var catalogBackup = BackupIfExists(catalogPath, "worker-catalog");
        var indexBackup = BackupIfExists(indexPath, "provider-index");

        try
        {
            var removedWorker = RemoveManagedWorkerConfiguration(agentConfigPath);
            var removedGuidance = RemoveDelegationGuidance(guidancePath);
            RemoveResponsesSubagentSections(lines);
            var removedCatalogReference = RemoveManagedCatalogReference(lines, catalogPath);
            WriteConfig(lines);
            if (removedCatalogReference && File.Exists(catalogPath)) File.Delete(catalogPath);

            var index = LoadIndex();
            foreach (var item in index.Profiles) item.IsDefaultSubagent = false;
            profile = index.Profiles.Single(item => item.Id == profileId);
            profile.Kind = ConnectionKind.CustomApi;
            profile.StatusMessage = "Native third-party subagent unavailable: Codex encrypts child task payloads.";
            profile.UpdatedUtc = DateTime.UtcNow;
            SaveIndex(index);

            var workerText = removedWorker ? "Helper-managed worker.toml removed" : "no Helper-managed worker.toml found";
            var guidanceText = removedGuidance ? "obsolete delegation guidance removed" : "no obsolete Helper guidance found";
            return $"Native third-party subagent was blocked and legacy configuration was cleaned. {workerText}; {guidanceText}; config backup: {configBackup}. Use the API profile only for connectivity tests until a dedicated local task relay is available.";
        }
        catch
        {
            RestoreBackup(configPath, configBackup);
            RestoreBackup(agentConfigPath, workerBackup);
            RestoreBackup(guidancePath, guidanceBackup);
            RestoreBackup(catalogPath, catalogBackup);
            RestoreBackup(indexPath, indexBackup);
            throw;
        }
    }

    private string? BackupIfExists(string path, string name)
    {
        if (!File.Exists(path)) return null;
        Directory.CreateDirectory(recoveryDirectory);
        var backup = Path.Combine(recoveryDirectory, $"{name}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.bak");
        File.Copy(path, backup, overwrite: false);
        return backup;
    }

    private static void RestoreBackup(string path, string? backup)
    {
        if (backup is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(backup, path, overwrite: true);
    }

    private static bool RemoveDelegationGuidance(string path)
    {
        const string start = "<!-- CODEX-HELPER-DELEGATION-START -->";
        const string end = "<!-- CODEX-HELPER-DELEGATION-END -->";
        if (!File.Exists(path)) return false;
        var current = File.ReadAllText(path);
        var expression = new Regex(Regex.Escape(start) + ".*?" + Regex.Escape(end), RegexOptions.Singleline);
        if (!expression.IsMatch(current)) return false;
        var updated = expression.Replace(current, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(updated)) File.Delete(path);
        else AtomicFile.WriteAllText(path, updated + Environment.NewLine);
        return true;
    }

    private static bool RemoveManagedWorkerConfiguration(string path)
    {
        if (!File.Exists(path)) return false;
        var content = File.ReadAllText(path);
        var managed = content.Contains("model_provider = \"responses_subagent\"", StringComparison.Ordinal) &&
                      content.Contains("Dedicated Responses coding worker", StringComparison.Ordinal);
        if (!managed) return false;
        File.Delete(path);
        return true;
    }

    private bool RemoveManagedCatalogReference(List<string> lines, string catalogPath)
    {
        var configured = ReadTopLevel(lines, "model_catalog_json");
        if (string.IsNullOrWhiteSpace(configured)) return false;
        if (!IsSameCodexPath(configured, catalogPath)) return false;
        var firstSection = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\["));
        if (firstSection < 0) firstSection = lines.Count;
        var expression = new Regex(@"^\s*model_catalog_json\s*=");
        for (var index = firstSection - 1; index >= 0; index--)
            if (expression.IsMatch(lines[index])) lines.RemoveAt(index);
        return true;
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

    private static void RemoveResponsesSubagentSections(List<string> lines)
    {
        var result = new List<string>();
        var skip = false;
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"^\s*\[([^\]]+)\]\s*$");
            if (match.Success)
            {
                var section = match.Groups[1].Value;
                skip = section is "model_providers.responses_subagent" or "agents.worker" ||
                       section.StartsWith("model_providers.responses_subagent.", StringComparison.Ordinal);
            }
            if (!skip) result.Add(line);
        }
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        lines.Clear();
        lines.AddRange(result);
    }

    private static void SetSectionValue(List<string> lines, string section, string key, string value)
    {
        var header = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\[" + Regex.Escape(section) + @"\]\s*$"));
        if (header < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add(string.Empty);
            lines.Add($"[{section}]");
            header = lines.Count - 1;
        }
        var end = lines.FindIndex(header + 1, line => Regex.IsMatch(line, @"^\s*\["));
        if (end < 0) end = lines.Count;
        var expression = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=");
        for (var index = header + 1; index < end; index++)
        {
            if (!expression.IsMatch(lines[index])) continue;
            lines[index] = key + " = " + value;
            return;
        }
        lines.Insert(end, key + " = " + value);
    }

    private static void SetTopLevelValue(List<string> lines, string key, string value)
    {
        var end = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\["));
        if (end < 0) end = lines.Count;
        var expression = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=");
        for (var index = 0; index < end; index++)
        {
            if (expression.IsMatch(lines[index])) { lines[index] = key + " = " + value; return; }
        }
        lines.Insert(end, key + " = " + value);
    }

    private static void RemoveTopLevelValue(List<string> lines, string key)
    {
        var end = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\["));
        if (end < 0) end = lines.Count;
        var expression = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=");
        for (var index = end - 1; index >= 0; index--)
            if (expression.IsMatch(lines[index])) lines.RemoveAt(index);
    }

    private static string ReadTopLevel(List<string> lines, string key)
    {
        var firstSection = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\["));
        if (firstSection < 0) firstSection = lines.Count;
        var regex = new Regex("^\\s*" + Regex.Escape(key) + "\\s*=\\s*[\"'](.*?)[\"']\\s*(?:#.*)?$");
        for (var index = 0; index < firstSection; index++)
        {
            var match = regex.Match(lines[index]);
            if (match.Success) return UnescapeToml(match.Groups[1].Value);
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
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Base URL 不能包含账号信息、查询参数或片段。");
        var hadResponsesSuffix = clean.EndsWith("/responses", StringComparison.OrdinalIgnoreCase);
        if (hadResponsesSuffix) clean = clean[..^"/responses".Length];
        if (!hadResponsesSuffix && new Uri(clean).AbsolutePath == "/") clean += "/v1";
        return clean;
    }

    private static bool IsApiProfile(ConnectionKind kind) => kind is ConnectionKind.CustomApi or ConnectionKind.Sub2Api or ConnectionKind.ResponsesSubagent;
    private static bool IsOfficialDeepSeek(string url, string model) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(model.Trim(), "deepseek-v4-flash", StringComparison.OrdinalIgnoreCase);

    // Official Codex configurations often omit model_provider entirely. Only an
    // explicit non-OpenAI provider means the main model has been switched away.
    private static bool UsesOfficialMainModel(List<string> lines)
    {
        var provider = ReadTopLevel(lines, "model_provider");
        return string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static void EnsureSafeRemoteEndpoint(ConnectionProfile profile)
    {
        if (profile.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLoopback(profile.BaseUrl))
            throw new InvalidOperationException("远程 HTTP 会明文传输 API Key；请改用 HTTPS。 ");
    }

    private static HttpClient CreateClient(string key)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexHelper/0.1");
        return client;
    }

    private static string EscapeToml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string UnescapeToml(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length && value[index + 1] is '\\' or '"') result.Append(value[++index]);
            else result.Append(value[index]);
        }
        return result.ToString();
    }
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
        public bool HelperCatalogActive { get; set; }
        public string HelperCatalogMarker { get; set; } = string.Empty;
        public string OriginalModelCatalogJson { get; set; } = string.Empty;
        public string CatalogSourcePath { get; set; } = string.Empty;
        public string CatalogSourceBackup { get; set; } = string.Empty;
        public bool PlanWorkerActive { get; set; }
        public string PlanWorkerProfileId { get; set; } = string.Empty;
    }

    private sealed record CatalogPlan(bool EnableDeepSeek, string HelperCatalogPath, string? Content, string? SourcePath);
    private sealed record SwitchFileSnapshot(string Path, bool Existed, string? BackupPath);
}
