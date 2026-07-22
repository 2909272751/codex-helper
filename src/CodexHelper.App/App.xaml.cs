using System.Windows;

namespace CodexHelper.App;

public partial class App : Application
{
    private System.Threading.Mutex? singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstanceMutex = new System.Threading.Mutex(true, "Local\\CodexHelper.Main.0.1", out var created);
        if (!created)
        {
            MessageBox.Show("Codex Helper 已经在运行。", "Codex Helper", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

