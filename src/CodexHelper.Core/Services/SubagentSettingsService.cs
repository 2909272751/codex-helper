using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

public sealed record SubagentSettingsState(bool Enabled, bool HasLegacyResidue, bool HasPlanWorker)
{
    public string DisplayText => HasPlanWorker ? "DeepSeek 开发协作已配置；请使用连接中心的“关闭 DeepSeek 开发协作”，不要在这里直接关闭原生子智能体。" :
        Enabled ? "已开启，会增加当前主模型用量。Codex 可按需派发子智能体，但不会强制派发。" :
        HasLegacyResidue ? "已关闭（推荐），但检测到旧版 Helper 子智能体残留；点击“清理旧配置”可安全移除。" : "已关闭（推荐）。Codex 不会派发子智能体。";
}

public sealed class SubagentSettingsService
{
    private const string MarkerStart = "<!-- CODEX-HELPER-DELEGATION-START -->";
    private const string MarkerEnd = "<!-- CODEX-HELPER-DELEGATION-END -->";
    private readonly string codexRoot;
    private readonly string configPath;
    private readonly string recovery;
    private readonly string connectionsPath;
    private readonly string providerMetadataPath;
    private readonly JsonStore json = new();

    public SubagentSettingsService(string codexRoot, AppPaths paths)
    {
        this.codexRoot = Path.GetFullPath(codexRoot);
        configPath = Path.Combine(this.codexRoot, "config.toml");
        recovery = Path.Combine(paths.RecoveryDirectory, "subagent-settings");
        connectionsPath = Path.Combine(paths.VaultDirectory, "connections.json");
        providerMetadataPath = Path.Combine(paths.VaultDirectory, "providers", "metadata.json");
    }

    public SubagentSettingsState ReadState()
    {
        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath) : [];
        var enabled = ReadSectionValue(lines, "agents", "enabled")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var planWorker = Path.Combine(codexRoot, "agents", "deepseek_plan_worker.toml");
        return new(enabled, HasLegacy(lines), File.Exists(planWorker) && File.ReadAllText(planWorker).Contains("CODEX-HELPER-DEEPSEEK-PLAN-WORKER", StringComparison.Ordinal));
    }

    public void SetEnabled(bool enabled)
    {
        if (!File.Exists(configPath)) throw new FileNotFoundException("Codex config.toml 不存在。", configPath);
        Directory.CreateDirectory(recovery);
        var targets = new[]
        {
            configPath, Path.Combine(codexRoot, "AGENTS.md"), Path.Combine(codexRoot, "agents", "worker.toml"),
            Path.Combine(codexRoot, "codex-helper-model-catalog.json"), connectionsPath
        };
        var backups = targets.ToDictionary(path => path, Backup, StringComparer.OrdinalIgnoreCase);
        try
        {
            var lines = File.ReadAllLines(configPath, Encoding.UTF8).ToList();
            _ = TomlConfigurationDocument.Parse(lines);
            SetSectionValue(lines, "agents", "enabled", enabled ? "true" : "false");
            if (!enabled)
            {
                var legacyProvider = HasLegacyProvider(lines);
                RemoveTopLevel(lines, "default_subagent_model");
                var catalog = ReadTopLevel(lines, "model_catalog_json");
                var managedCatalog = Path.Combine(codexRoot, "codex-helper-model-catalog.json");
                var catalogIsReferenced = IsSameCodexPath(catalog, managedCatalog);
                var catalogMetadata = ReadHelperCatalogMetadata();
                var markedCatalog = IsMarkedHelperCatalog(managedCatalog);
                var activeMainCatalog = catalogIsReferenced && markedCatalog && catalogMetadata.Active && catalogMetadata.Owned;
                var removeCatalog = !activeMainCatalog && (markedCatalog || (catalogIsReferenced && (legacyProvider || catalogMetadata.Owned)));
                if (catalogIsReferenced && removeCatalog) RemoveTopLevel(lines, "model_catalog_json");
                RemoveLegacyProvider(lines);
                RemoveManagedWorker(Path.Combine(codexRoot, "agents", "worker.toml"));
                if (removeCatalog && File.Exists(managedCatalog)) File.Delete(managedCatalog);
                RemoveGuidance(Path.Combine(codexRoot, "AGENTS.md"));
                ClearLegacyFlags();
            }
            _ = TomlConfigurationDocument.Parse(lines);
            AtomicFile.WriteAllText(configPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch
        {
            foreach (var pair in backups) Restore(pair.Key, pair.Value);
            throw;
        }
    }

    private bool HasLegacy(IReadOnlyList<string> lines)
    {
        var worker = Path.Combine(codexRoot, "agents", "worker.toml");
        var agents = Path.Combine(codexRoot, "AGENTS.md");
        var catalog = Path.Combine(codexRoot, "codex-helper-model-catalog.json");
        var configuredCatalog = ReadTopLevel(lines, "model_catalog_json");
        var metadata = ReadHelperCatalogMetadata();
        var staleMarkedCatalog = IsMarkedHelperCatalog(catalog) && !(IsSameCodexPath(configuredCatalog, catalog) && metadata.Active && metadata.Owned);
        return lines.Any(line => line.Contains("model_providers.responses_subagent", StringComparison.Ordinal) || Regex.IsMatch(line, @"^\s*default_subagent_model\s*=")) ||
               staleMarkedCatalog ||
               IsManagedWorker(worker) || (File.Exists(agents) && File.ReadAllText(agents).Contains(MarkerStart, StringComparison.Ordinal));
    }

    private static bool HasLegacyProvider(IEnumerable<string> lines) => lines.Any(line =>
        Regex.IsMatch(line, @"^\s*\[model_providers\.responses_subagent(?:\.|\])"));

    private HelperCatalogMetadata ReadHelperCatalogMetadata()
    {
        if (!File.Exists(providerMetadataPath)) return new(false, false);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(providerMetadataPath));
            var active = document.RootElement.TryGetProperty("helperCatalogActive", out var activeValue) && activeValue.ValueKind == JsonValueKind.True;
            var owned = document.RootElement.TryGetProperty("helperCatalogMarker", out var marker) && marker.GetString()?.StartsWith("codex-helper-", StringComparison.Ordinal) == true;
            return new(active, owned);
        }
        catch (JsonException) { return new(false, false); }
    }

    private static bool IsMarkedHelperCatalog(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("codex_helper", out var helper) &&
                   helper.TryGetProperty("marker", out var marker) &&
                   string.Equals(marker.GetString(), "codex-helper-deepseek-v1", StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private void ClearLegacyFlags()
    {
        if (!File.Exists(connectionsPath)) return;
        var index = json.LoadOrCreate(connectionsPath, () => new ConnectionIndex());
        foreach (var profile in index.Profiles) profile.IsDefaultSubagent = false;
        json.Save(connectionsPath, index);
    }

    private static bool IsManagedWorker(string path)
    {
        if (!File.Exists(path)) return false;
        var content = File.ReadAllText(path);
        return content.Contains("model_provider = \"responses_subagent\"", StringComparison.Ordinal) &&
               content.Contains("Dedicated Responses coding worker", StringComparison.Ordinal);
    }
    private static void RemoveManagedWorker(string path) { if (IsManagedWorker(path)) File.Delete(path); }
    private static void RemoveGuidance(string path)
    {
        if (!File.Exists(path)) return;
        var value = File.ReadAllText(path);
        var updated = Regex.Replace(value, Regex.Escape(MarkerStart) + ".*?" + Regex.Escape(MarkerEnd), string.Empty, RegexOptions.Singleline).Trim();
        if (updated == value.Trim()) return;
        if (updated.Length == 0) File.Delete(path); else AtomicFile.WriteAllText(path, updated + Environment.NewLine);
    }
    private static void RemoveLegacyProvider(List<string> lines)
    {
        var result = new List<string>(); var skip = false;
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"^\s*\[([^]]+)\]\s*$");
            if (match.Success) skip = match.Groups[1].Value.StartsWith("model_providers.responses_subagent", StringComparison.Ordinal);
            if (!skip) result.Add(line);
        }
        lines.Clear(); lines.AddRange(result);
    }
    private static void SetSectionValue(List<string> lines, string section, string key, string value)
    {
        var header = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\[" + Regex.Escape(section) + @"\]\s*$"));
        if (header < 0) { lines.Add(string.Empty); lines.Add($"[{section}]"); header = lines.Count - 1; }
        var end = lines.FindIndex(header + 1, line => Regex.IsMatch(line, @"^\s*\[")); if (end < 0) end = lines.Count;
        var found = lines.FindIndex(header + 1, end - header - 1, line => Regex.IsMatch(line, @"^\s*" + Regex.Escape(key) + @"\s*="));
        if (found >= 0)
        {
            var comment = Regex.Match(lines[found], @"\s+(#.*)$").Groups[1].Value;
            lines[found] = $"{key} = {value}" + (comment.Length == 0 ? string.Empty : " " + comment);
        }
        else lines.Insert(end, $"{key} = {value}");
    }
    private static string? ReadSectionValue(IReadOnlyList<string> lines, string section, string key)
    {
        var inside = false;
        foreach (var line in lines) { var h = Regex.Match(line, @"^\s*\[([^]]+)\]"); if (h.Success) inside = h.Groups[1].Value == section; else if (inside) { var m = Regex.Match(line, @"^\s*" + key + @"\s*=\s*(\S+)"); if (m.Success) return m.Groups[1].Value.Trim('"'); } }
        return null;
    }
    private static string ReadTopLevel(IReadOnlyList<string> lines, string key) { foreach (var line in lines) { if (Regex.IsMatch(line, @"^\s*\[")) break; var m=Regex.Match(line,"^\\s*"+key+"\\s*=\\s*[\\\"'](.*?)[\\\"']"); if(m.Success)return m.Groups[1].Value;} return string.Empty; }
    private static void RemoveTopLevel(List<string> lines, string key) { var section=lines.FindIndex(line=>Regex.IsMatch(line,@"^\s*\[")); if(section<0)section=lines.Count; for(var i=section-1;i>=0;i--)if(Regex.IsMatch(lines[i],@"^\s*"+key+@"\s*="))lines.RemoveAt(i); }
    private bool IsSameCodexPath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        var resolved = Path.IsPathRooted(left) ? left : Path.Combine(codexRoot, left);
        return string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
    private string? Backup(string path){if(!File.Exists(path))return null;var p=Path.Combine(recovery,$"{Path.GetFileName(path)}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.bak");File.Copy(path,p);return p;}
    private static void Restore(string path,string? backup){if(backup is null)return;Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.Copy(backup,path,true);}
    private sealed record HelperCatalogMetadata(bool Active, bool Owned);
}
