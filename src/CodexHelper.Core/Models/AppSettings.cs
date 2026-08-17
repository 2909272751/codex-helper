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
    /// <summary>智能拆分默认开。</summary>
    public bool AutoSplitEnabled { get; set; } = true;
    /// <summary>独立任务并行默认开。</summary>
    public bool ParallelIndependentEnabled { get; set; } = true;
    /// <summary>最大并发（1..3），默认 2；越界在保存时收敛。</summary>
    public int MaxConcurrency { get; set; } = 2;
    /// <summary>自动 worktree 默认开。</summary>
    public bool AutoWorktreeEnabled { get; set; } = true;
    /// <summary>超预算收敛默认开。</summary>
    public bool ConvergeOnBudgetOverrunEnabled { get; set; } = true;
    /// <summary>
    /// 协作执行器选择：Off / Reasonix / Harness。空值或旧设置由 <see cref="Services.SettingsService.Load"/>
    /// 迁移：旧版已启用 Reasonix 的设备保持 Reasonix，否则保持 Off。
    /// </summary>
    public string CollaborationMode { get; set; } = "Off";
    /// <summary>用户显式选择的 Node 可执行文件路径（Harness 模式；空表示用自动发现）。</summary>
    public string HarnessNodePath { get; set; } = string.Empty;
    /// <summary>用户显式选择的 dsh JS 入口（lib/bin.js）路径（Harness 模式；空表示用自动发现）。</summary>
    public string HarnessDshEntryPath { get; set; } = string.Empty;
    /// <summary>Harness 执行模式：codex-contract / standard / minimal / plan。</summary>
    public string HarnessExecutionMode { get; set; } = "codex-contract";
    /// <summary>Harness 权限模式。用户要求推荐设置默认启用完全控制。</summary>
    public string HarnessPermissionMode { get; set; } = "danger-full-access";
    /// <summary>Helper 合同执行强度：quick / standard / deep；不是模型 reasoning_effort。</summary>
    public string HarnessExecutionStrength { get; set; } = "standard";
    public bool HarnessReuseSession { get; set; } = true;
    public bool HarnessAutoStartHost { get; set; } = true;
    public bool HarnessReturnToGptOnFailure { get; set; } = true;
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
