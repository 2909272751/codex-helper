using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

public sealed class ProjectDiscovery
{
    private static readonly string[] ProjectMarkers =
    {
        ".git", "*.sln", "*.slnx", "*.csproj", "package.json", "Cargo.toml", "pyproject.toml",
        "build.gradle", "settings.gradle", "pom.xml", "CMakeLists.txt", "go.mod"
    };

    public Task<IReadOnlyList<ProjectInfo>> DiscoverAsync(
        IEnumerable<string> workspaceRoots,
        IEnumerable<string> protectedPaths,
        CancellationToken cancellationToken = default) => Task.Run(
            () => Discover(workspaceRoots, protectedPaths, cancellationToken), cancellationToken);

    private static IReadOnlyList<ProjectInfo> Discover(
        IEnumerable<string> workspaceRoots,
        IEnumerable<string> protectedPaths,
        CancellationToken cancellationToken)
    {
        var protectedSet = protectedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootValue in workspaceRoots.Where(Directory.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(rootValue);
            if (LooksLikeProject(root)) candidates.Add(root);

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (LooksLikeProject(directory)) candidates.Add(Path.GetFullPath(directory));
                }
            }
            catch { }
        }

        return candidates.Select(path => new ProjectInfo(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                path,
                Directory.Exists(Path.Combine(path, ".git")),
                File.Exists(Path.Combine(path, "AGENTS.md")),
                Directory.Exists(Path.Combine(path, ".codex")),
                SafeLastWrite(path),
                protectedSet.Contains(path)))
            .OrderByDescending(project => project.IsProtected)
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeProject(string path)
    {
        foreach (var marker in ProjectMarkers)
        {
            if (marker.Contains('*'))
            {
                try { if (Directory.EnumerateFiles(path, marker, SearchOption.TopDirectoryOnly).Any()) return true; } catch { }
            }
            else if (Directory.Exists(Path.Combine(path, marker)) || File.Exists(Path.Combine(path, marker)))
            {
                return true;
            }
        }
        return false;
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}

