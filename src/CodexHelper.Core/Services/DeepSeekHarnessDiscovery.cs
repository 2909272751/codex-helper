namespace CodexHelper.Core.Services;

/// <summary>Node 可执行文件候选的来源分类。</summary>
public enum HarnessNodeSource
{
    /// <summary>用户显式选择并保存的路径。</summary>
    UserSelected,
    /// <summary>常见安装位置（%ProgramFiles%\nodejs 等）。</summary>
    CommonLocation,
    /// <summary>PATH 环境变量中的 node.exe。</summary>
    Path
}

/// <summary>单个 Node 候选：路径 + 来源 + 探测到的版本（探测前为空）。</summary>
public sealed record HarnessNodeCandidate(string Path, HarnessNodeSource Source, string Version = "");

/// <summary>dsh npm 包/JS 入口候选的来源分类。</summary>
public enum HarnessDshSource
{
    /// <summary>用户显式选择并保存的路径。</summary>
    UserSelected,
    /// <summary>常见 npm 全局根（%APPDATA%\npm、%LOCALAPPDATA%\npm、Program Files nodejs）等。</summary>
    CommonLocation,
    /// <summary>PATH 环境变量中的目录。</summary>
    Path
}

/// <summary>单个 dsh 候选：JS 入口（lib/bin.js）+ 包根 + 来源 + 探测到的版本（探测前为空）。</summary>
public sealed record HarnessDshCandidate(string EntryPath, string PackageRoot, HarnessDshSource Source, string Version = "");

/// <summary>
/// 多来源 Node 可执行文件发现。纯候选枚举（不做版本探测）。所有外部输入
/// （文件系统、PATH、特殊目录）都可注入，测试不依赖真实 Node 安装。
/// 不做全盘递归扫描：只查固定常见目录与 PATH 顶层。
/// </summary>
public sealed class DeepSeekHarnessDiscovery
{
    /// <summary>文件存在性检查（默认 File.Exists）。</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>特殊目录解析（默认 Environment.GetFolderPath）。</summary>
    public Func<Environment.SpecialFolder, string> SpecialFolder { get; init; } = Environment.GetFolderPath;

    /// <summary>PATH 目录（默认从环境变量解析）。</summary>
    public Func<IEnumerable<string>> PathDirectoryReader { get; init; } = ReadPathDirectories;

    /// <summary>
    /// 收集全部候选并去重（同路径只保留首个来源）。优先用户显式选择路径，
    /// 其次常见安装目录，最后 PATH。返回顺序即优先级顺序。
    /// </summary>
    public IReadOnlyList<HarnessNodeCandidate> Discover(string? userSelectedPath)
    {
        var candidates = new List<HarnessNodeCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, HarnessNodeSource source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = System.IO.Path.GetFullPath(path); }
            catch { return; }
            if (!string.Equals(System.IO.Path.GetFileName(path), "node.exe", StringComparison.OrdinalIgnoreCase)) return;
            if (!FileExists(path)) return;
            if (seen.Add(path)) candidates.Add(new HarnessNodeCandidate(path, source));
        }

        Add(userSelectedPath, HarnessNodeSource.UserSelected);

        var programFiles = SpecialFolder(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = SpecialFolder(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = SpecialFolder(Environment.SpecialFolder.LocalApplicationData);
        var appData = SpecialFolder(Environment.SpecialFolder.ApplicationData);
        foreach (var baseDir in new[]
        {
            System.IO.Path.Combine(programFiles, "nodejs"),
            System.IO.Path.Combine(programFilesX86, "nodejs"),
            System.IO.Path.Combine(localAppData, "Programs", "nodejs"),
            System.IO.Path.Combine(appData, "npm") // npm 全局目录旁，兼容 dsh 的 node
        })
            Add(System.IO.Path.Combine(baseDir, "node.exe"), HarnessNodeSource.CommonLocation);

        foreach (var directory in PathDirectoryReader())
            Add(System.IO.Path.Combine(directory, "node.exe"), HarnessNodeSource.Path);

        return candidates;
    }

    /// <summary>
    /// 收集全部 dsh npm 包/JS 入口候选并去重（同入口只保留首个来源）。优先用户显式路径，
    /// 其次常见 npm 全局根，最后 PATH。返回顺序即优先级顺序。
    /// 不做全盘递归扫描：只查固定常见全局根与 PATH 顶层目录下的 node_modules 固定子路径。
    /// </summary>
    public IReadOnlyList<HarnessDshCandidate> DiscoverDsh(string? userSelectedEntryPath)
    {
        var candidates = new List<HarnessDshCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? entry, HarnessDshSource source)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;
            try { entry = System.IO.Path.GetFullPath(entry); }
            catch { return; }
            if (!string.Equals(System.IO.Path.GetFileName(entry), "bin.js", StringComparison.OrdinalIgnoreCase)) return;
            if (!FileExists(entry)) return;
            if (seen.Add(entry)) candidates.Add(new HarnessDshCandidate(entry, ResolvePackageRoot(entry) ?? entry, source));
        }

        Add(userSelectedEntryPath, HarnessDshSource.UserSelected);

        var programFiles = SpecialFolder(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = SpecialFolder(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = SpecialFolder(Environment.SpecialFolder.LocalApplicationData);
        var appData = SpecialFolder(Environment.SpecialFolder.ApplicationData);
        foreach (var baseDir in new[]
        {
            // 用户全局 npm 根与 Program Files nodejs 的全局 node_modules。
            System.IO.Path.Combine(appData, "npm"),
            System.IO.Path.Combine(localAppData, "npm"),
            System.IO.Path.Combine(programFiles, "nodejs"),
            System.IO.Path.Combine(programFilesX86, "nodejs")
        })
            Add(CombineEntry(baseDir), HarnessDshSource.CommonLocation);

        foreach (var directory in PathDirectoryReader())
            Add(CombineEntry(directory), HarnessDshSource.Path);

        return candidates;
    }

    /// <summary>lib/bin.js 的包根（bin.js 的父目录的父目录）。</summary>
    private static string? ResolvePackageRoot(string entry)
    {
        var lib = System.IO.Path.GetDirectoryName(entry);
        return string.IsNullOrEmpty(lib) ? null : System.IO.Path.GetDirectoryName(lib);
    }

    private static string CombineEntry(string baseDir)
        => System.IO.Path.Combine(baseDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

    private static IEnumerable<string> ReadPathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = raw.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory)) continue;
            yield return directory;
        }
    }
}
