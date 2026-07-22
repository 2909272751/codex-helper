using System.Diagnostics;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

public sealed class CodexProcessService
{
    public IReadOnlyList<CodexProcessInfo> GetRunningProcesses()
    {
        var current = Environment.ProcessId;
        var result = new List<CodexProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != current && IsCodexProcessName(process.ProcessName) && !process.HasExited)
                        result.Add(new CodexProcessInfo(process.Id, process.ProcessName));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id).ToList();
    }

    public async Task StopAllAsync(bool forceAfterGracePeriod, CancellationToken cancellationToken = default)
    {
        foreach (var info in GetRunningProcesses())
        {
            using var process = TryOpen(info.Id);
            if (process is null) continue;
            try { process.CloseMainWindow(); } catch { }
        }

        var remaining = await WaitForExitAsync(TimeSpan.FromSeconds(4), cancellationToken);
        if (remaining.Count > 0 && forceAfterGracePeriod)
        {
            foreach (var info in remaining)
            {
                using var process = TryOpen(info.Id);
                if (process is null) continue;
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            remaining = await WaitForExitAsync(TimeSpan.FromSeconds(6), cancellationToken);
        }

        if (remaining.Count > 0)
            throw new InvalidOperationException("以下 Codex 进程无法结束：" + string.Join("、", remaining.Select(item => $"{item.Name}.exe (PID {item.Id})")));
    }

    public static bool IsCodexProcessName(string name) =>
        string.Equals(name, "Codex", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "codex-cli", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<CodexProcessInfo>> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        IReadOnlyList<CodexProcessInfo> running;
        do
        {
            running = GetRunningProcesses();
            if (running.Count == 0) return running;
            await Task.Delay(100, cancellationToken);
        } while (started.Elapsed < timeout);
        return running;
    }

    private static Process? TryOpen(int id)
    {
        try
        {
            var process = Process.GetProcessById(id);
            if (!process.HasExited && IsCodexProcessName(process.ProcessName)) return process;
            process.Dispose();
        }
        catch { }
        return null;
    }
}

