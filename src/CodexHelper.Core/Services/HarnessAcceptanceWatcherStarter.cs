using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 自动验收守候进程的紧邻安全记录（任务目录 HARNESS_ACCEPTANCE_WATCH.json）：
/// 只含调度事实（任务 ID、两绝对路径、守候 PID、守候启动 UTC、创建/更新 UTC、状态与脱敏说明），
/// 不含合同正文/凭据；PID + 启动 UTC 双重身份防 PID 复用。写入一律 UTF-8 无 BOM 原子写入。
/// </summary>
public sealed record HarnessAcceptanceWatchRecord(
    int SchemaVersion,
    string TaskId,
    string ProjectRoot,
    string TaskDirectory,
    int WatchPid,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime WatchStartedUtc,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime CreatedUtc,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime UpdatedUtc,
    string State,
    string? Message);

/// <summary>守候记录文件与状态常量宿主（宽容原子读写）。</summary>
public static class HarnessAcceptanceWatchStore
{
    public const string RecordFileName = "HARNESS_ACCEPTANCE_WATCH.json";
    public const int RecordSchemaVersion = 1;

    /// <summary>守候进程存活/应存活。</summary>
    public const string RunningState = "running";
    /// <summary>守候已正常结束（终局：已排队/接回/终态不排队）。</summary>
    public const string ExitedState = "exited";
    /// <summary>守候未能启动（失败可诊断，绝不假称启用）。</summary>
    public const string NotStartedState = "not-started";

    public static string RecordPath(string taskDirectory) => Path.Combine(taskDirectory, RecordFileName);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    public static HarnessAcceptanceWatchRecord? TryReadRecord(string taskDirectory)
    {
        var path = RecordPath(taskDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<HarnessAcceptanceWatchRecord>(text, Options);
        }
        catch { return null; }
    }

    public static void WriteRecord(HarnessAcceptanceWatchRecord record)
        => AtomicFile.WriteAllText(RecordPath(record.TaskDirectory), JsonSerializer.Serialize(record, Options));
}

/// <summary>守候拉起参数：只含两个绝对路径与模式（任务正文绝不进入命令行）。</summary>
public sealed record HarnessAcceptanceWatchSpawnArguments(string ProjectRoot, string TaskDirectory);

/// <summary>拉起结果：Started=新拉起；Reused=接回既有存活守候；否则为未启动（Message 说明原因，绝不假称启用）。</summary>
public sealed record HarnessAcceptanceWatchStartResult(
    bool Started,
    bool Reused,
    int? WatchPid,
    DateTime? WatchStartedUtc,
    string State,
    string Message);

/// <summary>
/// 自动验收守候拉起器（阶段二 A）：在受管 Runner 成功启动后自动、单飞地拉起一个独立
/// `-Mode automate` 守候子进程（默认与当前进程同路径的可执行文件；测试注入受控假进程）。
/// 只接收绝对项目根/任务目录，绝不承载合同正文/凭据。幂等：
/// 既有守候进程存活（PID+启动 UTC 身份相符）→ 接回不重复拉起；
/// 守候崩溃残留：若任务尚无验收队列记录则重新拉起一次（守候本身零副作用）；
/// 拉起失败 → 落盘 not-started 可诊断记录并如实返回（绝不假称已自动启用）。
/// </summary>
public sealed class HarnessAcceptanceWatcherStarter
{
    /// <summary>守候进程启动工厂（默认：当前进程同路径可执行文件 + -Mode automate 两绝对路径）。</summary>
    public Func<HarnessAcceptanceWatchSpawnArguments, IHarnessSupervisedProcess>? SpawnWatcher { get; init; }

    /// <summary>守候进程接回工厂（PID + 启动 UTC 双重身份；测试注入受控实现）。</summary>
    public Func<int, DateTime, IHarnessSupervisedProcess?>? AttachWatcher { get; init; }

    /// <summary>子进程启动身份允许的最大时间偏差（PID 重用防护）。</summary>
    private static readonly TimeSpan IdentityTolerance = TimeSpan.FromSeconds(2);

    public async Task<HarnessAcceptanceWatchStartResult> TryStartAsync(
        string projectRoot,
        string taskDirectory,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask; // 保留 async 语义；全部路径同步完成（无等待点），避免 CS1998。
        projectRoot = Path.GetFullPath(projectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        var runsRoot = Path.GetFullPath(Path.Combine(projectRoot, ".codex-helper", "runs"));
        if (!PathSafety.IsWithin(taskDirectory, runsRoot))
            throw new InvalidOperationException("自动验收守候只接受属于项目 .codex-helper/runs 的绝对任务目录。");
        var taskId = Path.GetFileName(taskDirectory);

        var existing = HarnessAcceptanceWatchStore.TryReadRecord(taskDirectory);
        if (existing is not null)
        {
            var alive = existing.State == HarnessAcceptanceWatchStore.RunningState
                ? Attach(existing.WatchPid, existing.WatchStartedUtc)
                : null;
            if (alive is not null)
            {
                if (alive is IDisposable disposable) { try { disposable.Dispose(); } catch { /* 忽略 */ } }
                return new HarnessAcceptanceWatchStartResult(false, true, existing.WatchPid, existing.WatchStartedUtc,
                    HarnessAcceptanceWatchStore.RunningState,
                    "已存在自动验收守候进程（PID " + existing.WatchPid + "），未重复拉起。");
            }
            // 守候崩溃/已退出残留：任务已有验收队列记录则无需再拉起（已排队/终局）。
            if (HarnessAcceptanceQueue.TryReadRecord(taskDirectory) is not null)
                return new HarnessAcceptanceWatchStartResult(false, false, null, null,
                    HarnessAcceptanceWatchStore.ExitedState,
                    "任务已有验收队列/终态记录且既有守候已退出，无需重复拉起自动验收守候。");
        }

        // 拉起独立守候子进程（默认 -Mode automate）；失败落盘 not-started 可诊断记录，绝不假称启用。
        try
        {
            var spawned = (SpawnWatcher ?? DefaultSpawnWatcher)(new HarnessAcceptanceWatchSpawnArguments(projectRoot, taskDirectory));
            var pid = spawned.Id;
            DateTime watchStartedUtc;
            try { watchStartedUtc = spawned.StartTimeUtc; }
            catch { watchStartedUtc = DateTime.UtcNow; }
            if (spawned is IDisposable disposed) disposed.Dispose(); // 守候子进程独立存活，句柄即弃
            var now = DateTime.UtcNow;
            var fresh = new HarnessAcceptanceWatchRecord(HarnessAcceptanceWatchStore.RecordSchemaVersion, taskId,
                projectRoot, taskDirectory, pid, watchStartedUtc, now, now, HarnessAcceptanceWatchStore.RunningState,
                "已自动拉起独立验收守候（-Mode automate），真实终态后自动排队并执行一次独立验收。");
            HarnessAcceptanceWatchStore.WriteRecord(fresh);
            return new HarnessAcceptanceWatchStartResult(true, false, pid, now, HarnessAcceptanceWatchStore.RunningState,
                "已自动拉起独立验收守候（PID " + pid + "）；守候期零 Codex 模型调用，仅真实 DSH 完成后的一次独立验收会消耗 Codex 用量。");
        }
        catch (Exception ex)
        {
            return WriteNotStarted(projectRoot, taskDirectory, taskId, "自动验收守候启动失败（不影响 DSH 本身）：" + HarnessAcceptanceQueue.Safe(ex.Message));
        }
    }

    /// <summary>守候启动失败的安全落盘与返回（not-started 可诊断；DSH 不受影响，绝不假称启用）。</summary>
    private static HarnessAcceptanceWatchStartResult WriteNotStarted(string projectRoot, string taskDirectory, string taskId, string message)
    {
        try
        {
            var existing = HarnessAcceptanceWatchStore.TryReadRecord(taskDirectory);
            var now = DateTime.UtcNow;
            var failed = existing is null
                ? new HarnessAcceptanceWatchRecord(HarnessAcceptanceWatchStore.RecordSchemaVersion, taskId,
                    projectRoot, taskDirectory, 0, now, now, now, HarnessAcceptanceWatchStore.NotStartedState, message)
                : existing with { State = HarnessAcceptanceWatchStore.NotStartedState, Message = message, UpdatedUtc = now };
            HarnessAcceptanceWatchStore.WriteRecord(failed);
        }
        catch { /* 落盘失败不阻断：结果仍如实返回未启动 */ }
        return new HarnessAcceptanceWatchStartResult(false, false, null, null,
            HarnessAcceptanceWatchStore.NotStartedState, message);
    }

    /// <summary>生产默认：启动与当前进程同路径的可执行文件的 -Mode automate（只传两绝对路径）。</summary>
    private static IHarnessSupervisedProcess DefaultSpawnWatcher(HarnessAcceptanceWatchSpawnArguments arguments)
    {
        var fileName = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            throw new InvalidOperationException("无法定位受管可执行文件（Environment.ProcessPath 缺失），无法拉起自动验收守候。");
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-Mode");
        startInfo.ArgumentList.Add(HarnessRunnerCli.ModeAutomate);
        startInfo.ArgumentList.Add("-ProjectRoot");
        startInfo.ArgumentList.Add(arguments.ProjectRoot);
        startInfo.ArgumentList.Add("-TaskDirectory");
        startInfo.ArgumentList.Add(arguments.TaskDirectory);
        var process = Process.Start(startInfo);
        if (process is null) throw new InvalidOperationException("自动验收守候子进程启动失败。");
        return new WatcherManagedProcess(process);
    }

    private IHarnessSupervisedProcess? Attach(int pid, DateTime startedUtc)
        => AttachWatcher is not null ? AttachWatcher(pid, startedUtc) : DefaultAttach(pid, startedUtc);

    private static IHarnessSupervisedProcess? DefaultAttach(int pid, DateTime recordStartedUtc)
    {
        if (pid <= 0) return null;
        Process? process = null;
        try { process = Process.GetProcessById(pid); }
        catch { return null; }
        DateTime osStartedUtc;
        try { osStartedUtc = process.StartTime.ToUniversalTime(); }
        catch { process.Dispose(); return null; }
        if ((osStartedUtc - recordStartedUtc).Duration() > IdentityTolerance)
        {
            process.Dispose();
            return null; // PID 被重用（启动时间不符）
        }
        if (process.HasExited)
        {
            process.Dispose();
            return null;
        }
        return new WatcherManagedProcess(process);
    }

    /// <summary>守候子进程的真实受控句柄（spawn 与 attach 共用）。</summary>
    private sealed class WatcherManagedProcess : IHarnessSupervisedProcess
    {
        private readonly Process process;

        internal WatcherManagedProcess(Process process)
        {
            this.process = process;
            try { StartTimeUtc = process.StartTime.ToUniversalTime(); }
            catch { StartTimeUtc = DateTime.UtcNow; }
        }

        public int Id => process.Id;
        public DateTime StartTimeUtc { get; }
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    }
}
