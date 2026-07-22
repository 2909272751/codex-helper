namespace CodexHelper.Core.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexHelper");
        SettingsPath = Path.Combine(BaseDirectory, "settings.json");
        VaultDirectory = Path.Combine(BaseDirectory, "vault");
        RecoveryDirectory = Path.Combine(BaseDirectory, "recovery");
        LogsDirectory = Path.Combine(BaseDirectory, "logs");
        TempDirectory = Path.Combine(BaseDirectory, "temp");
    }

    public string BaseDirectory { get; }
    public string SettingsPath { get; }
    public string VaultDirectory { get; }
    public string RecoveryDirectory { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(VaultDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}

