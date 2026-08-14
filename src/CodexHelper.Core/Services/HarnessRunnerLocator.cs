namespace CodexHelper.Core.Services;

/// <summary>
/// 定位 CodexHelper.HarnessRunner.exe：已安装目录（Helper 同目录 / 标准安装目录）与
/// 开发输出（从进程目录向上找仓库根 CodexHelper.sln 后的 bin 输出）。候选顺序与
/// invoke-harness.ps1 内 Find-HarnessRunner 保持一致；找不到返回 null，绝不虚报。
/// </summary>
public sealed class HarnessRunnerLocator
{
    public const string RunnerFileName = "CodexHelper.HarnessRunner.exe";

    /// <summary>Helper 进程目录（安装后 Runner 与 Helper 同目录；测试可注入临时目录）。</summary>
    public Func<string> BaseDirectoryProvider { get; init; } = () => AppContext.BaseDirectory;

    /// <summary>标准安装目录根（LocalAppData）。</summary>
    public Func<string> LocalAppDataProvider { get; init; } = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>文件存在性判定（测试可注入确定实现）。</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>向上搜索仓库根的最大目录层数。</summary>
    public int MaxRepositorySearchDepth { get; init; } = 8;

    public string? FindRunner()
    {
        var candidates = new List<string>();
        var baseDirectory = SafeGet(BaseDirectoryProvider);
        if (!string.IsNullOrWhiteSpace(baseDirectory))
            candidates.Add(Path.Combine(baseDirectory, RunnerFileName));

        var localAppData = SafeGet(LocalAppDataProvider);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "Programs", "Codex Helper", RunnerFileName));

        // 开发输出：从进程目录向上找仓库根（含 CodexHelper.sln），检查 Release/Debug 输出。
        var directory = baseDirectory;
        for (var i = 0; i < MaxRepositorySearchDepth && !string.IsNullOrWhiteSpace(directory); i++)
        {
            try
            {
                if (FileExists(Path.Combine(directory, "CodexHelper.sln")))
                {
                    foreach (var configuration in new[] { "Release", "Debug" })
                    {
                        candidates.Add(Path.Combine(directory, "src", "CodexHelper.HarnessRunner", "bin", configuration, "net8.0-windows", RunnerFileName));
                    }
                    break;
                }
            }
            catch { /* 单个目录不可枚举时继续向上 */ }
            var parent = Path.GetDirectoryName(directory);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, directory, StringComparison.Ordinal)) break;
            directory = parent;
        }

        foreach (var candidate in candidates)
        {
            try { if (FileExists(candidate)) return candidate; }
            catch { /* 忽略单个候选失败 */ }
        }
        return null;
    }

    private static string? SafeGet(Func<string> provider)
    {
        try { return provider(); }
        catch { return null; }
    }
}
