using System.Diagnostics;
using Microsoft.Win32;

namespace CodexHelper.Core.Services;

/// <summary>Reasonix CLI 候选的来源分类（用于评分与 UI 展示）。</summary>
public enum ReasonixCliSource
{
    /// <summary>用户手动选择并保存的路径。</summary>
    Saved,
    /// <summary>Reasonix Windows 卸载注册表推导的安装目录。</summary>
    Registry,
    /// <summary>正在运行的 reasonix-desktop.exe / Reasonix.exe 所在目录。</summary>
    RunningProcess,
    /// <summary>常见安装位置（%LOCALAPPDATA%\Programs\Reasonix 等）。</summary>
    CommonLocation,
    /// <summary>PATH 环境变量中的 reasonix-cli.exe / reasonix.exe。</summary>
    Path,
    /// <summary>npm 全局 shim（%APPDATA%\npm\reasonix.cmd），最后兜底。</summary>
    Npm
}

/// <summary>单个 Reasonix CLI 候选：路径 + 来源。</summary>
public sealed record ReasonixCliCandidate(string Path, ReasonixCliSource Source);

/// <summary>
/// 多来源 Reasonix CLI 候选发现。纯候选枚举（不做能力探测），所有外部输入
/// （文件系统、注册表、进程、PATH、特殊目录）都可注入，测试不依赖真实注册表
/// 或真实 Reasonix 安装。不做全盘递归扫描：注册表只读卸载键值，versions 只下钻一层。
/// </summary>
public sealed class ReasonixCliDiscovery
{
    /// <summary>文件存在性检查（默认 File.Exists）。</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>特殊目录解析（默认 Environment.GetFolderPath）。</summary>
    public Func<Environment.SpecialFolder, string> SpecialFolder { get; init; } = Environment.GetFolderPath;

    /// <summary>注册表卸载项读取器，返回推导出的安装根/可执行文件路径（默认读真实注册表）。</summary>
    public Func<IEnumerable<string>>? RegistryReader { get; init; }

    /// <summary>运行中进程可执行文件路径（默认枚举 reasonix-desktop.exe / Reasonix.exe / reasonix-cli.exe）。</summary>
    public Func<IEnumerable<string>>? RunningProcessReader { get; init; }

    /// <summary>PATH 目录（默认从环境变量解析）。</summary>
    public Func<IEnumerable<string>>? PathDirectoryReader { get; init; }

    /// <summary>目录的一层子目录（默认 Directory.EnumerateDirectories，容错）。</summary>
    public Func<string, IEnumerable<string>>? SubdirectoryReader { get; init; }

    /// <summary>
    /// 收集全部候选并去重（不丢来源信息，同路径只保留首个来源）。
    /// savedPath 仍存在时作为 Saved 来源加入；不存在时返回的候选列表不包含它，
    /// 但可通过 <see cref="SavedPathMissing"/> 供诊断说明“已保存路径失效”。
    /// </summary>
    public IReadOnlyList<ReasonixCliCandidate> Discover(string? savedPath)
    {
        var candidates = new List<ReasonixCliCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, ReasonixCliSource source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(path); }
            catch { return; }
            if (!FileExists(path)) return;
            if (seen.Add(path)) candidates.Add(new ReasonixCliCandidate(path, source));
        }

        // 1) 已保存且仍存在的用户选择（npm shim 路径仍归入 Npm 兜底来源，避免掩盖 Desktop）。
        var npmPath = Path.Combine(SpecialFolder(Environment.SpecialFolder.ApplicationData), "npm", "reasonix.cmd");
        if (!string.IsNullOrWhiteSpace(savedPath)
            && string.Equals(PathUtil.GetFullPathSafe(savedPath), PathUtil.GetFullPathSafe(npmPath), StringComparison.OrdinalIgnoreCase))
            Add(savedPath, ReasonixCliSource.Npm);
        else
            Add(savedPath, ReasonixCliSource.Saved);

        // 2) 注册表卸载信息推导的安装目录（HKCU/HKLM、32/64 位视图）。
        foreach (var installDir in (RegistryReader ?? ReadRegistryInstallDirs)())
        {
            foreach (var derived in DeriveCliPaths(installDir)) Add(derived, ReasonixCliSource.Registry);
            Add(Path.Combine(installDir, "reasonix-cli.exe"), ReasonixCliSource.Registry);
            Add(Path.Combine(installDir, "reasonix.exe"), ReasonixCliSource.Registry);
        }

        // 3) 正在运行的 reasonix-desktop.exe / Reasonix.exe 所在安装根或版本目录。
        foreach (var executable in (RunningProcessReader ?? ReadRunningProcessExecutables)())
        {
            Add(executable, ReasonixCliSource.RunningProcess);
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(executable));
                if (string.IsNullOrWhiteSpace(directory)) continue;
                Add(Path.Combine(directory, "reasonix-cli.exe"), ReasonixCliSource.RunningProcess);
                Add(Path.Combine(directory, "reasonix.exe"), ReasonixCliSource.RunningProcess);
                // 进程位于 versions\vX.Y.Z 时，安装根在上两级。
                var versionsParent = Path.GetDirectoryName(directory);
                if (!string.IsNullOrWhiteSpace(versionsParent) && string.Equals(Path.GetFileName(versionsParent), "versions", StringComparison.OrdinalIgnoreCase))
                    Add(Path.Combine(versionsParent, "reasonix-cli.exe"), ReasonixCliSource.RunningProcess);
            }
            catch { /* 路径非法时跳过该进程候选 */ }
        }

        // 4) 常见安装位置。
        var localAppData = SpecialFolder(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = SpecialFolder(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = SpecialFolder(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var baseDir in new[] { Path.Combine(localAppData, "Programs", "Reasonix"), Path.Combine(localAppData, "reasonix"), Path.Combine(programFiles, "Reasonix"), Path.Combine(programFilesX86, "Reasonix") })
        {
            Add(Path.Combine(baseDir, "reasonix-cli.exe"), ReasonixCliSource.CommonLocation);
            Add(Path.Combine(baseDir, "reasonix.exe"), ReasonixCliSource.CommonLocation);
        }

        // 5) PATH 中的 reasonix-cli.exe / reasonix.exe。
        foreach (var directory in (PathDirectoryReader ?? ReadPathDirectories)())
        {
            Add(Path.Combine(directory, "reasonix-cli.exe"), ReasonixCliSource.Path);
            Add(Path.Combine(directory, "reasonix.exe"), ReasonixCliSource.Path);
        }

        // 6) npm shim 最后兜底。
        Add(npmPath, ReasonixCliSource.Npm);

        return candidates;
    }

    /// <summary>
    /// 从注册表安装根/可执行路径派生 CLI 候选。支持任意自定义磁盘目录（如 D:\Apps\Reasonix）、
    /// versions\vX.Y.Z 版本目录（只下钻一层，不做全盘递归）、DisplayIcon/UninstallString。
    /// </summary>
    private IEnumerable<string> DeriveCliPaths(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) yield break;
        // 入参可能是裸路径（InstallLocation）、引号包裹、带 ,0 索引的图标路径或卸载命令。
        var root = ExtractExecutablePath(installRoot) ?? installRoot.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(root)) yield break;
        var directory = Path.GetExtension(root).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(root)
            : root;
        if (string.IsNullOrWhiteSpace(directory)) yield break;
        yield return Path.Combine(directory, "reasonix-cli.exe");
        var versions = Path.Combine(directory, "versions");
        foreach (var sub in (SubdirectoryReader ?? ReadSubdirectories)(versions))
            yield return Path.Combine(sub, "reasonix-cli.exe");
    }

    private static string? ExtractExecutablePath(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('"') && text.EndsWith('"')) text = text.Trim('"');
        var comma = text.IndexOf(',');
        if (comma > 0) text = text[..comma].Trim();
        return Path.GetExtension(text).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? text : null;
    }

    /// <summary>真实注册表读取：HKCU/HKLM × 64/32 视图，卸载键 DisplayName 含 Reasonix 时读取
    /// InstallLocation/DisplayIcon/UninstallString。任何键值格式异常都跳过，绝不崩溃。</summary>
    private static IEnumerable<string> ReadRegistryInstallDirs()
    {
        var values = new List<string>();
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = uninstall.OpenSubKey(name);
                        var displayName = subKey?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName) || !displayName.Contains("Reasonix", StringComparison.OrdinalIgnoreCase)) continue;
                        if (subKey?.GetValue("InstallLocation") is string installLocation && !string.IsNullOrWhiteSpace(installLocation)) values.Add(installLocation);
                        if (subKey?.GetValue("DisplayIcon") is string displayIcon && !string.IsNullOrWhiteSpace(displayIcon)) values.Add(displayIcon);
                        if (subKey?.GetValue("UninstallString") is string uninstallString && !string.IsNullOrWhiteSpace(uninstallString)) values.Add(uninstallString);
                    }
                    catch { /* 单个损坏键跳过 */ }
                }
            }
            catch { /* 视图/子树不可读时跳过 */ }
        }
        return values;
    }

    /// <summary>枚举运行中进程，无需管理员权限；MainModule 读取失败（权限/已退出）时跳过该进程。</summary>
    private static IEnumerable<string> ReadRunningProcessExecutables()
    {
        var results = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!name.StartsWith("reasonix", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "Reasonix", StringComparison.OrdinalIgnoreCase)) continue;
                var mainModule = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(mainModule)) results.Add(mainModule);
            }
            catch { /* 无权限访问 MainModule 的进程跳过 */ }
            finally { try { process.Dispose(); } catch { } }
        }
        return results;
    }

    private static IEnumerable<string> ReadPathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = raw.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory)) continue;
            yield return directory;
        }
    }

    private static IEnumerable<string> ReadSubdirectories(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        try { return Directory.EnumerateDirectories(directory).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}

internal static class PathUtil
{
    /// <summary>安全 GetFullPath：非法路径返回原值（不抛异常），供比较/诊断使用。</summary>
    public static string GetFullPathSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
