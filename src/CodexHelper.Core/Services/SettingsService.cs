using System.Text.Json;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

public sealed class SettingsService
{
    private readonly AppPaths paths;
    private readonly JsonStore store = new();

    public SettingsService(AppPaths paths)
    {
        this.paths = paths;
        paths.EnsureCreated();
    }

    public AppSettings Load()
    {
        var settings = store.LoadOrCreate(paths.SettingsPath, () => new AppSettings());
        // 仅当设置文件确实不含 CollaborationMode 字段（旧版升级前）时才迁移；
        // 反序列化会把缺失字段填为默认值 "Off"，不能据此判断新旧。
        if (!HasCollaborationModeField(paths.SettingsPath)) MigrateCollaborationMode(settings);
        return settings;
    }

    private bool HasCollaborationModeField(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("CollaborationMode", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// 执行器设置迁移（旧设置兼容）：旧版（无 CollaborationMode 字段）设备升级后，
    /// 若此前已启用 Reasonix 协作（reasonix-integration.json.Enabled == true）则保持
    /// Reasonix；否则保持关闭（Off）。首次判定后写回规范化值，后续不再重复迁移。
    /// 绝不因旧设置意外开启 Harness。
    /// </summary>
    private void MigrateCollaborationMode(AppSettings settings)
    {
        var wasReasonixEnabled = ReadLegacyReasonixEnabled();
        settings.CollaborationMode = wasReasonixEnabled
            ? CollaborationMode.Reasonix.ToPersisted()
            : CollaborationMode.Off.ToPersisted();
        Save(settings);
    }

    private bool ReadLegacyReasonixEnabled()
    {
        var statePath = Path.Combine(paths.BaseDirectory, "reasonix-integration.json");
        if (!File.Exists(statePath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            return document.RootElement.TryGetProperty("Enabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    public void Save(AppSettings settings)
    {
        settings.CodexRoot = Path.GetFullPath(settings.CodexRoot);
        settings.MaxConcurrency = Math.Clamp(settings.MaxConcurrency,
            ReasonixParallelScheduler.MinMaxConcurrency, ReasonixParallelScheduler.MaxMaxConcurrency);
        settings.WorkspaceRoots = NormalizeDistinct(settings.WorkspaceRoots);
        settings.ProtectedProjectPaths = NormalizeDistinct(settings.ProtectedProjectPaths);
        settings.CollaborationMode = CollaborationModeExtensions.ParseCollaborationMode(settings.CollaborationMode).ToPersisted();
        settings.HarnessExecutionMode = HarnessExecutionOptions.NormalizeMode(settings.HarnessExecutionMode);
        settings.HarnessPermissionMode = HarnessExecutionOptions.NormalizePermission(settings.HarnessPermissionMode);
        settings.HarnessExecutionStrength = HarnessExecutionOptions.NormalizeStrength(settings.HarnessExecutionStrength);
        if (!string.IsNullOrWhiteSpace(settings.HarnessNodePath))
            settings.HarnessNodePath = Path.GetFullPath(settings.HarnessNodePath);
        store.Save(paths.SettingsPath, settings);
    }

    private static List<string> NormalizeDistinct(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
