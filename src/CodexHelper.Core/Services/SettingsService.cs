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

    public AppSettings Load() => store.LoadOrCreate(paths.SettingsPath, () => new AppSettings());

    public void Save(AppSettings settings)
    {
        settings.CodexRoot = Path.GetFullPath(settings.CodexRoot);
        settings.WorkspaceRoots = NormalizeDistinct(settings.WorkspaceRoots);
        settings.ProtectedProjectPaths = NormalizeDistinct(settings.ProtectedProjectPaths);
        store.Save(paths.SettingsPath, settings);
    }

    private static List<string> NormalizeDistinct(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

