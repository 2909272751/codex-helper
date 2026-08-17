using System.Windows;
using CodexHelper.Core.Services;

namespace CodexHelper.App;

public partial class App : Application
{
    private System.Threading.Mutex? singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 隐藏宿主模式：不得创建 MainWindow、不得弹消息框。探测 127.0.0.1:3080，
        // 已健康则安静退出 0；否则无窗口启动绝对 node + dsh 并等待子进程（阻塞等待，
        // 无 UI 需要泵消息）。与主窗口单实例互斥无关，也不读取/写入任何 Helper 状态。
        var hiddenOptions = HarnessHiddenHostCli.TryParse(e.Args, out _);
        if (hiddenOptions is not null)
        {
            Shutdown(RunHiddenHost(hiddenOptions));
            return;
        }

        var smokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var mutexName = smokeTest ? $"Local\\CodexHelper.Smoke.{Environment.ProcessId}" : "Local\\CodexHelper.Main.0.1";
        singleInstanceMutex = new System.Threading.Mutex(true, mutexName, out var created);
        if (!created)
        {
            MessageBox.Show("Codex Helper 已经在运行。", "Codex Helper", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 隐藏宿主同步执行：探测 → 启动并等待子进程 → 返回退出码。任何异常按失败处理（安静，无窗口/无消息框）。
    /// 整个流程在 ThreadPool 上运行：内部 await（HTTP 探测、进程等待）不会捕获 WPF 派发器上下文，
    /// 避免 OnStartup 阻塞等待时续延排队到被阻塞的派发器造成死锁。
    /// </summary>
    private static int RunHiddenHost(HarnessHiddenHostCli.HiddenHostOptions options)
    {
        try
        {
            return Task.Run(() => DeepSeekHarnessHiddenHost.RunAsync(options.NodePath, options.DshEntryPath))
                .GetAwaiter().GetResult();
        }
        catch
        {
            return HarnessHiddenHostCli.ExitFailed;
        }
    }
}
