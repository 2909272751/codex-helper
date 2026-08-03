namespace CodexHelper.Core.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string CodexRoot { get; set; } = CodexRootResolver.GetDefaultRoot();
    public string BackupRepositoryPath { get; set; } = string.Empty;
    public List<string> WorkspaceRoots { get; set; } = new();
    public List<string> ProtectedProjectPaths { get; set; } = new();
    public bool IncludeSessions { get; set; } = true;
    public bool IncludeAttachments { get; set; } = true;
    public bool IncludeGeneratedImages { get; set; }
    public bool CloseToTray { get; set; }
    public bool UseDarkTheme { get; set; }
    public bool HasCompletedOnboarding { get; set; }
    public string LastSelectedPage { get; set; } = "Dashboard";
    public string LastOfficialModel { get; set; } = string.Empty;
    public string ReasonixExecutionIntensity { get; set; } = "standard";
    /// <summary>DeepSeek 缓存统计时间范围（24h/7d/14d/30d/all）；非法值回退 14 天。</summary>
    public string DeepSeekCacheRange { get; set; } = "14d";
}

public static class CodexRootResolver
{
    public static string GetDefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }
}
