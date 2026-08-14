using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>DeepSeek Harness 任务的结构化状态（映射 task/session/workspace/URL）。</summary>
public sealed record HarnessTaskStatus(
    string TaskId,
    string ProjectRoot,
    string TaskDirectory,
    string State,
    string Message,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime StartedUtc,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime UpdatedUtc,
    int HostProcessId,
    string WebUrl,
    string? SessionId = null)
{
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "starting", StringComparison.OrdinalIgnoreCase);
}

/// <summary>ISO 8601 UTC 序列化器（标准 JSON）。</summary>
public sealed class HarnessUtcConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTimeOffset.Parse(reader.GetString() ?? string.Empty, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// Harness 任务中继 runner：接收绝对项目根目录和唯一任务目录，任务正文只从文件读取
/// （绝不放进命令行）；记录 task/session/workspace/URL 映射与结构化状态；支持停止。
/// 不通过点击 Web UI 自动化提交任务。当前预览版协议未确认时，受管 dsh 进程启动失败
/// 会诚实记录失败状态，绝不伪造成成功提交。
/// </summary>
public sealed class DeepSeekHarnessRunner
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    private readonly AppPaths paths;
    private readonly string taskRegistry;
    private readonly Dictionary<string, System.Diagnostics.Process> live = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();

    /// <summary>任务提交执行器（默认通过绝对 node.exe + dsh 入口；测试可注入，返回退出码）。</summary>
    public Func<string, string, string, int>? TaskRunner { get; init; }
    /// <summary>绝对 node.exe 路径（默认 TaskRunner 为空时使用；缺任一入口则诚实失败）。</summary>
    public string? NodeExecutable { get; init; }
    /// <summary>绝对 dsh JS 入口（lib/bin.js）路径。</summary>
    public string? DshEntryPath { get; init; }

    public DeepSeekHarnessRunner(AppPaths paths)
    {
        this.paths = paths;
        taskRegistry = Path.Combine(paths.BaseDirectory, "harness-tasks");
        paths.EnsureCreated();
        Directory.CreateDirectory(taskRegistry);
    }

    public string TaskDirectoryFor(string taskId) => Path.Combine(taskRegistry, taskId + ".json");

    /// <summary>
    /// 启动一个 Harness 任务。projectRoot 与 taskDirectory 必须为绝对路径，且 taskDirectory 位于
    /// projectRoot 内；任务正文从 taskDirectory/SPEC.md 读取（不读入命令行）。
    /// </summary>
    public async Task<HarnessTaskStatus> StartAsync(string projectRoot, string taskDirectory, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        if (!PathSafety.IsWithin(taskDirectory, projectRoot))
            throw new InvalidOperationException("任务目录必须位于项目根目录内。");
        var specPath = Path.Combine(taskDirectory, "SPEC.md");
        if (!File.Exists(specPath))
            throw new FileNotFoundException("任务目录缺少 SPEC.md，任务正文只从文件读取。", specPath);
        // 读取正文仅用于确认存在且可读，绝不出现在任何命令行参数中。
        _ = File.ReadAllText(specPath, System.Text.Encoding.UTF8);

        var taskId = Path.GetFileName(taskDirectory);
        var now = DateTime.UtcNow;
        var status = new HarnessTaskStatus(taskId, projectRoot, taskDirectory, "starting", "正在提交到 Harness Web Host 会话。", now, now, 0, DeepSeekHarnessVersions.WebHostDefaultUrl);
        Write(status);

        var started = await Task.Run(() => RunManagedTask(status, cancellationToken), cancellationToken);
        status = status with { State = started ? "completed" : "failed", UpdatedUtc = DateTime.UtcNow };
        if (started) status = status with { Message = "任务已在持久 Harness Web Host 会话中可见；关闭浏览器不会停止任务。" };
        else status = status with { Message = "Harness 预览版协议无法确认任务提交成功；请打开 Harness Web 人工确认或停止。" };
        Write(status);
        return status;
    }

    private bool RunManagedTask(HarnessTaskStatus initial, CancellationToken cancellationToken)
    {
        // 任务通过独立受管进程执行（复用 Web Host 会话，不通过点击 Web UI）。
        var exit = TaskRunner?.Invoke(initial.ProjectRoot, initial.TaskDirectory, initial.TaskId);
        if (exit is not null) return exit == 0;

        // 默认路径：绝对 node.exe + 绝对 dsh 入口，不依赖 PATH 或 dsh.cmd 自行找 node。
        if (string.IsNullOrWhiteSpace(NodeExecutable) || string.IsNullOrWhiteSpace(DshEntryPath)) return false;

        var start = new System.Diagnostics.ProcessStartInfo(NodeExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(DshEntryPath);
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(initial.TaskDirectory);
        try
        {
            var process = new System.Diagnostics.Process { StartInfo = start };
            if (!process.Start()) return false;
            lock (sync) live[initial.TaskId] = process;
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            try { process.WaitForExit(); } catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } }
            lock (sync) { if (live.TryGetValue(initial.TaskId, out var p) && ReferenceEquals(p, process)) live.Remove(initial.TaskId); }
            try { return process.ExitCode == 0; } catch { return false; }
        }
        catch { return false; }
    }

    /// <summary>停止运行中的 Harness 任务（终止进程树）。</summary>
    public void StopTask(string taskId)
    {
        lock (sync)
        {
            if (!live.TryGetValue(taskId, out var process)) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { process.Dispose(); } catch { }
            live.Remove(taskId);
        }
        if (TryRead(taskId) is { } status && status.IsRunning)
            Write(status with { State = "cancelled", Message = "用户已停止任务。", UpdatedUtc = DateTime.UtcNow });
    }

    /// <summary>宽容读取状态文件；缺失/损坏返回 null。</summary>
    public HarnessTaskStatus? TryRead(string taskId)
    {
        var path = TaskDirectoryFor(taskId);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<HarnessTaskStatus>(text, Options);
        }
        catch { return null; }
    }

    /// <summary>最近任务快照（按启动时间倒序）。</summary>
    public IReadOnlyList<HarnessTaskStatus> GetRecentTasks(int limit = 20)
    {
        var wanted = Math.Max(1, limit);
        return Directory.EnumerateFiles(taskRegistry, "*.json")
            .Select(path => { try { return JsonSerializer.Deserialize<HarnessTaskStatus>(File.ReadAllText(path, System.Text.Encoding.UTF8), Options); } catch { return null; } })
            .Where(status => status is not null)
            .Cast<HarnessTaskStatus>()
            .OrderByDescending(status => status.StartedUtc)
            .Take(wanted)
            .ToList();
    }

    private void Write(HarnessTaskStatus status)
        => AtomicFile.WriteAllText(TaskDirectoryFor(status.TaskId), JsonSerializer.Serialize(status, Options));
}
