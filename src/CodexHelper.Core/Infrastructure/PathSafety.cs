namespace CodexHelper.Core.Infrastructure;

public static class PathSafety
{
    public static bool IsWithin(string candidate, string root)
    {
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string CombineWithin(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("导入条目不能使用绝对路径。");
        }

        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsWithin(destination, root))
        {
            throw new InvalidDataException($"导入路径越界：{relativePath}");
        }
        return destination;
    }

    public static void EnsureRepositoryOutsideSources(string repositoryPath, IEnumerable<string> sourcePaths)
    {
        var repository = Path.GetFullPath(repositoryPath);
        foreach (var sourcePath in sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var source = Path.GetFullPath(sourcePath);
            if (IsWithin(repository, source) || string.Equals(repository, source, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"备份仓库不能位于保护源内部：{source}");
            }
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}

