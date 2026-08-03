using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

public sealed record ReasonixStatus(
    bool Installed,
    string ExecutablePath,
    string Version,
    string DefaultModel,
    bool CredentialReady,
    string CredentialMessage,
    bool IntegrationEnabled);

public sealed record ReasonixModelOption(string Id, string Provider, string Model);

public enum ReasonixPermissionMode { Safe, Full }

public sealed record ReasonixTaskStatus(
    string TaskId,
    string ProjectRoot,
    string TaskDirectory,
    string State,
    string Phase,
    string PermissionMode,
    [property: JsonConverter(typeof(ReasonixDateTimeConverter))] DateTime StartedUtc,
    [property: JsonConverter(typeof(ReasonixDateTimeConverter))] DateTime UpdatedUtc,
    int HostProcessId,
    long EventCount,
    string Message,
    string? CodexThreadId = null,
    string? ReasonixSessionPath = null,
    string? ReturnUri = null,
    string? ReturnState = null,
    string? ExecutionIntensity = null,
    string? ExecutionProfile = null,
    string? ExecutionEffort = null,
    string? ExecutionModel = null,
    int? EstimatedSteps = null,
    long ModelTurnCount = 0,
    long StepCount = 0,
    long ToolCallCount = 0,
    long ReasoningEventCount = 0,
    long TokenInput = 0,
    long TokenOutput = 0,
    long CacheHitTokens = 0,
    bool DesktopLive = false,
    string? DesktopState = null,
    string? ExecutionSource = null,
    string? ProgressStage = null,
    string? ProgressSummary = null,
    [property: JsonConverter(typeof(ReasonixDateTimeConverter))] DateTime? ProgressUpdatedUtc = null,
    int? CompletedChecks = null,
    int? TotalChecks = null,
    string? ManifestDiagnostic = null,
    string? ProgressDiagnostic = null,
    string? BudgetState = null,
    long? BudgetOverrunSteps = null,
    string? LastEventKind = null,
    string? LastToolName = null,
    string? FailureKind = null,
    string? FailureSummary = null,
    int? AttemptNumber = null,
    string? ProgressSource = null)
{
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase) || string.Equals(State, "starting", StringComparison.OrdinalIgnoreCase);

    /// <summary>失败类型是否允许安全原地重试（仅失败/中断/取消且无活进程时）。</summary>
    public bool IsRetryableState => string.Equals(State, "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "interrupted", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "cancelled", StringComparison.OrdinalIgnoreCase);

    /// <summary>任务期间 Reasonix CLI 是否已产生真实的执行活动（步骤或工具调用）。</summary>
    public bool HasExecutionActivity => StepCount > 0 || ToolCallCount > 0 || ReasoningEventCount > 0;

    /// <summary>是否已可靠绑定本轮会话（新会话或可证明继续写入的会话）。</summary>
    public bool HasBoundSession => !string.IsNullOrWhiteSpace(ReasonixSessionPath);

    public string StrategyDisplay => string.IsNullOrWhiteSpace(ExecutionIntensity)
        ? "未记录"
        : $"{ExecutionIntensity}/{ExecutionProfile ?? "-"}/{ExecutionEffort ?? "-"}" + (EstimatedSteps is > 0 ? $"（预计 ≤{EstimatedSteps} 步）" : string.Empty);
}

/// <summary>单个无法读取的状态文件的诊断信息（仅文件名，不暴露完整路径）。</summary>
public sealed record ReasonixTaskDiagnostic(string FileName, string Reason);

/// <summary>安全原地重试的结果：成功或可读的阻断原因。</summary>
public sealed record ReasonixRetryResult(bool Success, string Message);

/// <summary>任务快照：有效任务列表与损坏文件诊断摘要。</summary>
public sealed record ReasonixTasksSnapshot(IReadOnlyList<ReasonixTaskStatus> Tasks, IReadOnlyList<ReasonixTaskDiagnostic> Diagnostics);

/// <summary>
/// 兼容 Windows PowerShell ConvertTo-Json 的 /Date(milliseconds)/ 日期格式，
/// 同时接受标准 ISO 8601 与 Unix 毫秒时间戳；新格式写 ISO 8601 UTC。
/// </summary>
public sealed class ReasonixDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (TryParseReasonixDate(reader.GetString(), out var value)) return value;
            throw new JsonException("无法解析 Reasonix 日期字段。");
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64()).UtcDateTime; }
            catch (ArgumentOutOfRangeException ex) { throw new JsonException("Reasonix 日期超出支持范围。", ex); }
        }
        throw new JsonException($"不支持的 Reasonix 日期类型 {reader.TokenType}。");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static bool TryParseReasonixDate(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var candidate = text.Trim().Replace("\\/", "/"); // 防御 ConvertTo-Json 转义的 \/Date(...)\/ 字面量
        if (candidate.StartsWith("/Date(", StringComparison.Ordinal) && candidate.EndsWith(")/", StringComparison.Ordinal))
        {
            var inner = candidate.AsSpan(6, candidate.Length - 8);
            if (long.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                try { value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime; return true; }
                catch (ArgumentOutOfRangeException) { return false; }
            }
            return false;
        }
        if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            value = parsed.UtcDateTime;
            return true;
        }
        return false;
    }
}

public sealed class ReasonixIntegrationService
{
    public const string GuidanceStart = "<!-- CODEX-HELPER-REASONIX-EXECUTOR-START -->";
    public const string GuidanceEnd = "<!-- CODEX-HELPER-REASONIX-EXECUTOR-END -->";
    private readonly string codexRoot;
    private readonly AppPaths paths;
    private readonly string skillDirectory;
    private readonly string statePath;

    public ReasonixIntegrationService(string codexRoot, AppPaths paths)
    {
        this.codexRoot = Path.GetFullPath(codexRoot);
        this.paths = paths;
        skillDirectory = Path.Combine(this.codexRoot, "skills", "reasonix-executor");
        statePath = Path.Combine(paths.BaseDirectory, "reasonix-integration.json");
    }

    public string FindExecutable()
    {
        var configured = LoadState().ExecutablePath;
        var candidates = new[]
        {
            configured,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Reasonix", "reasonix-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "reasonix.cmd")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)) ?? string.Empty;
    }

    public async Task<ReasonixStatus> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable();
        if (string.IsNullOrWhiteSpace(executable))
            return new(false, string.Empty, string.Empty, string.Empty, false, "未找到 Reasonix CLI。", IsEnabled());

        var version = (await RunAsync(executable, ["--version"], cancellationToken)).StdOut.Trim();
        var doctor = await RunAsync(executable, ["doctor", "--json"], cancellationToken, allowFailure: true);
        if (doctor.ExitCode != 0)
            return new(true, executable, version, string.Empty, false, "Reasonix 诊断失败：" + FirstUsefulLine(doctor.StdErr), IsEnabled());

        using var json = ParseLenientWindowsJson(doctor.StdOut);
        var config = json.RootElement.GetProperty("config");
        var defaultModel = config.TryGetProperty("default_model", out var model) ? model.GetString() ?? string.Empty : string.Empty;
        var providers = json.RootElement.TryGetProperty("providers", out var providerArray) ? providerArray.EnumerateArray().ToList() : [];
        var providerName = defaultModel.Split('/', 2)[0];
        var activeProvider = providers.FirstOrDefault(item => string.Equals(item.GetProperty("name").GetString(), providerName, StringComparison.OrdinalIgnoreCase));
        var ready = activeProvider.ValueKind != JsonValueKind.Undefined && activeProvider.TryGetProperty("key_present", out var present) && present.GetBoolean();
        var message = ready
            ? "默认模型凭据已保存；是否有效请以“测试 Reasonix 连接”的结果为准。"
            : $"默认模型 {defaultModel} 缺少凭据，请先在 Reasonix 中重新配置。";
        return new(true, executable, version, defaultModel, ready, message, IsEnabled());
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var status = await DiagnoseAsync(cancellationToken);
        if (!status.Installed) throw new InvalidOperationException(status.CredentialMessage);
        var result = await RunAsync(status.ExecutablePath,
            ["run", "--model", status.DefaultModel, "--permission-mode", "dontAsk", "--max-steps", "1", "--output-format", "text", "只回复 READY，不要调用工具。"],
            cancellationToken, allowFailure: true);
        if (result.ExitCode != 0)
        {
            var error = FirstUsefulLine(result.StdErr);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Reasonix 连接测试失败。" : error);
        }
        return $"Reasonix 连接正常。\n模型：{status.DefaultModel}\n最小生成测试已通过。";
    }

    public async Task<IReadOnlyList<ReasonixModelOption>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable();
        if (string.IsNullOrWhiteSpace(executable)) throw new FileNotFoundException("Reasonix CLI 不存在。");
        var doctor = await RunAsync(executable, ["doctor", "--json"], cancellationToken, allowFailure: true);
        if (doctor.ExitCode != 0) throw new InvalidOperationException("无法读取 Reasonix 模型列表：" + FirstUsefulLine(doctor.StdErr));
        using var json = ParseLenientWindowsJson(doctor.StdOut);
        if (!json.RootElement.TryGetProperty("providers", out var providers)) return [];
        var result = new List<ReasonixModelOption>();
        foreach (var provider in providers.EnumerateArray())
        {
            var providerName = provider.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(providerName)) continue;
            if (provider.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in models.EnumerateArray()) AddModel(model.GetString());
            }
            else if (provider.TryGetProperty("model", out var singleModel)) AddModel(singleModel.GetString());

            void AddModel(string? modelName)
            {
                if (!string.IsNullOrWhiteSpace(modelName)) result.Add(new($"{providerName}/{modelName}", providerName, modelName));
            }
        }
        return result.DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).OrderBy(item => item.Id).ToList();
    }

    public async Task SetDefaultModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        modelId = modelId?.Trim() ?? string.Empty;
        var models = await GetAvailableModelsAsync(cancellationToken);
        if (!models.Any(item => string.Equals(item.Id, modelId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Reasonix 当前配置中不存在模型 {modelId}。");
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "reasonix", "config.toml");
        if (!File.Exists(configPath)) throw new FileNotFoundException("Reasonix 全局配置不存在。", configPath);
        var lines = File.ReadAllText(configPath, Encoding.UTF8).Replace("\r\n", "\n").Split('\n').ToList();
        var index = lines.FindIndex(line => line.TrimStart().StartsWith("default_model", StringComparison.Ordinal));
        if (index < 0) throw new InvalidDataException("Reasonix 配置缺少 default_model。");
        lines[index] = $"default_model = \"{modelId.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        AtomicFile.WriteAllText(configPath, string.Join(Environment.NewLine, lines));
        var verified = await DiagnoseAsync(cancellationToken);
        if (!string.Equals(verified.DefaultModel, modelId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Reasonix 未接受默认模型设置；当前仍为 {verified.DefaultModel}。");
        var state = LoadState();
        paths.EnsureCreated();
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state with { DefaultModel = modelId }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Enable(string executablePath, string defaultModel, ReasonixPermissionMode permissionMode = ReasonixPermissionMode.Full)
    {
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Reasonix CLI 不存在。", executablePath);
        Directory.CreateDirectory(skillDirectory);
        AtomicFile.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), BuildSkill());
        WriteManagedScripts(executablePath, permissionMode);
        UpdateGuidance(enabled: true);
        paths.EnsureCreated();
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(new IntegrationState(true, executablePath, defaultModel, permissionMode), new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Disable()
    {
        UpdateGuidance(enabled: false);
        if (Directory.Exists(skillDirectory)) Directory.Delete(skillDirectory, recursive: true);
        paths.EnsureCreated();
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(new IntegrationState(false, FindExecutable(), string.Empty, ReasonixPermissionMode.Full), new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool IsEnabled() => LoadState().Enabled && File.Exists(Path.Combine(skillDirectory, "invoke-reasonix.ps1")) && HasGuidance();

    private IntegrationState LoadState()
    {
        if (!File.Exists(statePath)) return new(false, string.Empty, string.Empty, ReasonixPermissionMode.Full);
        try { return JsonSerializer.Deserialize<IntegrationState>(File.ReadAllText(statePath, Encoding.UTF8)) ?? new(false, string.Empty, string.Empty, ReasonixPermissionMode.Full); }
        catch { return new(false, string.Empty, string.Empty, ReasonixPermissionMode.Full); }
    }

    public ReasonixPermissionMode GetPermissionMode() => LoadState().PermissionMode ?? ReasonixPermissionMode.Full;

    public void RefreshManagedScripts()
    {
        var state = LoadState();
        if (!state.Enabled) return;
        var executable = FindExecutable();
        if (string.IsNullOrWhiteSpace(executable)) return;
        WriteManagedScripts(executable, state.PermissionMode ?? ReasonixPermissionMode.Full);
        UpdateGuidance(true);
    }

    public void SetPermissionMode(ReasonixPermissionMode permissionMode)
    {
        var state = LoadState();
        var executable = FindExecutable();
        if (string.IsNullOrWhiteSpace(executable)) throw new FileNotFoundException("Reasonix CLI 不存在。");
        if (state.Enabled) WriteManagedScripts(executable, permissionMode);
        paths.EnsureCreated();
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state with { ExecutablePath = executable, PermissionMode = permissionMode }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public ReasonixTasksSnapshot GetRecentTasks(int limit = 20)
    {
        paths.EnsureCreated();
        var tasks = new List<ReasonixTaskStatus>();
        var diagnostics = new List<ReasonixTaskDiagnostic>();
        var wanted = Math.Max(1, limit);
        foreach (var file in Directory.EnumerateFiles(paths.ReasonixTasksDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(File.ReadAllText(file, Encoding.UTF8), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (status is not null)
                {
                    // 刚进入 starting 且 PID 尚未写回时提供短暂 grace，不得立即归一化为 interrupted；
                    // 超过 grace 仍无活进程才归一化，避免重试启动的窗口被误判。
                    var inStartingGrace = string.Equals(status.State, "starting", StringComparison.OrdinalIgnoreCase)
                        && status.HostProcessId <= 0
                        && DateTime.UtcNow - status.UpdatedUtc < StartingGrace;
                    if (status.IsRunning && !IsProcessAlive(status.HostProcessId) && !inStartingGrace)
                    {
                        var reportExists = File.Exists(Path.Combine(status.TaskDirectory, "EXECUTION_REPORT.md"));
                        status = status with
                        {
                            State = "interrupted",
                            Phase = reportExists ? "等待验收" : "意外停止",
                            Message = reportExists ? "执行进程已退出，执行报告可供验收。" : "执行进程已退出，未生成执行报告。",
                            FailureKind = "interrupted",
                            FailureSummary = reportExists ? "host 进程意外退出，但交付报告存在。" : "host 进程意外退出，未生成交付报告。"
                        };
                    }
                    tasks.Add(status);
                    if (tasks.Count >= wanted) break;
                }
                else diagnostics.Add(new(Path.GetFileName(file), "状态文件内容为空。"));
            }
            catch (Exception ex)
            {
                // 单个损坏文件不得让整个列表失效；改为可见、可诊断的摘要。
                diagnostics.Add(new(Path.GetFileName(file), DescribeStatusFileError(ex)));
            }
        }
        return new ReasonixTasksSnapshot(tasks.OrderByDescending(item => item.UpdatedUtc).ToList(), diagnostics);
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        try { return !Process.GetProcessById(processId).HasExited; }
        catch { return false; }
    }

    private static string DescribeStatusFileError(Exception ex) => ex switch
    {
        JsonException => "JSON 语法或日期格式无效",
        IOException => "文件读取失败",
        UnauthorizedAccessException => "文件无访问权限",
        _ => ex.GetType().Name
    };

    public void StopTask(ReasonixTaskStatus task)
    {
        if (!task.IsRunning || task.HostProcessId <= 0) return;
        try { Process.GetProcessById(task.HostProcessId).Kill(entireProcessTree: true); } catch { }
        var stopped = task with { State = "cancelled", Phase = "已停止", UpdatedUtc = DateTime.UtcNow, Message = "用户从 Codex Helper 停止了任务。", FailureKind = "user-stopped", FailureSummary = "用户主动停止任务。" };
        AtomicFile.WriteAllText(Path.Combine(paths.ReasonixTasksDirectory, task.TaskId + ".json"), JsonSerializer.Serialize(stopped, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static readonly string[] RetryContractFiles = ["SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json"];
    private static readonly string[] RetryArchiveFiles = ["events.jsonl", "metrics.json", "REVIEW_PACKET.md", "EXECUTION_REPORT.md", "FAILURE_REPORT.md", "PROGRESS.json", "helper-stderr.txt"];
    /// <summary>starting 状态但 PID 尚未写回时的短暂宽限，避免被误归一化为 interrupted。</summary>
    private static readonly TimeSpan StartingGrace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 返回该任务为何不能重试的阻断原因；null 表示可以安全原地重试（B4）。
    /// 仅允许失败/中断/取消且进程不存活、合同四文件完整、项目与任务路径仍在原范围、无项目 lock 占用。
    /// </summary>
    public string? RetryBlockReason(ReasonixTaskStatus task)
    {
        if (task is null) return "任务不存在。";
        if (!task.IsRetryableState) return $"当前状态 {task.State} 不可重试。";
        if (IsProcessAlive(task.HostProcessId)) return "任务进程仍在运行，不能重试。";
        if (string.IsNullOrWhiteSpace(task.ProjectRoot) || string.IsNullOrWhiteSpace(task.TaskDirectory)) return "任务路径缺失，无法重试。";
        try
        {
            var project = Path.GetFullPath(task.ProjectRoot);
            var taskDir = Path.GetFullPath(task.TaskDirectory);
            var runs = Path.Combine(project, ".codex-helper", "runs");
            if (!PathSafety.IsWithin(taskDir, runs)) return "任务目录不在项目范围内，拒绝越界重试。";
        }
        catch { return "任务路径非法，无法重试。"; }
        foreach (var name in RetryContractFiles)
            if (!File.Exists(Path.Combine(task.TaskDirectory, name))) return $"缺少合同文件 {name}，无法重试。";
        var lockPath = Path.Combine(task.ProjectRoot, ".codex-helper", "runs", ".reasonix.lock");
        if (IsLocked(lockPath)) return "存在任务锁占用，请稍后再试。";
        if (!File.Exists(Path.Combine(skillDirectory, "invoke-reasonix.ps1"))) return "缺少托管 invoke 脚本，无法重试。";
        return null;
    }

    /// <summary>
    /// 安全原地重试（B5–B7）：先复制并验证归档、再清理根运行产物；写 RETRY_CONTEXT；
    /// 递增 AttemptNumber 置 starting 后启动同一托管 invoke 脚本，回写启动 PID；
    /// 启动失败时恢复原状态与运行产物，绝不留 starting 假状态或半归档。
    /// 任务级 .retry.lock 独占准备，配合 starting 状态共同防双击/并发。
    /// </summary>
    public async Task<ReasonixRetryResult> RetryTaskAsync(ReasonixTaskStatus task, CancellationToken cancellationToken = default)
    {
        var blockReason = RetryBlockReason(task);
        if (blockReason is not null) return new(false, blockReason);
        FileStream? retryLock = null;
        string? archiveDir = null;
        try
        {
            var taskDir = Path.GetFullPath(task.TaskDirectory);
            var retryLockPath = Path.Combine(taskDir, ".retry.lock");
            try { retryLock = new FileStream(retryLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return new(false, "该任务的重试正在准备中，请稍后再试。"); }
            catch (UnauthorizedAccessException) { return new(false, "无法锁定任务进行重试，请稍后再试。"); }

            var oldAttempt = task.AttemptNumber ?? 1;
            var newAttempt = oldAttempt + 1;
            var archiveName = $"attempt-{oldAttempt}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            archiveDir = Path.Combine(taskDir, "attempts", archiveName);
            Directory.CreateDirectory(archiveDir);

            // 1) 先复制并验证归档（不移动；任何一步失败都不动根运行产物）。
            var copied = new List<string>();
            foreach (var file in RetryArchiveFiles)
            {
                var source = Path.Combine(taskDir, file);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(archiveDir, file), overwrite: true);
                copied.Add(file);
            }
            // 状态快照（脱敏：仅状态内容，不含合同正文）。
            AtomicFile.WriteAllText(Path.Combine(archiveDir, "status.json"), JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var file in copied)
                if (!File.Exists(Path.Combine(archiveDir, file))) throw new InvalidOperationException($"归档验证失败：{file}。");

            // 2) 验证归档后再清理根运行产物（仅删除已成功复制到归档的根文件）。
            foreach (var file in copied)
            {
                var rootFile = Path.Combine(taskDir, file);
                if (File.Exists(rootFile)) File.Delete(rootFile);
            }

            // 3) 写 RETRY_CONTEXT，指向归档内 FAILURE_REPORT 相对路径，不引用已移走的根文件、不嵌入自由 FailureSummary。
            var hasFailureReport = File.Exists(Path.Combine(archiveDir, "FAILURE_REPORT.md"));
            var context = $$"""
# RETRY_CONTEXT.md

从当前源码继续收尾本次 Reasonix 任务。优先读取归档内失败摘要与 ACCEPTANCE.md 中未满足的验收项，聚焦剩余范围收敛；不要重新分析已完成的部分，不要重跑已通过的检查。完成后照常写 EXECUTION_REPORT.md。

- 任务：{{task.TaskId}}
- 本次尝试编号：{{newAttempt}}
- 项目根：{{task.ProjectRoot}}
- 原 CodexThreadId：{{task.CodexThreadId ?? "-"}}
- 失败报告：attempts/{{archiveName}}/FAILURE_REPORT.md
{{(hasFailureReport ? "" : "（旧尝试未生成 FAILURE_REPORT.md 时可结合归档内 status.json 的状态信息。）")}}
""";
            AtomicFile.WriteAllText(Path.Combine(taskDir, "RETRY_CONTEXT.md"), context);

            // 4) 递增 AttemptNumber 并置为 starting（同时使其不再是可重试状态，从而防双击/并发）。
            var next = task with
            {
                State = "starting",
                Phase = "starting",
                AttemptNumber = newAttempt,
                UpdatedUtc = DateTime.UtcNow,
                Message = "正在从当前源码继续重试。",
                FailureKind = null,
                FailureSummary = null,
                BudgetState = null,
                BudgetOverrunSteps = null,
                HostProcessId = 0
            };
            var statusPath = Path.Combine(paths.ReasonixTasksDirectory, task.TaskId + ".json");
            AtomicFile.WriteAllText(statusPath, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));

            // 5) 启动 runner 并回写启动 PID（starting 状态下提供可靠进程绑定）。
            var runner = Path.Combine(skillDirectory, "invoke-reasonix.ps1");
            var pid = await Task.Run(() => StartRunner(runner, task.ProjectRoot, taskDir, task.CodexThreadId), cancellationToken);
            if (pid <= 0) throw new InvalidOperationException("启动托管脚本失败。");
            try
            {
                var latest = JsonSerializer.Deserialize<ReasonixTaskStatus>(File.ReadAllText(statusPath, Encoding.UTF8), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                AtomicFile.WriteAllText(statusPath, JsonSerializer.Serialize((latest ?? next) with { HostProcessId = pid }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 状态竞争时以已完成写入为准，不阻断 */ }

            return new(true, $"已启动第 {newAttempt} 次尝试。由 Helper 启动的重试无法自动唤醒既有 GPT 轮次，完成后请返回原 Codex 任务继续验收。");
        }
        catch (OperationCanceledException)
        {
            // 6) 取消回滚：与普通异常一致，仅恢复本次调用新建的归档与运行产物，绝不触碰其他 attempt 历史目录。
            RestoreRetryState(task, archiveDir);
            return new(false, "重试已取消。");
        }
        catch (Exception ex)
        {
            // 6) 启动失败回滚：仅恢复本次调用新建的归档与运行产物，绝不触碰其他 attempt 历史目录。
            RestoreRetryState(task, archiveDir);
            return new(false, "重试失败：" + ex.Message);
        }
        finally { retryLock?.Dispose(); }
    }

    /// <summary>
    /// 回滚本次重试：恢复原状态文件，并仅从本次调用新建的 archiveDir 把运行产物移回任务根。
    /// 不得通过“最新目录”或遍历 attempts 猜测，绝不读取/移动/删除其他 attempt-* 历史目录的文件。
    /// archiveDir 为 null（归档创建前失败）时仅恢复状态。
    /// </summary>
    private void RestoreRetryState(ReasonixTaskStatus task, string? archiveDir)
    {
        // 1) 恢复原任务状态（若已切换为 starting），绝不留 starting 假状态。
        try
        {
            var statusPath = Path.Combine(paths.ReasonixTasksDirectory, task.TaskId + ".json");
            AtomicFile.WriteAllText(statusPath, JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        // 2) 仅回滚本次调用新建的归档目录。
        if (string.IsNullOrWhiteSpace(archiveDir)) return;
        try
        {
            var taskDir = Path.GetFullPath(task.TaskDirectory);
            var fullArchive = Path.GetFullPath(archiveDir);
            if (!Directory.Exists(fullArchive)) return;
            // 防御越界：归档必须恰好位于 taskDir/attempts/<name> 之下，否则不做任何文件操作。
            var attemptsDir = Path.GetFullPath(Path.Combine(taskDir, "attempts"));
            var archiveParent = Path.GetFullPath(Path.GetDirectoryName(fullArchive)!);
            if (!string.Equals(archiveParent, attemptsDir, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var file in Directory.EnumerateFiles(fullArchive))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, "status.json", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(taskDir, name);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(file, dest);
            }
            // 本次归档保留 status.json 快照作为历史证据，不清空目录（安全、可重复）。
        }
        catch { }
    }

    /// <summary>启动托管 runner，返回启动进程的 PID（<=0 表示未启动成功）。</summary>
    private static int StartRunner(string runnerScript, string projectRoot, string taskDirectory, string? codexThreadId)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(runnerScript);
        psi.ArgumentList.Add("-ProjectRoot");
        psi.ArgumentList.Add(projectRoot);
        psi.ArgumentList.Add("-TaskDirectory");
        psi.ArgumentList.Add(taskDirectory);
        if (!string.IsNullOrWhiteSpace(codexThreadId))
        {
            psi.ArgumentList.Add("-CodexThreadId");
            psi.ArgumentList.Add(codexThreadId);
        }
        using var process = new Process { StartInfo = psi };
        return process.Start() ? process.Id : 0;
    }

    /// <summary>探测 .reasonix.lock 是否被占用（host 独占打开时视为占用）。</summary>
    private static bool IsLocked(string lockPath)
    {
        try { using var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private void WriteManagedScripts(string executablePath, ReasonixPermissionMode permissionMode)
    {
        Directory.CreateDirectory(skillDirectory);
        AtomicFile.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), BuildSkill());
        WriteManagedPowerShell(Path.Combine(skillDirectory, "run-reasonix-job.ps1"), BuildJobHost(executablePath, permissionMode, paths.SettingsPath));
        WriteManagedPowerShell(Path.Combine(skillDirectory, "invoke-reasonix.ps1"), BuildRunner(paths.ReasonixTasksDirectory));
    }

    /// <summary>
    /// 托管 PowerShell 脚本以 UTF-8 BOM 写入，确保 Windows PowerShell 5.1 正确按 UTF-8 解码
    /// 其中的中文（无 BOM 会按系统代码页误读并破坏语法）。
    /// </summary>
    private static void WriteManagedPowerShell(string path, string content)
    {
        var preamble = new UTF8Encoding(true).GetPreamble();
        var body = Encoding.UTF8.GetBytes(content);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    private bool HasGuidance()
    {
        var path = Path.Combine(codexRoot, "AGENTS.md");
        return File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Contains(GuidanceStart, StringComparison.Ordinal);
    }

    private void UpdateGuidance(bool enabled)
    {
        Directory.CreateDirectory(codexRoot);
        var path = Path.Combine(codexRoot, "AGENTS.md");
        var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        existing = RemoveMarkedBlock(existing, GuidanceStart, GuidanceEnd).TrimEnd();
        if (enabled)
        {
            var block = $$"""
{{GuidanceStart}}
For implementation tasks that change project files, GPT is the planner and judge and Reasonix is the executor. GPT must create a unique task directory under `<project>/.codex-helper/runs/run-<timestamp>-<guid>/` containing `SPEC.md`, `ACCEPTANCE.md`, `HANDOFF.md`, and `manifest.json`. Then invoke the `reasonix-executor` skill runner with only the absolute project root and task directory and **no command timeout** (the host waits indefinitely, without polling, until Reasonix exits; there is no task-duration limit). Do not configure a fixed one-hour or any other finite timeout for this command; only a user-initiated Stop in Codex Helper or closing Codex should end it. The same GPT turn resumes and performs acceptance after the runner returns. Do not poll the event log. Codex Helper shows a live event view; the session syncs to Reasonix Desktop once its session file appears. After the runner returns, inspect `REVIEW_PACKET.md`, the actual diff, and rerun acceptance checks. GPT owns visual acceptance and gptChecks/releaseChecks; Reasonix performs implementation and workerChecks only. Execution intensity (auto/fast/standard/strict) is declared in manifest.json or inferred from the contract scope; Fast/Standard never auto-start review subagents. The manifest.json budget fields are `budgetSteps` (soft budget) and `maxSteps` (hard cap); `estimatedSteps` is not supported. Do not use Codex native subagents. Pure questions and read-only reviews stay in GPT.
{{GuidanceEnd}}
""";
            AtomicFile.WriteAllText(path, string.IsNullOrWhiteSpace(existing) ? block + Environment.NewLine : existing + Environment.NewLine + Environment.NewLine + block + Environment.NewLine);
        }
        else if (string.IsNullOrWhiteSpace(existing))
        {
            if (File.Exists(path)) File.Delete(path);
        }
        else AtomicFile.WriteAllText(path, existing + Environment.NewLine);
    }

    private static string BuildSkill() => """
---
name: reasonix-executor
description: Execute an already planned implementation task through the managed Reasonix CLI runner. Use only after GPT has written SPEC.md, ACCEPTANCE.md, HANDOFF.md and manifest.json in a unique project-local run directory.
---

# Reasonix Executor

GPT remains planner and judge. This skill only launches Reasonix as the implementation hand.

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File invoke-reasonix.ps1 -ProjectRoot <absolute project root> -TaskDirectory <absolute run directory>`. The runner binds the newest active user Codex task automatically; pass `-CodexThreadId <uuid>` when the current thread id is explicitly available.
Never pass the task body as a command-line argument. Run the command with **no command timeout** (wait indefinitely; the host has no task-duration limit) and wait for it to return; this consumes no repeated GPT turns and must not be replaced by log polling. Do not configure a fixed one-hour or any other finite timeout for this command — only user-initiated Stop in Codex Helper or closing Codex should end it. Tell the user the task is visible in Codex Helper (live event view) and will be synced to Reasonix Desktop once its session file appears. After completion, read REVIEW_PACKET.md and EXECUTION_REPORT.md, inspect actual changed files, and independently rerun acceptance checks in this same GPT turn. GPT owns visual acceptance and gptChecks/releaseChecks; Reasonix performs implementation and workerChecks only. Execution intensity (auto/fast/standard/strict) comes from manifest.json or is inferred; Fast/Standard never auto-start review subagents. The manifest.json budget fields are `budgetSteps` (soft budget) and `maxSteps` (hard cap); `estimatedSteps` is not supported. Do not commit, push, reset, clean, delete important files, modify credentials, or install dependencies unless the user explicitly authorized it.
""";

    private static string BuildRunner(string taskRegistry) => $$"""
param(
    [Parameter(Mandatory=$true)][string]$ProjectRoot,
    [Parameter(Mandatory=$true)][string]$TaskDirectory,
    [string]$CodexThreadId=''
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
$task = [IO.Path]::GetFullPath($TaskDirectory).TrimEnd('\')
$runs = [IO.Path]::GetFullPath((Join-Path $project '.codex-helper\runs')).TrimEnd('\')
if (-not ($task.StartsWith($runs + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Task directory must be inside <project>\.codex-helper\runs.' }
foreach ($name in @('SPEC.md','ACCEPTANCE.md','HANDOFF.md','manifest.json')) { if (-not [IO.File]::Exists((Join-Path $task $name))) { throw "Missing required task file: $name" } }
$registry = '{{taskRegistry.Replace("'", "''")}}'
New-Item -ItemType Directory -Path $registry -Force | Out-Null
$taskId = Split-Path $task -Leaf
$status = Join-Path $registry ($taskId + '.json')
$hostScript = Join-Path $PSScriptRoot 'run-reasonix-job.ps1'
$reasonixHomePath=if([string]::IsNullOrWhiteSpace($env:CODEX_HELPER_REASONIX_HOME)){Join-Path $env:APPDATA 'reasonix'}else{[IO.Path]::GetFullPath($env:CODEX_HELPER_REASONIX_HOME)}
if([string]::IsNullOrWhiteSpace($CodexThreadId)){
  $sessionRoot=Join-Path $env:USERPROFILE '.codex\sessions'
  if([IO.Directory]::Exists($sessionRoot)){
    $current=Get-ChildItem -LiteralPath $sessionRoot -Filter 'rollout-*.jsonl' -File -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if($null-ne$current -and $current.BaseName -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$'){$CodexThreadId=$Matches[1]}
  }
}
function Quote-Arg([string]$value) { return '"' + $value.Replace('"','\"') + '"' }
$arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $hostScript) + ' -ProjectRoot ' + (Quote-Arg $project) + ' -TaskDirectory ' + (Quote-Arg $task) + ' -StatusPath ' + (Quote-Arg $status) + ' -CodexThreadId ' + (Quote-Arg $CodexThreadId) + ' -ReasonixHome ' + (Quote-Arg $reasonixHomePath)
$process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "Reasonix task running: $taskId (host PID $($process.Id)). View progress in Codex Helper or Reasonix Desktop."
Write-Output "Waiting indefinitely (no command timeout; no task-duration limit) for the host to exit; stop only via Codex Helper Stop or closing Codex."
$process.WaitForExit()
if($process.ExitCode -ne 0){throw "Reasonix task host failed with exit code $($process.ExitCode)."}
Write-Output "Reasonix task finished: $taskId. GPT must now inspect REVIEW_PACKET.md and perform acceptance in this same turn."
""";

    private static string BuildJobHost(string executable, ReasonixPermissionMode permissionMode, string settingsPath)
    {
        var permissionArgs = permissionMode == ReasonixPermissionMode.Full
            ? "$permissionArgs=@('--permission-mode','bypassPermissions')"
            : "$permissionArgs=@('--permission-mode','acceptEdits','--allowed-tools','Bash(dotnet --info:*)','--allowed-tools','Bash(dotnet restore:*)','--allowed-tools','Bash(dotnet build:*)','--allowed-tools','Bash(dotnet test:*)','--allowed-tools','Bash(dotnet run:*)','--allowed-tools','Bash(dotnet publish:*)')";
        return $$$"""
param([Parameter(Mandatory=$true)][string]$ProjectRoot,[Parameter(Mandatory=$true)][string]$TaskDirectory,[Parameter(Mandatory=$true)][string]$StatusPath,[string]$CodexThreadId='',[Parameter(Mandatory=$true)][string]$ReasonixHome)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
$task=[IO.Path]::GetFullPath($TaskDirectory).TrimEnd('\')
$taskId=Split-Path $task -Leaf
$events=Join-Path $task 'events.jsonl'; $metrics=Join-Path $task 'metrics.json'; $report=Join-Path $task 'EXECUTION_REPORT.md'; $review=Join-Path $task 'REVIEW_PACKET.md'; $helperErr=Join-Path $task 'helper-stderr.txt'
$started=[DateTime]::UtcNow; $startedText=$started.ToString('o'); $startTicks=$started.Ticks
$script:count=0; $script:reasonixSession=''; $script:desktopState='awaiting-session'; $script:desktopDiagnostic='not-attempted'
$script:returnUri=if([string]::IsNullOrWhiteSpace($CodexThreadId)){''}else{'codex://threads/'+$CodexThreadId}; $script:returnState='pending'
$script:stepCount=0; $script:toolCallCount=0; $script:reasoningCount=0; $script:finalSteps=-1
$script:tokenInput=0; $script:tokenOutput=0; $script:cacheHit=0
$script:budgetWarningNotified=$false; $script:budgetExceededNotified=$false; $script:lastSavedSteps=-1
$script:budgetFinalReady=$false; $script:finalBudgetState=$null; $script:finalOverrun=$null
$script:lastEventKind=''; $script:lastToolName=''; $script:lastStage='analyzing'; $script:failureKind=$null; $script:failureSummary=$null; $script:modelRunFailed=$false
$script:attemptNumber=1
$script:reasonixModel=''; $script:manifestDiagnostic=$null; $script:progressDiagnostic=$null
$script:planIntensity='auto'; $script:planProfile='balanced'; $script:planEffort='medium'; $script:planComplexity='medium'
$script:planBudget=80; $script:planMaxSteps=$null; $script:planSource='inferred'; $script:allowAutoReview=$false; $script:workerChecks=@()
function Get-Progress {
  $progressPath=Join-Path $task 'PROGRESS.json'
  if(-not [IO.File]::Exists($progressPath)){ return $null }
  try{
    if((Get-Item -LiteralPath $progressPath).Length -gt 16384){ $script:progressDiagnostic='PROGRESS.json 超过 16KB，已忽略'; return $null }
    $p=[IO.File]::ReadAllText($progressPath,[Text.Encoding]::UTF8)|ConvertFrom-Json
    # 标准 stage 协议优先；缺 stage 时兼容旧式 phase 白名单映射。
    $stage=$null
    if($p.PSObject.Properties['stage'] -and -not [string]::IsNullOrWhiteSpace([string]$p.stage)){
      $stage=([string]$p.stage).ToLowerInvariant()
      if(@('analyzing','implementing','testing','reporting','done','blocked') -notcontains $stage){ $script:progressDiagnostic="PROGRESS.json 未知 stage: $stage，已忽略"; return $null }
    }
    elseif($p.PSObject.Properties['phase'] -and -not [string]::IsNullOrWhiteSpace([string]$p.phase)){
      $rawPhase=([string]$p.phase).ToLowerInvariant()
      $stage=switch($rawPhase){
        'analysis' {'analyzing'}
        'analyzing' {'analyzing'}
        'implementation' {'implementing'}
        'implementing' {'implementing'}
        'test' {'testing'}
        'testing' {'testing'}
        'verification' {'testing'}
        'report' {'reporting'}
        'reporting' {'reporting'}
        'done' {'done'}
        'completed' {'done'}
        'blocked' {'blocked'}
        default {''}
      }
      if([string]::IsNullOrWhiteSpace($stage)){ $script:progressDiagnostic="PROGRESS.json 未知 phase: $rawPhase，已忽略"; return $null }
    }
    else { $script:progressDiagnostic='PROGRESS.json 缺少 stage/phase，已忽略'; return $null }
    if($p.PSObject.Properties['taskId'] -and [string]$p.taskId -ne $taskId){ $script:progressDiagnostic='PROGRESS.json taskId 与任务不匹配，已忽略'; return $null }
    $summary=''
    if($p.PSObject.Properties['summary'] -and $null-ne$p.summary){ $summary=[string]$p.summary; if($summary.Length -gt 240){ $summary=$summary.Substring(0,240); $script:progressDiagnostic='PROGRESS.json summary 超长已截断' } }
    $completed=-1; $total=-1
    if($p.PSObject.Properties['completedChecks'] -and $null-ne$p.completedChecks){ $completed=[int]$p.completedChecks }
    if($p.PSObject.Properties['totalChecks'] -and $null-ne$p.totalChecks){ $total=[int]$p.totalChecks }
    # 仅当标准 completedChecks/totalChecks 未显式提供时，从 steps 数组兜底统计（只统计对象项，绝不展示 step 名称/内容）。
    if(($completed -lt 0 -or $total -lt 0) -and $p.PSObject.Properties['steps'] -and $p.steps -is [System.Array]){
      $valid=@($p.steps | Where-Object { $_ -is [System.Management.Automation.PSCustomObject] }).Count
      if($valid -gt 0){
        $stepDone=@($p.steps | Where-Object { $_ -is [System.Management.Automation.PSCustomObject] -and @('completed','done','passed') -contains ([string]$_.status).ToLowerInvariant() }).Count
        if($completed -lt 0){ $completed=$stepDone }
        if($total -lt 0){ $total=$valid }
      }
    }
    $updated=$null
    if($p.PSObject.Properties['updatedUtc'] -and $null-ne$p.updatedUtc){
      $candidate=[string]$p.updatedUtc
      $dt=[datetime]::MinValue
      if([datetime]::TryParse($candidate,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$dt)){
        $dtUtc=$dt.ToUniversalTime()
        if($dtUtc -gt ([datetime]::UtcNow.AddMinutes(2))){ $dtUtc=[datetime]::UtcNow; $script:progressDiagnostic='PROGRESS.json updatedUtc 明显晚于当前时间，已夹到观察时间' }
        $updated=$dtUtc.ToString('o')
      }
      else { $script:progressDiagnostic='PROGRESS.json updatedUtc 格式非法，已忽略' }
    }
    return [pscustomobject]@{stage=$stage;summary=$summary;completedChecks=$completed;totalChecks=$total;updatedUtc=$updated}
  }catch{ $script:progressDiagnostic='PROGRESS.json 无法解析，已忽略'; return $null }
}
function Get-ReasonixModel {
  $configPath=Join-Path $ReasonixHome 'config.toml'
  if(-not [IO.File]::Exists($configPath)){ return '' }
  try{
    $text=[IO.File]::ReadAllText($configPath,[Text.Encoding]::UTF8)
    foreach($line in ($text -split "`r?`n")){
      $m=[regex]::Match($line,'^\s*default_model\s*=\s*"([^"]+)"')
      if($m.Success){ return $m.Groups[1].Value }
    }
  }catch{}
  return ''
}
function Get-StageRank([string]$s){
  switch($s){ 'analyzing'{1} 'implementing'{2} 'testing'{3} 'reporting'{4} 'done'{5} 'blocked'{6} default{0} }
}
function Save-Status([string]$state,[string]$phase,[string]$message){
  $progress=Get-Progress
  $pgSummary=$null; $pgUpdated=$null; $pgDone=-1; $pgTotal=-1
  if($null-ne$progress){ $pgSummary=$progress.summary; $pgUpdated=$progress.updatedUtc; $pgDone=$progress.completedChecks; $pgTotal=$progress.totalChecks }
  # Helper 主导基础阶段（F2/F4）：默认 analyzing→implementing；EXECUTION_REPORT 出现→reporting。
  $helperStage=$script:lastStage
  if([IO.File]::Exists($report)){ $helperStage='reporting' }
  $pgStage=$helperStage; $pgSource='helper'
  if($null-ne$progress){
    # 外部 PROGRESS 白名单合法阶段可把阶段提升到更前进阶段（analyzing→…→reporting/done/blocked），
    # 但不得降级 Helper 已到达的阶段；done 仅在任务完成时采纳。
    $progressStage=[string]$progress.stage
    $allow=$false
    if($progressStage -eq 'done'){ $allow=($state -eq 'completed') }
    elseif(@('analyzing','implementing','testing','reporting','blocked') -contains $progressStage){ $allow=$true }
    if($allow -and (Get-StageRank $progressStage) -ge (Get-StageRank $helperStage)){ $pgStage=$progressStage; $pgSource='reasonix' }
  }
  # 成功终态强制 done；失败不得伪装 done。
  if($state -eq 'completed'){ $pgStage='done'; $pgSource='helper' }
  # 软预算（A3）：达到预算=warning，达到150%=exceeded；都不终止任务。
  $budgetState=$null; $overrun=$null
  if($script:budgetFinalReady){
    $budgetState=$script:finalBudgetState; $overrun=$script:finalOverrun
  }
  elseif($script:planBudget -gt 0){
    if($script:budgetExceededNotified -or $script:budgetWarningNotified){
      $budgetState=if($script:budgetExceededNotified){'exceeded'}else{'warning'}
      $overrun=if($script:stepCount -gt $script:planBudget){[int]($script:stepCount-$script:planBudget)}else{0}
    }
  }
  $data=[ordered]@{TaskId=$taskId;ProjectRoot=$project;TaskDirectory=$task;State=$state;Phase=$phase;PermissionMode='{{{permissionMode}}}';StartedUtc=$startedText;UpdatedUtc=[DateTime]::UtcNow.ToString('o');HostProcessId=$PID;EventCount=$script:count;Message=$message;CodexThreadId=$CodexThreadId;ReasonixSessionPath=$script:reasonixSession;ReturnUri=$script:returnUri;ReturnState=$script:returnState;ExecutionIntensity=$script:planIntensity;ExecutionProfile=$script:planProfile;ExecutionEffort=$script:planEffort;ExecutionModel=$script:reasonixModel;EstimatedSteps=$script:planBudget;ModelTurnCount=$script:stepCount;StepCount=$(if($script:finalSteps -ge 0){$script:finalSteps}else{$script:stepCount});ToolCallCount=$script:toolCallCount;ReasoningEventCount=$script:reasoningCount;TokenInput=$script:tokenInput;TokenOutput=$script:tokenOutput;CacheHitTokens=$script:cacheHit;DesktopLive=(-not [string]::IsNullOrWhiteSpace($script:reasonixSession));DesktopState=$script:desktopState;ExecutionSource=$script:planSource;ProgressStage=$pgStage;ProgressSummary=$pgSummary;ProgressUpdatedUtc=$pgUpdated;CompletedChecks=$pgDone;TotalChecks=$pgTotal;ManifestDiagnostic=$script:manifestDiagnostic;ProgressDiagnostic=$script:progressDiagnostic;BudgetState=$budgetState;BudgetOverrunSteps=$overrun;LastEventKind=$script:lastEventKind;LastToolName=$script:lastToolName;FailureKind=$script:failureKind;FailureSummary=$script:failureSummary;AttemptNumber=$script:attemptNumber;ProgressSource=$pgSource}
  $tmp=$StatusPath+'.status-'+[Guid]::NewGuid().ToString('N')+'.tmp'
  try{[IO.File]::WriteAllText($tmp,($data|ConvertTo-Json),[Text.UTF8Encoding]::new($false));Move-Item -LiteralPath $tmp -Destination $StatusPath -Force}
  finally{if([IO.File]::Exists($tmp)){Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue}}
}
function Write-JsonAtomic([string]$path,$value){
  $tmp=$path+'.ch-'+[Guid]::NewGuid().ToString('N')+'.tmp'
  try{[IO.File]::WriteAllText($tmp,($value|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));Move-Item -LiteralPath $tmp -Destination $path -Force}
  finally{if([IO.File]::Exists($tmp)){Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue}}
}
function Write-FailureReport([string]$kind,[string]$summary,[int]$exitCode){
  $reportPath=Join-Path $task 'FAILURE_REPORT.md'
  $hasActivity=if($script:stepCount -gt 0 -or $script:toolCallCount -gt 0){'有'}else{'无'}
  $metricsSummary="模型轮次=$($script:stepCount), 工具调用=$($script:toolCallCount), 推理流=$($script:reasoningCount), 输入=$($script:tokenInput), 输出=$($script:tokenOutput), 缓存命中=$($script:cacheHit)"
  $content=@"
# Reasonix 执行失败报告

- 任务/尝试编号：$taskId / 第 $($script:attemptNumber) 次尝试
- 退出码：$exitCode
- 最后阶段：$($script:lastStage)
- 最后安全事件：$($script:lastEventKind)
- 最后工具名：$($script:lastToolName)
- Metrics 摘要：$metricsSummary
- 是否有源码活动：$hasActivity
- 失败类型：$kind
- 摘要：$summary

下一步建议：保留现有源码改动，从当前状态安全原地重试；重新读取本摘要与未满足的验收项，不要重新分析已完成范围。
"@
  [IO.File]::WriteAllText($reportPath,$content,[Text.UTF8Encoding]::new($false))
}
function Resolve-ExecutionPlan {
  $intensity=''; $profile=''; $effort=''; $complexity=''; $maxSteps=$null; $budget=$null; $declared=$false; $checks=@()
  $manifestPath=Join-Path $task 'manifest.json'
  $manifest=$null
  $script:reasonixModel=Get-ReasonixModel
  if([IO.File]::Exists($manifestPath)){ try { $manifest=[IO.File]::ReadAllText($manifestPath,[Text.Encoding]::UTF8) | ConvertFrom-Json } catch { $manifest=$null; $script:manifestDiagnostic='manifest.json 无法解析，已安全回退合同推断' } }
  if($null-ne$manifest){
    if($manifest.PSObject.Properties['intensity'] -and @('auto','fast','standard','strict') -contains ([string]$manifest.intensity).ToLowerInvariant()){ $intensity=([string]$manifest.intensity).ToLowerInvariant(); $declared=$true }
    if($manifest.PSObject.Properties['complexity'] -and @('small','medium','major') -contains ([string]$manifest.complexity).ToLowerInvariant()){ $complexity=([string]$manifest.complexity).ToLowerInvariant(); $declared=$true }
    if($manifest.PSObject.Properties['profile'] -and @('economy','balanced','delivery') -contains ([string]$manifest.profile).ToLowerInvariant()){ $profile=([string]$manifest.profile).ToLowerInvariant(); $declared=$true }
    if($manifest.PSObject.Properties['effort'] -and @('low','medium','high','max') -contains ([string]$manifest.effort).ToLowerInvariant()){ $effort=([string]$manifest.effort).ToLowerInvariant(); $declared=$true }
    $n=0; if($manifest.PSObject.Properties['maxSteps'] -and [int]::TryParse([string]$manifest.maxSteps,[ref]$n) -and $n -gt 0){ $maxSteps=$n }
    $b=0; if($manifest.PSObject.Properties['budgetSteps'] -and [int]::TryParse([string]$manifest.budgetSteps,[ref]$b) -and $b -gt 0){ $budget=$b }
    if($manifest.PSObject.Properties['estimatedSteps'] -and -not $manifest.PSObject.Properties['budgetSteps'] -and -not $manifest.PSObject.Properties['maxSteps']){ $script:manifestDiagnostic='manifest.json 使用了不支持的 estimatedSteps 字段，预算按推断处理（请改用 budgetSteps）' }
    if($manifest.PSObject.Properties['workerChecks'] -and $manifest.workerChecks -is [System.Array]){ $checks=@($manifest.workerChecks | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }) }
  }
  if(-not $intensity){
    $default='auto'
    try{
      $settingsPath='{{{settingsPath.Replace("'", "''")}}}'
      if([IO.File]::Exists($settingsPath)){ $s=[IO.File]::ReadAllText($settingsPath,[Text.Encoding]::UTF8) | ConvertFrom-Json; if($s.PSObject.Properties['reasonixExecutionIntensity']){ $default=([string]$s.reasonixExecutionIntensity).ToLowerInvariant() } }
    }catch{}
    if(@('auto','fast','standard','strict') -contains $default){ $intensity=$default }
  }
  $specPath=Join-Path $task 'SPEC.md'; $acceptPath=Join-Path $task 'ACCEPTANCE.md'
  $specText=if([IO.File]::Exists($specPath)){[IO.File]::ReadAllText($specPath)}else{''}
  $acceptText=if([IO.File]::Exists($acceptPath)){[IO.File]::ReadAllText($acceptPath)}else{''}
  if(-not $complexity){
    $acceptCount=0
    foreach($line in ($acceptText -split "`r?`n")){
      $t=[string]$line; $t=$t.Trim()
      if($t.Length -gt 0 -and -not $t.StartsWith('#')){
        if($t[0] -eq '-' -or $t[0] -eq '*' -or ($t[0] -ge '0' -and $t[0] -le '9' -and $t.Length -gt 1 -and $t[1] -eq '.')){ $acceptCount++ }
      }
    }
    $spec=$specText.ToLowerInvariant()
    if($specText.Length -ge 9000 -or $spec.Contains('major') -or $spec.Contains('product delivery') -or $spec.Contains('release delivery') -or $acceptCount -ge 12){ $complexity='major' }
    elseif($specText.Length -le 2500 -or $spec.Contains('minor') -or $spec.Contains('small fix') -or $spec.Contains('focused fix')){ $complexity='small' }
    else{ $complexity='medium' }
  }
  if(-not $profile){
    if($intensity -eq 'strict'){ $profile='delivery' }
    elseif($intensity -eq 'fast' -or $intensity -eq 'standard'){ $profile='balanced' }
    elseif($complexity -eq 'major'){ $profile='delivery' }
    else{ $profile='balanced' }
  }
  if(-not $effort){
    $deepSeek=$script:reasonixModel.ToLowerInvariant().Contains('deepseek')
    if($intensity -eq 'strict'){ $effort='high' }
    elseif($intensity -eq 'fast'){ $effort='low' }
    elseif($intensity -eq 'standard'){ $effort=if($deepSeek){'low'}else{'medium'} }
    elseif($complexity -eq 'major'){ $effort='high' }
    elseif($complexity -eq 'small'){ $effort='low' }
    else{ $effort=if($deepSeek){'low'}else{'medium'} }
  }
  if($null-eq$budget){ if($complexity -eq 'small'){$budget=25}elseif($complexity -eq 'major'){$budget=200}else{$budget=80} }
  $script:planIntensity=$intensity; $script:planProfile=$profile; $script:planEffort=$effort; $script:planComplexity=$complexity
  $script:planBudget=$budget; $script:planMaxSteps=$maxSteps; $script:planSource=if($declared){'manifest'}else{'inferred'}
  $script:allowAutoReview=($intensity -eq 'strict' -or ($intensity -eq 'auto' -and $complexity -eq 'major'))
  $script:workerChecks=$checks
}
function Get-ProjectSessionRoot {
  $reasonixHomePath=[IO.Path]::GetFullPath($ReasonixHome)
  $slug=($project.ToLowerInvariant() -replace '[:\\/]+','-')
  return Join-Path (Join-Path (Join-Path $reasonixHomePath 'projects') $slug) 'sessions'
}
function Get-SessionBaseline([string]$root){
  $map=@{}
  if(-not [string]::IsNullOrWhiteSpace($root) -and [IO.Directory]::Exists($root)){
    Get-ChildItem -LiteralPath $root -File -Filter '*.jsonl' -ErrorAction SilentlyContinue | ForEach-Object { $map[$_.FullName.ToLowerInvariant()]=[string]$_.LastWriteTimeUtc.Ticks+'|'+$_.Length }
  }
  return $map
}
function Find-NewSession([string]$root,[hashtable]$baseline){
  if([string]::IsNullOrWhiteSpace($root) -or -not [IO.Directory]::Exists($root)){return ''}
  $c=Get-ChildItem -LiteralPath $root -File -Filter '*.jsonl' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*.events.jsonl' -and -not $baseline.ContainsKey($_.FullName.ToLowerInvariant()) } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  if($null-eq$c){return ''}
  return $c.FullName
}
function Find-ResumedSession([string]$root,[hashtable]$baseline){
  if([string]::IsNullOrWhiteSpace($root) -or -not [IO.Directory]::Exists($root)){return ''}
  $c=Get-ChildItem -LiteralPath $root -File -Filter '*.jsonl' -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -notlike '*.events.jsonl' -and $baseline.ContainsKey($_.FullName.ToLowerInvariant()) -and
    $baseline[$_.FullName.ToLowerInvariant()] -ne ([string]$_.LastWriteTimeUtc.Ticks+'|'+$_.Length) -and
    $_.LastWriteTimeUtc.Ticks -ge $startTicks
  } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  if($null-eq$c){return ''}
  return $c.FullName
}
function Register-DesktopSession {
  if(-not [string]::IsNullOrWhiteSpace($script:reasonixSession)){ return }
  $session=''
  $new=Find-NewSession $script:sessionRoot $script:baseline
  if(-not [string]::IsNullOrWhiteSpace($new)){ $session=$new; $script:desktopDiagnostic='new-session' }
  else {
    $resumed=Find-ResumedSession $script:sessionRoot $script:baseline
    if(-not [string]::IsNullOrWhiteSpace($resumed)){ $session=$resumed; $script:desktopDiagnostic='resumed-session' }
    else { $script:desktopDiagnostic='no-new-session-yet'; return }
  }
  $script:reasonixSession=$session
  $topic='topic_'+$taskId.Replace('.','_').Replace('-','_'); $title='Codex Helper '+$taskId
  $metaPath=$session+'.jsonl.meta'
  $metaData=[ordered]@{}
  if([IO.File]::Exists($metaPath)){ try { $src=[IO.File]::ReadAllText($metaPath,[Text.Encoding]::UTF8)|ConvertFrom-Json; $src.psobject.Properties|ForEach-Object{ $metaData[$_.Name]=$_.Value } } catch {} }
  elseif([IO.File]::Exists($session+'.meta')){ try { $src=[IO.File]::ReadAllText(($session+'.meta'),[Text.Encoding]::UTF8)|ConvertFrom-Json; $src.psobject.Properties|ForEach-Object{ $metaData[$_.Name]=$_.Value } } catch {} }
  $metaData['id']=[IO.Path]::GetFileNameWithoutExtension($session)
  $metaData['scope']='project'; $metaData['workspace_root']=$project; $metaData['topic_id']=$topic; $metaData['topic_title']=$title
  $metaData['mode']='yolo'; $metaData['tool_approval_mode']='yolo'; $metaData['schema_version']=1
  Write-JsonAtomic $metaPath $metaData
  if(-not [IO.File]::Exists($session+'.meta')){ Write-JsonAtomic ($session+'.meta') $metaData }
  $reasonixHomePath=[IO.Path]::GetFullPath($ReasonixHome)
  $projectsPath=Join-Path $reasonixHomePath 'desktop-projects.json'
  if([IO.File]::Exists($projectsPath)){
    try{$projects=[IO.File]::ReadAllText($projectsPath,[Text.Encoding]::UTF8)|ConvertFrom-Json}
    catch{$projects=$null}
  }else{$projects=$null}
  if($null-eq$projects){$projects=[pscustomobject]@{projects=@()}}
  $entry=@($projects.projects|Where-Object{$_.root -eq $project})|Select-Object -First 1
  if($null-eq$entry){$entry=[pscustomobject]@{root=$project;topics=@()};$projects.projects=@($projects.projects)+$entry}
  if($entry.topics -notcontains $topic){$entry.topics=@($entry.topics)+$topic}
  Write-JsonAtomic $projectsPath $projects
  $tabsPath=Join-Path $reasonixHomePath 'desktop-tabs.json'
  if([IO.File]::Exists($tabsPath)){
    try{$tabs=[IO.File]::ReadAllText($tabsPath,[Text.Encoding]::UTF8)|ConvertFrom-Json}
    catch{$tabs=$null}
  }else{$tabs=$null}
  if($null-eq$tabs){$tabs=[pscustomobject]@{tabs=@();activeTab=''}}
  if(-not @($tabs.tabs|Where-Object{$_.sessionPath -eq $script:reasonixSession})){
    $tabId='tab_'+[Guid]::NewGuid().ToString('N')
    $tab=[pscustomobject]@{id=$tabId;scope='project';workspaceRoot=$project;topicId=$topic;sessionPath=$script:reasonixSession;model=[string]$metaData['model'];mode='yolo';toolApprovalMode='yolo'}
    $tabs.tabs=@($tabs.tabs)+$tab; $tabs.activeTab=$tabId; Write-JsonAtomic $tabsPath $tabs
  }
  $script:desktopState='registered'
  Save-Status 'running' 'executing' 'Reasonix Desktop session registered'
}
Resolve-ExecutionPlan
if([IO.File]::Exists($StatusPath)){
  try{ $prev=[IO.File]::ReadAllText($StatusPath,[Text.Encoding]::UTF8)|ConvertFrom-Json; $an=0; if($prev.PSObject.Properties['attemptNumber'] -and [int]::TryParse([string]$prev.attemptNumber,[ref]$an) -and $an -gt 0){ $script:attemptNumber=$an } }catch{}
}
$script:sessionRoot=Get-ProjectSessionRoot
$script:baseline=Get-SessionBaseline $script:sessionRoot
Save-Status 'starting' 'starting' 'Reading task contract'
$utf8=[Text.UTF8Encoding]::new($false); [IO.File]::WriteAllText($events,'',$utf8)
$lock=Join-Path ([IO.Path]::GetFullPath((Join-Path $project '.codex-helper\runs'))) '.reasonix.lock'; $stream=$null
try{
  $stream=[IO.File]::Open($lock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  Save-Status 'running' 'executing' 'Reasonix is executing the task'
  $workerChecksLine=if($script:workerChecks.Count -gt 0){"workerChecks:`n"+($script:workerChecks -join "`n")+"`n"}else{'Run every automatically verifiable acceptance criterion in ACCEPTANCE.md (worker scope).`n'}
  $reviewLine=if($script:allowAutoReview){'Automatic review/security-review subagents are permitted when the contract requires them.`n'}else{'Do not auto-start review, security-review, or explore subagents; GPT is the reviewer.`n'}
  $prompt="Read the task contract from $task (only SPEC.md, ACCEPTANCE.md, HANDOFF.md and manifest.json in the current task directory). Implement SPEC.md exactly within project root $project, satisfy ACCEPTANCE.md and HANDOFF.md, and write the execution report to $report. Do not redesign scope.`n"+
  "First list at most 5 concrete implementation actions, then implement them directly; read only the files HANDOFF names and their direct dependencies; do not scan unrelated parts of the repo.`n"+
  "Execution policy (soft budget, not a hard limit): intensity=$($script:planIntensity), profile=$($script:planProfile), effort=$($script:planEffort), estimated ~$($script:planBudget) steps.`n"+
  'GPT is the final reviewer; Reasonix performs implementation and workerChecks only.`n'+
  $workerChecksLine+
  'Run each workerCheck at most once; if a check already passed, do not re-run it. Do not iterate test/Release build back and forth.`n'+
  'gptChecks and releaseChecks (visual acceptance, full regression, packaging/release) belong to GPT or a later release phase; do not attempt them.`n'+
  $reviewLine+
  'If actual steps exceed the estimate, converge to the remaining acceptance items; never create extra experiment projects and never run publish/package/build-release without explicit authorization.`n'+
  'Do not read old runs or events under .codex-helper/runs, do not recursively scan bin/obj, do not re-read unchanged files, and do not re-run commands that already passed.'
  {{{permissionArgs}}}
  $runArgs=@('run','--dir',$project,'--profile',$script:planProfile,'--effort',$script:planEffort)
  if($null-ne$script:planMaxSteps){ $runArgs+=@('--max-steps',[string]$script:planMaxSteps) }
  $runArgs+=@('--events-jsonl','--metrics',$metrics,$prompt)
  $runArgs += $permissionArgs
  & '{{{executable.Replace("'", "''")}}}' @runArgs 2>$helperErr | ForEach-Object {
    $line=$_.ToString()
    [IO.File]::AppendAllText($events,$line+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
    $script:count++
    $obj=$null
    try { $obj=$line|ConvertFrom-Json } catch {}
    if($null-ne$obj){
      $kind=[string]$obj.kind
      if(-not [string]::IsNullOrWhiteSpace($kind)){ $script:lastEventKind=$kind }
      if($kind -eq 'turn_started'){ $script:stepCount++ }
      elseif($kind -eq 'tool_dispatch'){ $script:toolCallCount++; $tn=[string]$obj.tool_name; if([string]::IsNullOrWhiteSpace($tn)){ $tn=[string]$obj.tool }; if([string]::IsNullOrWhiteSpace($tn)){ $tn=[string]$obj.name }; if(-not [string]::IsNullOrWhiteSpace($tn) -and $tn.Length -le 64){ $script:lastToolName=$tn } }
      elseif($kind -eq 'tool_result'){ $tn=[string]$obj.tool_name; if([string]::IsNullOrWhiteSpace($tn)){ $tn=[string]$obj.tool }; if([string]::IsNullOrWhiteSpace($tn)){ $tn=[string]$obj.name }; if(-not [string]::IsNullOrWhiteSpace($tn) -and $tn.Length -le 64){ $script:lastToolName=$tn }; $ro=$obj.tool_read_only; if($null-ne$ro -and ([string]$ro) -match '^(false|False|0)$'){ $script:lastStage='implementing' } }
      elseif($kind -eq 'tool_call' -or $kind -eq 'tool_use'){ $tn=[string]$obj.tool_name; if([string]::IsNullOrWhiteSpace($tn)){ $tn=[string]$obj.tool }; if([string]::IsNullOrWhiteSpace($tn)){ $tn=[string]$obj.name }; if(-not [string]::IsNullOrWhiteSpace($tn) -and $tn.Length -le 64){ $script:lastToolName=$tn } }
      elseif($kind -eq 'reasoning'){ $script:reasoningCount++ }
      elseif($kind -eq 'run_done'){ $okVal=$obj.ok; $ok=$true; if($null-ne$okVal -and ([string]$okVal) -match '^(false|False|0)$'){ $ok=$false }; if(-not $ok){ $script:modelRunFailed=$true; $script:lastStage='blocked' } }
      elseif($kind -eq 'usage' -and $null-ne$obj.usage){
        $script:tokenInput+= [long]($obj.usage.input_tokens)
        $script:tokenOutput+= [long]($obj.usage.output_tokens)
        $script:cacheHit+= [long]($obj.usage.cache_hit_tokens)
      }
    }
    if([string]::IsNullOrWhiteSpace($script:reasonixSession)){ Register-DesktopSession }
    if(($script:count % 50) -eq 0 -or $script:stepCount -ne $script:lastSavedSteps){ $script:lastSavedSteps=$script:stepCount; Save-Status 'running' 'executing' ("Processed $($script:count) events") }
    if($script:planBudget -gt 0){
      if(-not $script:budgetWarningNotified -and $script:stepCount -ge $script:planBudget){
        $script:budgetWarningNotified=$true
        [IO.File]::AppendAllText($events,'{"kind":"helper_budget_notice","state":"warning","message":"Reached estimated step budget; converge to remaining acceptance items."}'+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
        Save-Status 'running' 'executing' "Budget warning: reached ~$($script:planBudget) steps; converge to remaining acceptance items"
      }
      elseif(-not $script:budgetExceededNotified -and $script:stepCount -ge [int]($script:planBudget*1.5)){
        $script:budgetExceededNotified=$true
        [IO.File]::AppendAllText($events,'{"kind":"helper_budget_notice","state":"exceeded","message":"Exceeded 150% of estimated step budget; converge to remaining acceptance items."}'+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
        Save-Status 'running' 'executing' "Budget exceeded: >150% of ~$($script:planBudget) steps; converge to remaining acceptance items"
      }
    }
  }
  $exit=$LASTEXITCODE
  $reportExists=[IO.File]::Exists($report)
  $metricsText=if([IO.File]::Exists($metrics)){[IO.File]::ReadAllText($metrics)}else{'not generated'}
  if([IO.File]::Exists($metrics)){
    try{
      $m=[IO.File]::ReadAllText($metrics)|ConvertFrom-Json
      $raw=0
      if($null-ne$m -and $m.PSObject.Properties['steps'] -and [int]::TryParse([string]$m.steps,[ref]$raw) -and $raw -ge 0){ $script:finalSteps=$raw }
    }catch{}
  }
  # 最终预算以 metrics.json.steps 为准重算（运行中的模型轮次仅作估计提醒，绝不覆盖最终值）。
  if($script:finalSteps -ge 0 -and $script:planBudget -gt 0){
    $script:budgetFinalReady=$true
    if($script:finalSteps -lt $script:planBudget){ $script:finalBudgetState='within' }
    elseif($script:finalSteps -lt [int]($script:planBudget*1.5)){ $script:finalBudgetState='warning' }
    else { $script:finalBudgetState='exceeded' }
    $script:finalOverrun=[int][Math]::Max(0,$script:finalSteps-$script:planBudget)
  }
  $finalDisplay=if($script:finalSteps -ge 0){$script:finalSteps}else{'n/a'}
  $budgetLine="`n- Budget: $($script:planBudget) steps (soft, not a hard limit); final metrics steps=$finalDisplay; overrun=$(if($script:budgetFinalReady){$script:finalOverrun}else{'n/a'}); BudgetState=$(if($script:budgetFinalReady){$script:finalBudgetState}else{'n/a'})"
  $packet=@"
# GPT Review Packet

- Task: $taskId
- Attempt number: $($script:attemptNumber)
- Reasonix exit code: $exit
- Execution report exists: $reportExists
- Event count: $script:count
- Model turns (turn_started): $script:stepCount
- Final metrics steps: $(if($script:finalSteps -ge 0){$script:finalSteps}else{'n/a'})
- Tool calls: $script:toolCallCount
- Reasoning events: $script:reasoningCount
- Tokens: input=$script:tokenInput output=$script:tokenOutput cache_hit=$script:cacheHit
- Policy: intensity=$script:planIntensity profile=$script:planProfile effort=$script:planEffort budget=$script:planBudget source=$script:planSource
- Model: $script:reasonixModel
$budgetLine
- Project: $project
- Task directory: $task

## Metrics

$metricsText

GPT must read EXECUTION_REPORT.md, inspect actual changes, and independently rerun acceptance checks.
"@
  [IO.File]::WriteAllText($review,$packet,[Text.UTF8Encoding]::new($false))
  Register-DesktopSession
  # 失败分类（B2）：绝不假装知道测试失败；脱敏摘要，不含完整 stderr/命令/正文/秘密。
  $script:failureKind=$null; $script:failureSummary=$null
  if($reportExists){
    if($exit -ne 0){ $script:failureKind='cli-exit'; $script:failureSummary="Reasonix exited $exit but EXECUTION_REPORT.md exists." }
  } else {
    if($script:modelRunFailed){ $script:failureKind='model-run-failed' }
    elseif($exit -ne 0){ $script:failureKind='cli-exit' }
    else { $script:failureKind='missing-report' }
    $script:failureSummary="No EXECUTION_REPORT.md; last stage=$($script:lastStage), last event=$($script:lastEventKind), last tool=$($script:lastToolName)."
    Write-FailureReport $script:failureKind $script:failureSummary $exit
  }
  if($reportExists){
    if($exit -eq 0){
      $script:returnState='same-turn-resume'
      Save-Status 'completed' 'awaiting-gpt-review' ('Reasonix completed; GPT can review. Desktop: '+$script:desktopDiagnostic)
    } else {
      $script:returnState='executor-error'
      Save-Status 'failed' 'awaiting-gpt-review' ("Reasonix exited with code $exit but delivered EXECUTION_REPORT.md; GPT can review. Desktop: "+$script:desktopDiagnostic)
    }
  } else {
    if($exit -eq 0){
      $script:returnState='same-turn-resume'
      Save-Status 'failed' 'failed' 'Reasonix exited 0 but EXECUTION_REPORT.md was not produced'
    } else {
      $script:returnState='executor-error'
      Save-Status 'failed' 'failed' ("Reasonix exit code $exit; no EXECUTION_REPORT.md")
    }
  }
}
catch{ 
  $script:failureKind='host-error'; $script:failureSummary='Reasonix host caught an exception before completing.'
  $errDetail=$_.Exception.Message
  try{ if([IO.File]::Exists($helperErr)){ $stderrText=[IO.File]::ReadAllText($helperErr,[Text.Encoding]::UTF8); if(-not [string]::IsNullOrWhiteSpace($stderrText)){ $errDetail=$errDetail+' | stderr: '+$stderrText.Trim() } } }catch{}
  Save-Status 'failed' 'error' ($errDetail+' | trace: '+$_.ScriptStackTrace)
}
finally{ if($null-ne$stream){$stream.Dispose()} }
""";
    }

    private static string RemoveMarkedBlock(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0) return text;
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        if (endIndex < 0) return text[..startIndex];
        return text.Remove(startIndex, endIndex + end.Length - startIndex);
    }

    private static string FirstUsefulLine(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;

    public static JsonDocument ParseLenientWindowsJson(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException)
        {
            // Reasonix 1.19.1 can emit raw Windows paths such as C:\code in
            // `doctor --json`. Escape only invalid backslashes inside strings;
            // preserve valid JSON escapes and all content outside strings.
            var repaired = new StringBuilder(text.Length + 32);
            var inString = false;
            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                if (current == '"')
                {
                    var precedingBackslashes = 0;
                    for (var scan = index - 1; scan >= 0 && text[scan] == '\\'; scan--) precedingBackslashes++;
                    if (precedingBackslashes % 2 == 0) inString = !inString;
                    repaired.Append(current);
                    continue;
                }
                if (inString && current == '\\')
                {
                    var next = index + 1 < text.Length ? text[index + 1] : '\0';
                    if (next is not ('"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')) repaired.Append('\\');
                }
                repaired.Append(current);
            }
            return JsonDocument.Parse(repaired.ToString());
        }
    }

    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var result = new ProcessResult(process.ExitCode, await stdout, await stderr);
        if (!allowFailure && result.ExitCode != 0) throw new InvalidOperationException(FirstUsefulLine(result.StdErr));
        return result;
    }

    private sealed record IntegrationState(bool Enabled, string ExecutablePath, string DefaultModel, ReasonixPermissionMode? PermissionMode);
    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
