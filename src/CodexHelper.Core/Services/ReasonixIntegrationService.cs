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
    bool IntegrationEnabled,
    string Source = "",
    string ProtocolCompatibility = "",
    string? DoctorWarning = null,
    string? DiscoveryNote = null);

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
    string? ProgressSource = null,
    int? RemainingPercent = null,
    string? CurrentCheck = null,
    string? ContractDiagnostic = null,
    bool? ContractNormalized = null)
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
    /// <summary>视觉验收职责边界：Reasonix 不截图/不看图/不作视觉结论，视觉验收归 GPT。</summary>
    public const string VisualBoundaryRule =
        "Reasonix must never take screenshots, view images, or draw visual conclusions (no desktop screenshot, PrintWindow, BitBlt, RenderTargetBitmap, off-screen render capture, or pixel analysis, and no swapping screen-capture approaches). All screenshots, DPI, layout, color, occlusion and visual acceptance belong to GPT; if GPT lacks image tools it must honestly mark \"visual not verified\" instead of returning the task to Reasonix for repeated attempts. GUI smoke testing runs at most once; if the environment blocks it, record the fact and continue — never do graphics-environment diagnosis. If workerChecks wrongly includes a screenshot or visual item, skip it and hand it to GPT.";
    private readonly string codexRoot;
    private readonly AppPaths paths;
    private readonly string skillDirectory;
    private readonly string statePath;

    /// <summary>候选发现器工厂（测试注入用）；默认真实发现。</summary>
    public Func<ReasonixCliDiscovery>? DiscoveryFactory { get; init; }

    /// <summary>能力探测器工厂（测试注入用）；默认真实探测。</summary>
    public Func<ReasonixCliProbe>? ProbeFactory { get; init; }

    public ReasonixIntegrationService(string codexRoot, AppPaths paths)
    {
        this.codexRoot = Path.GetFullPath(codexRoot);
        this.paths = paths;
        skillDirectory = Path.Combine(this.codexRoot, "skills", "reasonix-executor");
        statePath = Path.Combine(paths.BaseDirectory, "reasonix-integration.json");
    }

    /// <summary>发现并择优选择 Reasonix CLI（同步包装；探测有超时上限，损坏候选不阻断其他候选）。</summary>
    public string FindExecutable()
        => DiscoverBestAsync(CancellationToken.None).GetAwaiter().GetResult().Best?.Path ?? string.Empty;

    /// <summary>
    /// 多来源候选发现 + 能力探测 + 评分选优。Saved 路径有效（探测可用且兼容）时优先；
    /// 已保存路径被删除或不再兼容时自动重新发现并迁移，诊断中说明；npm shim 永远兜底。
    /// 迁移仅在“保存路径确实应被替换且新候选兼容”时原子持久化（保留 Enabled/DefaultModel/
    /// PermissionMode），启用协作时把托管脚本刷新到新 CLI，且绝不递归触发再次探测；
    /// 无可用兼容候选时保持旧状态不变。
    /// </summary>
    public Task<ReasonixCliSelection> DiscoverBestAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => DiscoverBestCoreAsync(cancellationToken), cancellationToken);

    private async Task<ReasonixCliSelection> DiscoverBestCoreAsync(CancellationToken cancellationToken)
    {
        var discovery = DiscoveryFactory?.Invoke() ?? new ReasonixCliDiscovery();
        var probe = ProbeFactory?.Invoke() ?? new ReasonixCliProbe();
        var state = LoadState();
        var savedPath = state.ExecutablePath;
        var candidates = discovery.Discover(savedPath);
        var selection = await probe.SelectBestAsync(candidates, savedPath, cancellationToken);
        PersistMigratedPathIfNeeded(selection, state);
        return selection;
    }

    /// <summary>
    /// 保存路径应被替换时原子更新 ExecutablePath。判定规则（与择优迁移注释一致）：仅当
    /// 保存路径被删除、探测完全失败（损坏）或保存的是 npm 旧版 shim 时，才迁移到更兼容的候选。
    /// 无可用兼容候选时绝不改变旧状态。迁移后若协作已启用，把托管脚本刷新到新 CLI。
    /// </summary>
    private void PersistMigratedPathIfNeeded(ReasonixCliSelection selection, IntegrationState state)
    {
        var savedPath = state.ExecutablePath;
        if (!ShouldMigrateSavedPath(selection, savedPath)) return;
        var newPath = selection.Best!.Path;
        paths.EnsureCreated();
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state with { ExecutablePath = newPath }, new JsonSerializerOptions { WriteIndented = true }));
        if (state.Enabled) RefreshManagedScripts(newPath);
    }

    /// <summary>保存路径确实应被替换（非空、已切换到不同候选、且新候选兼容）且存在触发条件。</summary>
    private static bool ShouldMigrateSavedPath(ReasonixCliSelection selection, string? savedPath)
    {
        if (string.IsNullOrWhiteSpace(savedPath)) return false;
        var best = selection.Best;
        if (best is null || !best.ProbeOk || !best.HasConfig || !best.HasProviders) return false;
        if (string.Equals(PathUtil.GetFullPathSafe(best.Path), PathUtil.GetFullPathSafe(savedPath), StringComparison.OrdinalIgnoreCase)) return false;
        if (selection.SavedPathMissing) return true;
        var saved = selection.Candidates.FirstOrDefault(c => string.Equals(PathUtil.GetFullPathSafe(c.Path), PathUtil.GetFullPathSafe(savedPath), StringComparison.OrdinalIgnoreCase));
        if (saved is null) return false;
        return !saved.ProbeOk || saved.Source == ReasonixCliSource.Npm;
    }

    public async Task<ReasonixStatus> DiagnoseAsync(CancellationToken cancellationToken = default, ReasonixCliSelection? precomputedSelection = null)
    {
        var selection = precomputedSelection ?? await DiscoverBestAsync(cancellationToken);
        var executable = selection.Best?.Path;
        var integrationEnabled = IsEnabled();
        if (string.IsNullOrWhiteSpace(executable))
        {
            var reason = selection.SavedPathMissing
                ? "已保存的 Reasonix CLI 路径不存在，且未发现其他可用候选。"
                : "未找到 Reasonix CLI。请安装 Reasonix Desktop，或在协作开发页手动选择 CLI 文件。";
            return new(false, string.Empty, string.Empty, string.Empty, false, reason, integrationEnabled, DiscoveryNote: selection.DiscoveryNote);
        }

        var source = selection.Best!.Source;
        var version = selection.Best.Version;
        // 复用预计算 selection 中已探测的 doctor 结果，绝不再次启动进程（UI 一次刷新收敛到一次探测）。
        var precomputed = precomputedSelection is not null && precomputedSelection.Best is not null;
        var doctorJson = string.Empty;
        var doctorExitCode = 0;
        var doctorOutputSummary = string.Empty;
        if (precomputed)
        {
            doctorJson = selection.Best!.DoctorJson ?? string.Empty;
            doctorExitCode = selection.Best.DoctorExitCode;
        }
        else
        {
            var doctor = await RunAsync(executable, ["doctor", "--json"], cancellationToken, allowFailure: true, timeout: TimeSpan.FromSeconds(10));
            doctorJson = ReasonixCliProbe.CleanDoctorOutput(doctor.StdOut);
            doctorExitCode = doctor.ExitCode;
            doctorOutputSummary = DescribeOutputSummary(doctor);
        }
        using var json = ReasonixCliProbe.TryParseJson(doctorJson);
        var hasConfig = json is not null && json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("config", out _);
        var hasProviders = json is not null && json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Array;

        // 即使 exit code 非零，也先尝试解析 stdout 中的有效 JSON；config/providers 可用则继续。
        if (json is null)
        {
            var error = BuildDoctorError(executable, version, doctorExitCode, doctorOutputSummary);
            return new(true, executable, version, string.Empty, false, error, integrationEnabled, ReasonixCliProbe.DescribeSource(source), "unknown", DiscoveryNote: selection.DiscoveryNote);
        }

        var defaultModel = string.Empty;
        var credentialReady = false;
        var message = string.Empty;
        if (hasConfig)
        {
            defaultModel = json.RootElement.GetProperty("config").TryGetProperty("default_model", out var model)
                ? model.GetString() ?? string.Empty
                : string.Empty;
        }
        var providerName = defaultModel.Split('/', 2)[0];
        if (hasProviders)
        {
            var activeProvider = json.RootElement.GetProperty("providers").EnumerateArray()
                .FirstOrDefault(item => item.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), providerName, StringComparison.OrdinalIgnoreCase));
            credentialReady = activeProvider.ValueKind != JsonValueKind.Undefined
                && activeProvider.TryGetProperty("key_present", out var present)
                && present.ValueKind == JsonValueKind.True;
            message = credentialReady
                ? "默认模型凭据已保存；是否有效请以“测试 Reasonix 连接”的结果为准。"
                : $"默认模型 {defaultModel} 缺少凭据，请先在 Reasonix 中重新配置。";
        }
        else
        {
            // 旧版 doctor JSON 不含 providers：给出明确不兼容提示，而不是空白错误。
            message = string.IsNullOrWhiteSpace(defaultModel)
                ? "Reasonix 诊断返回的 JSON 不含 providers，协议不兼容（疑似旧版 Reasonix）。请改用 Reasonix Desktop。"
                : $"Reasonix 诊断返回的 JSON 不含 providers，协议不兼容（疑似旧版 Reasonix）；当前默认模型 {defaultModel} 无法核对凭据。请改用 Reasonix Desktop。";
        }

        var doctorWarning = doctorExitCode != 0
            ? $"doctor 以退出码 {doctorExitCode} 结束，但诊断 JSON 仍可用；{doctorOutputSummary}"
            : null;
        return new(true, executable, version, defaultModel, credentialReady, message, integrationEnabled,
            ReasonixCliProbe.DescribeSource(source),
            hasConfig && hasProviders ? "compatible" : "legacy",
            doctorWarning,
            selection.DiscoveryNote);
    }

    /// <summary>doctor 完全不可用时的非空错误：含路径、版本（可得时）、退出码与脱敏输出摘要。</summary>
    private static string BuildDoctorError(string executable, string version, int doctorExitCode, string outputSummary)
    {
        var versionText = string.IsNullOrWhiteSpace(version) ? string.Empty : $"，版本 {version}";
        return $"Reasonix 诊断失败：{executable}{versionText}（退出码 {doctorExitCode}）{outputSummary}。";
    }

    /// <summary>stdout/stderr 脱敏摘要；两者皆空时给出明确说明，绝不返回空白错误。</summary>
    private static string DescribeOutputSummary(ProcessResult doctor)
    {
        var stdout = RedactSecrets(doctor.StdOut).Trim();
        var stderr = RedactSecrets(doctor.StdErr).Trim();
        var stdoutSummary = string.IsNullOrWhiteSpace(stdout) ? string.Empty : " 输出：" + Truncate(stdout, 160);
        var stderrSummary = string.IsNullOrWhiteSpace(stderr) ? string.Empty : " 错误：" + Truncate(stderr, 160);
        return stdoutSummary + stderrSummary;
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>诊断文本脱敏：替换 API Key、JWT、token、密码等疑似敏感字段，避免泄露凭据。</summary>
    public static string RedactSecrets(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return SecretRegex.Replace(text, "***");
    }

    private static readonly System.Text.RegularExpressions.Regex SecretRegex = new(
        @"(sk-[A-Za-z0-9_-]{8,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}|(?:api[_-]?key|token|password|secret|authorization)\s*[:=]\s*(?:[""'][^""']{4,}[""']|[^\s""',;]{8,})|Bearer\s+[A-Za-z0-9._~+/=-]{8,})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

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

    public async Task<IReadOnlyList<ReasonixModelOption>> GetAvailableModelsAsync(CancellationToken cancellationToken = default, ReasonixCliSelection? precomputedSelection = null)
    {
        var selection = precomputedSelection ?? await DiscoverBestAsync(cancellationToken);
        var executable = selection.Best?.Path;
        if (string.IsNullOrWhiteSpace(executable))
            throw new FileNotFoundException("未找到 Reasonix CLI。" + (selection.DiscoveryNote is null ? string.Empty : selection.DiscoveryNote));
        // 复用预计算 selection 中已探测的 doctor 结果，绝不再次启动进程（UI 一次刷新收敛到一次探测）。
        string doctorJson;
        int doctorExitCode;
        string outputSummary;
        if (precomputedSelection is not null)
        {
            doctorJson = selection.Best!.DoctorJson ?? string.Empty;
            doctorExitCode = selection.Best.DoctorExitCode;
            outputSummary = string.Empty;
        }
        else
        {
            var doctor = await RunAsync(executable, ["doctor", "--json"], cancellationToken, allowFailure: true, timeout: TimeSpan.FromSeconds(10));
            doctorJson = ReasonixCliProbe.CleanDoctorOutput(doctor.StdOut);
            doctorExitCode = doctor.ExitCode;
            outputSummary = DescribeOutputSummary(doctor);
        }
        // 即使 exit code 非零，也先尝试解析 stdout 中的有效 JSON；providers 可用则返回模型。
        using var json = ReasonixCliProbe.TryParseJson(doctorJson);
        if (json is null || !json.RootElement.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
        {
            var version = selection.Best!.Version;
            var versionText = string.IsNullOrWhiteSpace(version) ? string.Empty : $"，版本 {version}";
            throw new InvalidOperationException($"无法读取 Reasonix 模型列表：{executable}{versionText}（退出码 {doctorExitCode}）{outputSummary}。Reasonix 协议不兼容或 doctor 输出无效。");
        }
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

    /// <summary>
    /// 手动选择 CLI：先验证（文件存在 + 版本/doctor 探测），成功后才持久化；
    /// 验证失败抛可恢复异常（含路径与原因），不改变任何状态。启用状态下切换后
    /// 立即刷新托管脚本到新路径。
    /// </summary>
    public async Task<ReasonixCliSelection> SelectCliAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("请选择 Reasonix CLI 文件。", nameof(executablePath));
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("所选文件不存在，请重新选择。", fullPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(fullPath), ".cmd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(fullPath), ".bat", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选文件不是可执行的 Reasonix CLI（请选择 .exe、.cmd 或 .bat 文件）。");

        var probe = ProbeFactory?.Invoke() ?? new ReasonixCliProbe();
        var result = await probe.ProbeAsync(new ReasonixCliCandidate(fullPath, ReasonixCliSource.Saved), cancellationToken);
        if (!result.ProbeOk)
            throw new InvalidOperationException($"所选文件无法作为 Reasonix CLI 使用：{fullPath}。{result.Error}");

        // 验证通过后持久化（原子写），取消/失败不改状态。
        var state = LoadState();
        paths.EnsureCreated();
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state with { ExecutablePath = fullPath }, new JsonSerializerOptions { WriteIndented = true }));
        if (state.Enabled) RefreshManagedScripts();

        var selection = new ReasonixCliSelection(result, [result], fullPath, false, "已手动指定 CLI：" + fullPath);
        return selection;
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

    public void RefreshManagedScripts(string? executable = null)
    {
        var state = LoadState();
        if (!state.Enabled) return;
        // 传入已算好的 CLI 路径时不重新探测（迁移/UI 复用场景避免递归探测）；
        // 未提供时才回退到 FindExecutable（保持既有无参语义）。
        if (string.IsNullOrWhiteSpace(executable)) executable = FindExecutable();
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
                // 先读原文区分“空文件”与“损坏内容”，再用统一宽容读取解析（损坏/日期非法 → null）。
                var text = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    diagnostics.Add(new(Path.GetFileName(file), "状态文件内容为空"));
                    continue;
                }
                var status = ReasonixStatusJson.TryReadStatus(file);
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
                    // P0-3 终态归一化（内存级）：完成/失败/取消/中断等非运行状态剩余百分比归零、阶段归一。
                    status = ReasonixStatusJson.NormalizeTerminalState(status);
                    // P0-1 漏报告自动恢复：missing-report 且证据充分时由 Helper 生成自动恢复报告并置为等待 GPT 验收。
                    if (IsMissingReportCandidate(status))
                    {
                        var recovered = TryAutoRecoverMissingReport(status);
                        if (recovered is not null) status = recovered;
                    }
                    tasks.Add(status);
                    if (tasks.Count >= wanted) break;
                }
                else diagnostics.Add(new(Path.GetFileName(file), "JSON 语法或日期格式无效"));
            }
            catch (Exception ex)
            {
                // 单个损坏文件不得让整个列表失效；改为可见、可诊断的摘要。
                diagnostics.Add(new(Path.GetFileName(file), DescribeStatusFileError(ex)));
            }
        }
        return new ReasonixTasksSnapshot(tasks.OrderByDescending(item => item.UpdatedUtc).ToList(), diagnostics);
    }

    /// <summary>
    /// 从任务目录的 manifest.json 安全读取 workerChecks 步骤列表（仅字符串项，按声明顺序）。
    /// manifest 缺失、损坏、无 workerChecks、非数组、越界或无权限时一律安全降级为空列表，
    /// 绝不抛异常，也不影响既有任务摘要。
    /// </summary>
    public IReadOnlyList<string> ReadWorkerChecks(ReasonixTaskStatus task)
    {
        if (task is null || string.IsNullOrWhiteSpace(task.TaskDirectory) || string.IsNullOrWhiteSpace(task.ProjectRoot)) return [];
        try
        {
            var project = Path.GetFullPath(task.ProjectRoot);
            var taskDir = Path.GetFullPath(task.TaskDirectory);
            var runs = Path.Combine(project, ".codex-helper", "runs");
            if (!PathSafety.IsWithin(taskDir, runs)) return [];
            var manifestPath = Path.Combine(taskDir, "manifest.json");
            if (!File.Exists(manifestPath)) return [];
            using var document = JsonDocument.Parse(
                File.ReadAllText(manifestPath, Encoding.UTF8),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!document.RootElement.TryGetProperty("workerChecks", out var checks)) return [];
            if (checks.ValueKind != JsonValueKind.Array) return [];
            return checks.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim())
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>是否为漏报告失败候选（runner 仅在该场景设置 missing-report：退出码 0、非模型失败、无报告）。</summary>
    private static bool IsMissingReportCandidate(ReasonixTaskStatus status)
        => status is not null
           && string.Equals(status.State, "failed", StringComparison.OrdinalIgnoreCase)
           && string.Equals(status.FailureKind, "missing-report", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// P0-1 漏报告自动恢复（Helper 侧）：仅当 runner 记录了 missing-report 证据且满足
    /// <see cref="ReasonixAutoRecovery.ShouldRecover"/>（退出码 0 + 实际活动 + 本次新增变化或已通过检查）
    /// 时，由 Helper 原子生成自动恢复版 EXECUTION_REPORT.md 与 REVIEW_PACKET.md，并把任务状态置为
    /// 等待 GPT 验收的完成态。绝不伪造测试通过；无活动/无变化/模型失败/非零退出均不恢复。
    /// 恢复是幂等的：恢复后 State 变为 completed，不再重复触发。
    /// </summary>
    public ReasonixTaskStatus? TryAutoRecoverMissingReport(ReasonixTaskStatus task)
    {
        try
        {
            if (!IsMissingReportCandidate(task)) return null;
            var evidence = ReasonixAutoRecovery.TryLoadEvidence(task.TaskDirectory);
            if (!ReasonixAutoRecovery.ShouldRecover(task, evidence)) return null;
            var recovered = ReasonixAutoRecovery.BuildRecoveredStatus(task, evidence!);
            ReasonixAutoRecovery.WriteReports(task, evidence!);
            ReasonixStatusJson.WriteStatus(Path.Combine(paths.ReasonixTasksDirectory, task.TaskId + ".json"), recovered);
            return recovered;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从 PROGRESS.json 宽容读取已通过（passed）的 workerChecks 名称（排除视觉/GUI/发布等已移交 GPT 的项）。
    /// 文件缺失、损坏、字段非法一律返回空列表，绝不抛异常。
    /// </summary>
    private static IReadOnlyList<string> ReadPassedWorkerChecks(string progressPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(progressPath) || !File.Exists(progressPath)) return [];
            using var document = JsonDocument.Parse(
                File.ReadAllText(progressPath, Encoding.UTF8),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!document.RootElement.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array) return [];
            var result = new List<string>();
            foreach (var item in checks.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("status", out var status)
                    || !string.Equals(status.GetString(), "passed", StringComparison.OrdinalIgnoreCase)) continue;
                if (!item.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString())) continue;
                var check = name.GetString()!.Trim();
                if (ReasonixAcceptanceFilter.ShouldDelegateToGpt(check)) continue;
                result.Add(check);
            }
            return result;
        }
        catch { return []; }
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
        ReasonixStatusJson.WriteStatus(Path.Combine(paths.ReasonixTasksDirectory, task.TaskId + ".json"), stopped);
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
        // P0-2 连续失败熔断：同一任务 missing-report 或 model-run-failed 累计达到 2 次（含当前尝试与
        // attempts/ 归档）后不再允许一键无脑重试；用户主动停止（user-stopped/cancelled）不计入。
        var breakerCount = CountCircuitBreakerFailures(task);
        if (breakerCount >= 2)
            return $"该任务已连续 {breakerCount} 次因缺少交付报告或模型运行失败（missing-report / model-run-failed），已触发重试熔断；请先检查任务合同、Reasonix 模型配置与失败日志，确认原因后再重试（用户主动停止不计入）。";
        return null;
    }

    /// <summary>
    /// P0-2 熔断计数：统计当前尝试与 attempts/ 归档中 failureKind 为 missing-report 或 model-run-failed
    /// 的次数（不区分大小写）；用户主动停止（user-stopped/cancelled）与其他失败类型不计入。
    /// </summary>
    public int CountCircuitBreakerFailures(ReasonixTaskStatus task)
    {
        if (task is null) return 0;
        var count = 0;
        if (IsCircuitBreakerFailure(task.FailureKind)) count++;
        try
        {
            var attemptsDir = Path.Combine(Path.GetFullPath(task.TaskDirectory), "attempts");
            if (!Directory.Exists(attemptsDir)) return count;
            foreach (var directory in Directory.EnumerateDirectories(attemptsDir))
            {
                var archived = ReasonixStatusJson.TryReadStatus(Path.Combine(directory, "status.json"));
                if (archived is not null && IsCircuitBreakerFailure(archived.FailureKind)) count++;
            }
        }
        catch { }
        return count;
    }

    private static bool IsCircuitBreakerFailure(string? failureKind)
        => string.Equals(failureKind, "missing-report", StringComparison.OrdinalIgnoreCase)
           || string.Equals(failureKind, "model-run-failed", StringComparison.OrdinalIgnoreCase);

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
            ReasonixStatusJson.WriteStatus(Path.Combine(archiveDir, "status.json"), task);
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
            // P1-7 避免重复验收：RETRY_CONTEXT 明确记录归档 PROGRESS.json 中已通过的 workerChecks
            //（排除视觉/GPT 项），后续只运行未完成检查，不重复构建或重复视觉检查。
            var passedChecks = ReadPassedWorkerChecks(Path.Combine(archiveDir, "PROGRESS.json"));
            var passedLine = passedChecks.Count > 0 ? string.Join("、", passedChecks.Take(10)) : "（归档内无已通过记录）";
            var context = $$"""
# RETRY_CONTEXT.md

从当前源码继续收尾本次 Reasonix 任务。优先读取归档内失败摘要与 WORKER_ACCEPTANCE.md 中未满足的 workerChecks，聚焦剩余范围收敛；不要重新分析已完成的部分，不要重跑已通过的检查，也不要读取 ACCEPTANCE.md。完成后只写 EXECUTION_REPORT.md，不写 REVIEW_PACKET.md（由 Helper 自动生成）。

- 任务：{{task.TaskId}}
- 本次尝试编号：{{newAttempt}}
- 项目根：{{task.ProjectRoot}}
- 原 CodexThreadId：{{task.CodexThreadId ?? "-"}}
- 失败报告：attempts/{{archiveName}}/FAILURE_REPORT.md
{{(hasFailureReport ? "" : "（旧尝试未生成 FAILURE_REPORT.md 时可结合归档内 status.json 的状态信息。）")}}
- 已通过 workerChecks（归档 PROGRESS.json，已排除视觉/GPT 项）：{{passedLine}}

> 后续只运行尚未完成的检查；已通过检查不重复执行，不重复构建，不重复视觉检查。
""";
            AtomicFile.WriteAllText(Path.Combine(taskDir, "RETRY_CONTEXT.md"), context);

            // 4) 递增 AttemptNumber 并置为 starting（同时使其不再是可重试状态，从而防双击/并发）。
            // 新 attempt 重新初始化剩余百分比（单调保护从 0 起点重新计算）。
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
                RemainingPercent = null,
                HostProcessId = 0
            };
            var statusPath = Path.Combine(paths.ReasonixTasksDirectory, task.TaskId + ".json");
            ReasonixStatusJson.WriteStatus(statusPath, next);

            // 5) 启动 runner 并回写启动 PID（starting 状态下提供可靠进程绑定）。
            var runner = Path.Combine(skillDirectory, "invoke-reasonix.ps1");
            var pid = await Task.Run(() => StartRunner(runner, task.ProjectRoot, taskDir, task.CodexThreadId), cancellationToken);
            if (pid <= 0) throw new InvalidOperationException("启动托管脚本失败。");
            try
            {
                var latest = ReasonixStatusJson.TryReadStatus(statusPath) ?? next;
                ReasonixStatusJson.WriteStatus(statusPath, latest with { HostProcessId = pid });
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
            ReasonixStatusJson.WriteStatus(statusPath, task);
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
For implementation tasks that change project files, GPT is the planner and judge. Route implementation by task scale (3.4.1, three tiers; defaults, not security-authorization expansion): (1) GPT implements micro tasks directly — no more than 2 files and about 80 effective changed lines, no new cross-module/public interface, no security/credentials/data-migration/backup-restore/concurrency-coordination/install-upgrade/shared-runner/core-config involvement, and reliably verifiable with one focused test; typical: copy, styling, small UI, test assertions, simple clear bug. (2) Route to a single Reasonix contract when any holds: at least 3 files or more than about 80 lines; a full new feature, cross-module interface, or heavy code reading; a high-risk domain; multiple rounds of implement-and-test; or the user explicitly requests Reasonix/DeepSeek. (3) Reasonix limited parallel only for medium/large tasks containing at least two independent modules whose interfaces are frozen, write sets do not overlap, no rewiring is needed, and merge is purely mechanical; otherwise fall back to a single contract. Boundaries: if a GPT micro-fix crosses a threshold mid-way, stop expanding and convert to a Reasonix contract; after Reasonix finishes the main body, only acceptance-stage fixes within 2 files/80 lines and low-risk are done directly by GPT, without starting a new Reasonix. GPT must create a unique task directory under `<project>/.codex-helper/runs/run-<timestamp>-<guid>/` containing `SPEC.md`, `ACCEPTANCE.md`, `HANDOFF.md`, and `manifest.json`. Then invoke the `reasonix-executor` skill runner with only the absolute project root and task directory and **no command timeout** (the host waits indefinitely, without polling, until Reasonix exits; there is no task-duration limit). Do not configure a fixed one-hour or any other finite timeout for this command; only a user-initiated Stop in Codex Helper or closing Codex should end it. The same GPT turn resumes and performs acceptance after the runner returns. Do not poll the event log. Codex Helper shows a live event view; the session syncs to Reasonix Desktop once its session file appears. After the runner returns, inspect `REVIEW_PACKET.md` and the actual diff, then independently rerun only the focused acceptance checks affected by the changes (incremental acceptance); REVIEW_PACKET.md contains an acceptance scope suggestion (focused/full/release/security/visual) derived from the actual changed files — GPT follows it for incremental acceptance, and always runs the full regression for high-risk, release, security, or contract-mandated changes. GPT owns visual acceptance and gptChecks/releaseChecks; Reasonix performs implementation and workerChecks only, reads only `SPEC.md`/`HANDOFF.md`/`manifest.json`/`WORKER_ACCEPTANCE.md` (never `ACCEPTANCE.md`), and writes only `EXECUTION_REPORT.md` (`REVIEW_PACKET.md` is generated by Helper, not by Reasonix). HANDOFF.md must explicitly state allowed-read files, allowed-write files, and direct dependencies, and must forbid recursive scanning once the goal is clear. Execution intensity (auto/fast/standard/strict) is declared in manifest.json or inferred from the contract scope; Fast/Standard never auto-start review subagents; DeepSeek default effort is low except Strict/major (high). The manifest.json budget fields are `budgetSteps` (soft budget) and `maxSteps` (hard cap); `estimatedSteps` is not supported. Do not use Codex native subagents. Pure questions and read-only reviews stay in GPT. Parallel collaboration: implementation tasks should first be smartly split; tasks that are independent and have non-overlapping write sets may run in parallel, up to the configured max concurrency (1..3); tasks sharing public files, depending on each other, or touching dirty files must run serially; Reasonix executes and GPT merges and accepts. Never force every request to run in parallel — evaluate independence first. Mechanical merge rule (3.4.0): only split an implementation task into parallel sub-contracts when the merge is purely mechanical — interfaces are frozen, no rewiring or shared UI/config/public entry is needed, and the sub-tasks declare they can be merged by Git/Helper; a task that still needs rewiring or has an unfrozen interface must run serially. Do not split when merging would require Reasonix to re-understand or re-encode. Merge conflict-free results mechanically via Git/Helper; only re-encode on real conflicts.
{{VisualBoundaryRule}}
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

    private static string BuildSkill() => $$"""
---
name: reasonix-executor
description: Execute an already planned implementation task through the managed Reasonix CLI runner. Use only after GPT has written SPEC.md, ACCEPTANCE.md, HANDOFF.md and manifest.json in a unique project-local run directory.
---

# Reasonix Executor

GPT remains planner and judge. This skill only launches Reasonix as the implementation hand.

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File invoke-reasonix.ps1 -ProjectRoot <absolute project root> -TaskDirectory <absolute run directory>`. The runner binds the newest active user Codex task automatically; pass `-CodexThreadId <uuid>` when the current thread id is explicitly available.
Never pass the task body as a command-line argument. Run the command with **no command timeout** (wait indefinitely; the host has no task-duration limit) and wait for it to return; this consumes no repeated GPT turns and must not be replaced by log polling. Do not configure a fixed one-hour or any other finite timeout for this command — only user-initiated Stop in Codex Helper or closing Codex should end it. Tell the user the task is visible in Codex Helper (live event view) and will be synced to Reasonix Desktop once its session file appears. After completion, read REVIEW_PACKET.md and EXECUTION_REPORT.md, inspect actual changed files, and independently rerun only the focused acceptance checks affected by the changes (incremental acceptance; REVIEW_PACKET.md includes an acceptance scope suggestion derived from the actual changed files — follow it, and always run the full regression for high-risk, release, security, or contract-mandated changes) in this same GPT turn. GPT owns visual acceptance and gptChecks/releaseChecks; Reasonix performs implementation and workerChecks only, reads only SPEC.md/HANDOFF.md/manifest.json/WORKER_ACCEPTANCE.md (never ACCEPTANCE.md), and writes only EXECUTION_REPORT.md (REVIEW_PACKET.md is generated by Helper, not by Reasonix). Execution intensity (auto/fast/standard/strict) comes from manifest.json or is inferred; Fast/Standard never auto-start review subagents; DeepSeek default effort is low except Strict/major (high). The manifest.json budget fields are `budgetSteps` (soft budget) and `maxSteps` (hard cap); `estimatedSteps` is not supported. Do not commit, push, reset, clean, delete important files, modify credentials, or install dependencies unless the user explicitly authorized it.
{{VisualBoundaryRule}}
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
            ? "$permissionArgs=@('--permission-mode','auto')"
            : "$permissionArgs=@('--permission-mode','acceptEdits','--allowed-tools','Bash(dotnet --info:*)','--allowed-tools','Bash(dotnet restore:*)','--allowed-tools','Bash(dotnet build:*)','--allowed-tools','Bash(dotnet test:*)','--allowed-tools','Bash(dotnet run:*)','--allowed-tools','Bash(dotnet publish:*)')";
        return $$$"""
param([Parameter(Mandatory=$true)][string]$ProjectRoot,[Parameter(Mandatory=$true)][string]$TaskDirectory,[Parameter(Mandatory=$true)][string]$StatusPath,[string]$CodexThreadId='',[Parameter(Mandatory=$true)][string]$ReasonixHome)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
$task=[IO.Path]::GetFullPath($TaskDirectory).TrimEnd('\')
$taskId=Split-Path $task -Leaf
$events=Join-Path $task 'events.jsonl'; $metrics=Join-Path $task 'metrics.json'; $report=Join-Path $task 'EXECUTION_REPORT.md'; $review=Join-Path $task 'REVIEW_PACKET.md'; $helperErr=Join-Path $task 'helper-stderr.txt'; $workerAccept=Join-Path $task 'WORKER_ACCEPTANCE.md'
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
$script:planBudget=80; $script:planMaxSteps=$null; $script:planSource='inferred'; $script:allowAutoReview=$false; $script:workerChecks=@(); $script:finalReadinessSeen=$false
$script:startedUtc=$started
$script:remainingPercent=$null; $script:currentCheck=''
$script:contractDiagnostic=$null; $script:contractNormalized=$false; $script:contractBlocked=$false; $script:contractBlockReason=$null
$script:gitBaseline=$null
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
    $currentCheck=''
    if($p.PSObject.Properties['currentCheck'] -and $null-ne$p.currentCheck){ $currentCheck=[string]$p.currentCheck; if($currentCheck.Length -gt 120){ $currentCheck=$currentCheck.Substring(0,120) } }
    $completed=-1; $total=-1
    if($p.PSObject.Properties['completedChecks'] -and $null-ne$p.completedChecks){ $completed=[int]$p.completedChecks }
    if($p.PSObject.Properties['totalChecks'] -and $null-ne$p.totalChecks){ $total=[int]$p.totalChecks }
    # 标准 checks 数组（名称、状态 pending/running/passed/failed）：只统计合法对象项；
    # passed 计数排除命中视觉/GUI/发布职责的项（Helper 绝不把视觉/GPT 检查计为 worker 完成）。
    if(($completed -lt 0 -or $total -lt 0) -and $p.PSObject.Properties['checks'] -and $p.checks -is [System.Array]){
      $validChecks=@($p.checks | Where-Object { $_ -is [System.Management.Automation.PSCustomObject] -and $null-ne$_.status -and @('pending','running','passed','failed') -contains ([string]$_.status).ToLowerInvariant() })
      if($validChecks.Count -gt 0){
        if($total -lt 0){ $total=$validChecks.Count }
        if($completed -lt 0){ $completed=@($validChecks | Where-Object { ([string]$_.status).ToLowerInvariant() -eq 'passed' -and -not (Test-IsGptOrReleaseCheck ([string]$_.name)) }).Count }
      }
    }
    # 仅当标准 completedChecks/totalChecks/checks 均未显式提供时，从旧式 steps 数组兜底统计（只统计对象项，绝不展示 step 名称/内容）。
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
        # 陈旧内容安全忽略：updatedUtc 明显早于任务开始（>5 分钟）视为任务开始前残留，不采信。
        elseif($dtUtc -lt $script:startedUtc.AddMinutes(-5)){ $script:progressDiagnostic='PROGRESS.json updatedUtc 陈旧（早于任务开始），已忽略'; return $null }
        $updated=$dtUtc.ToString('o')
      }
      else { $script:progressDiagnostic='PROGRESS.json updatedUtc 格式非法，已忽略' }
    }
    return [pscustomobject]@{stage=$stage;summary=$summary;completedChecks=$completed;totalChecks=$total;updatedUtc=$updated;currentCheck=$currentCheck}
  }catch{ $script:progressDiagnostic='PROGRESS.json 无法解析，已忽略'; return $null }
}
function Get-PassedWorkerChecks {
  # P1-7/自动恢复证据：读取 PROGRESS.json 中 status=passed 的 workerCheck 名称（排除视觉/GUI/发布等已移交 GPT 的项）。
  $progressPath=Join-Path $task 'PROGRESS.json'
  if(-not [IO.File]::Exists($progressPath)){ return @() }
  try{
    $p=[IO.File]::ReadAllText($progressPath,[Text.Encoding]::UTF8)|ConvertFrom-Json
    if($p.PSObject.Properties['checks'] -and $p.checks -is [System.Array]){
      return @($p.checks | Where-Object { $_ -is [System.Management.Automation.PSCustomObject] -and $null-ne$_.name -and $null-ne$_.status -and ([string]$_.status).ToLowerInvariant() -eq 'passed' -and -not (Test-IsGptOrReleaseCheck ([string]$_.name)) } | ForEach-Object { [string]$_.name } | Select-Object -First 20)
    }
  }catch{}
  return @()
}
function Get-BudgetHistoryPath {
  # P1-5 历史预算统计文件：与 settings.json 同目录（AppPaths.BaseDirectory 下）。
  try{
    $settingsPath='{{{settingsPath.Replace("'", "''")}}}'
    if(-not [string]::IsNullOrWhiteSpace($settingsPath)){ return Join-Path (Split-Path $settingsPath -Parent) 'reasonix-budget-history.json' }
  }catch{}
  return $null
}
function Get-BudgetSamples([string]$complexity){
  $path=Get-BudgetHistoryPath
  if([string]::IsNullOrWhiteSpace($path) -or -not [IO.File]::Exists($path)){ return @() }
  try{
    $h=[IO.File]::ReadAllText($path,[Text.Encoding]::UTF8)|ConvertFrom-Json
    if($h.PSObject.Properties['samples']){
      $key=((($project.ToLowerInvariant() -replace '[:\\/]+','-'))+'|'+([string]$complexity).ToLowerInvariant())
      $prop=$h.samples.PSObject.Properties[$key]
      if($null-ne$prop -and $prop.Value -is [System.Array]){
        return @($prop.Value | Where-Object { $null-ne$_ -and [int]::TryParse([string]$_,[ref]([int]0)) } | ForEach-Object { [int]$_ })
      }
    }
  }catch{}
  return @()
}
function Calibrate-Budget([int]$defaultBudget,[string]$complexity){
  # P1-5 纯函数规则与 C# ReasonixBudgetHistory.Calibrate 完全一致：
  # 样本 <3 回退默认；排序去首尾各 1 个异常值后取平均；钳制到 [max(8, default/2), min(200, default*2)]。
  $samples=@(Get-BudgetSamples $complexity)
  if($samples.Count -lt 3){ return $defaultBudget }
  $sorted=@($samples | Sort-Object)
  $trimmed=@($sorted | Select-Object -Skip 1 | Select-Object -First ([Math]::Max(0,$sorted.Count-2)))
  if($trimmed.Count -eq 0){ return $defaultBudget }
  $avg=[int][Math]::Round((($trimmed | Measure-Object -Average).Average))
  $lower=[Math]::Max(8,[int][Math]::Floor($defaultBudget/2))
  $upper=[Math]::Min(200,$defaultBudget*2)
  return [Math]::Max($lower,[Math]::Min($upper,$avg))
}
function Record-BudgetSample([int]$steps){
  # P1-5 成功任务结束后记录 (项目, 复杂度) 的实际 steps；样本保留最近 20 条，原子写入。
  if($steps -lt 0){ return }
  $path=Get-BudgetHistoryPath
  if([string]::IsNullOrWhiteSpace($path)){ return }
  try{
    $history=$null
    if([IO.File]::Exists($path)){ $history=[IO.File]::ReadAllText($path,[Text.Encoding]::UTF8)|ConvertFrom-Json }
    if($null-eq$history -or $history.PSObject.Properties['samples'] -eq $null){ $history=[pscustomobject]@{samples=[pscustomobject]@{}} }
    $key=((($project.ToLowerInvariant() -replace '[:\\/]+','-'))+'|'+([string]$script:planComplexity).ToLowerInvariant())
    $list=@()
    $prop=$history.samples.PSObject.Properties[$key]
    if($null-ne$prop -and $prop.Value -is [System.Array]){ $list=@($prop.Value | Where-Object { $null-ne$_ -and [int]::TryParse([string]$_,[ref]([int]0)) }) }
    $list=@($list | Select-Object -Last 19) + $steps
    $history.samples | Add-Member -NotePropertyName $key -NotePropertyValue $list -Force | Out-Null
    Write-JsonAtomic $path $history
  }catch{}
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
  $pgSummary=$null; $pgUpdated=$null; $pgDone=-1; $pgTotal=-1; $pgCurrent=''
  if($null-ne$progress){ $pgSummary=$progress.summary; $pgUpdated=$progress.updatedUtc; $pgDone=$progress.completedChecks; $pgTotal=$progress.totalChecks; $pgCurrent=[string]$progress.currentCheck }
  if(-not [string]::IsNullOrWhiteSpace($pgCurrent)){ $script:currentCheck=$pgCurrent }
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
  # 成功终态强制 done；失败不得伪装 done（P0-3 阶段归一：完成必为 done，其余终态保留失败发生阶段）。
  if($state -eq 'completed'){ $pgStage='done'; $pgSource='helper' }
  # P0-3 状态一致性：状态文件保留运行中计算的单调剩余百分比（同一 attempt 重启恢复继承、只降不升）；
  # 完成/失败/取消/等待验收的“0% 与阶段归一”由 Helper 读取侧 ReasonixStatusJson.NormalizeTerminalState
  # 统一执行（展示与消费侧归零），状态文件继续统一走标准 JSON 原子写入，不在此处覆盖单调数据。
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
  # 预计剩余百分比（A2 单调保护）：候选值取“workerChecks 完成比例”与“步骤/软预算”较大完成比例的剩余，
  # 与 C# ReasonixUiText.RunningRemainingPercent 同规则；同一 attempt 内只允许下降或不变（min 单调），
  # 范围 5–100；新 attempt/新任务重新初始化（remainingPercent 为 null 时直接采用候选）。
  $candidate=$null
  $checksRatio=$null; $stepsRatio=$null
  if($pgTotal -gt 0 -and $pgDone -ge 0){ $checksRatio=[Math]::Min($pgDone,$pgTotal)/$pgTotal }
  if($script:planBudget -gt 0 -and $script:stepCount -gt 0){ $stepsRatio=$script:stepCount/$script:planBudget }
  if($null-ne$checksRatio -and $null-ne$stepsRatio){ $candidate=[Math]::Max($checksRatio,$stepsRatio) }
  elseif($null-ne$checksRatio){ $candidate=$checksRatio }
  elseif($null-ne$stepsRatio){ $candidate=$stepsRatio }
  if($null-ne$candidate){
    $candidateRemaining=[int][Math]::Round([Math]::Max(0.0,[Math]::Min(1.0,1.0-$candidate))*100.0)
    if($candidateRemaining -lt 5){ $candidateRemaining=5 }
    if($null-eq$script:remainingPercent -or $candidateRemaining -lt $script:remainingPercent){ $script:remainingPercent=$candidateRemaining }
  }
  $data=[ordered]@{TaskId=$taskId;ProjectRoot=$project;TaskDirectory=$task;State=$state;Phase=$phase;PermissionMode='{{{permissionMode}}}';StartedUtc=$startedText;UpdatedUtc=[DateTime]::UtcNow.ToString('o');HostProcessId=$PID;EventCount=$script:count;Message=$message;CodexThreadId=$CodexThreadId;ReasonixSessionPath=$script:reasonixSession;ReturnUri=$script:returnUri;ReturnState=$script:returnState;ExecutionIntensity=$script:planIntensity;ExecutionProfile=$script:planProfile;ExecutionEffort=$script:planEffort;ExecutionModel=$script:reasonixModel;EstimatedSteps=$script:planBudget;ModelTurnCount=$script:stepCount;StepCount=$(if($script:finalSteps -ge 0){$script:finalSteps}else{$script:stepCount});ToolCallCount=$script:toolCallCount;ReasoningEventCount=$script:reasoningCount;TokenInput=$script:tokenInput;TokenOutput=$script:tokenOutput;CacheHitTokens=$script:cacheHit;DesktopLive=(-not [string]::IsNullOrWhiteSpace($script:reasonixSession));DesktopState=$script:desktopState;ExecutionSource=$script:planSource;ProgressStage=$pgStage;ProgressSummary=$pgSummary;ProgressUpdatedUtc=$pgUpdated;CompletedChecks=$pgDone;TotalChecks=$pgTotal;ManifestDiagnostic=$script:manifestDiagnostic;ProgressDiagnostic=$script:progressDiagnostic;BudgetState=$budgetState;BudgetOverrunSteps=$overrun;LastEventKind=$script:lastEventKind;LastToolName=$script:lastToolName;FailureKind=$script:failureKind;FailureSummary=$script:failureSummary;AttemptNumber=$script:attemptNumber;ProgressSource=$pgSource;RemainingPercent=$script:remainingPercent;CurrentCheck=$script:currentCheck;ContractDiagnostic=$script:contractDiagnostic;ContractNormalized=$script:contractNormalized}
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
    # 新字段优先，缺失时回退旧 execution* 别名，保证旧 manifest 继续兼容。
    $intensityRaw=if($manifest.PSObject.Properties['intensity']){$manifest.intensity}elseif($manifest.PSObject.Properties['executionIntensity']){$manifest.executionIntensity}else{$null}
    if($intensityRaw -and @('auto','fast','standard','strict') -contains ([string]$intensityRaw).ToLowerInvariant()){ $intensity=([string]$intensityRaw).ToLowerInvariant(); $declared=$true }
    $complexityRaw=if($manifest.PSObject.Properties['complexity']){$manifest.complexity}elseif($manifest.PSObject.Properties['executionComplexity']){$manifest.executionComplexity}else{$null}
    if($complexityRaw -and @('small','medium','major') -contains ([string]$complexityRaw).ToLowerInvariant()){ $complexity=([string]$complexityRaw).ToLowerInvariant(); $declared=$true }
    $profileRaw=if($manifest.PSObject.Properties['profile']){$manifest.profile}elseif($manifest.PSObject.Properties['executionProfile']){$manifest.executionProfile}else{$null}
    if($profileRaw -and @('economy','balanced','delivery') -contains ([string]$profileRaw).ToLowerInvariant()){ $declared=$true }
    $effortRaw=if($manifest.PSObject.Properties['effort']){$manifest.effort}elseif($manifest.PSObject.Properties['executionEffort']){$manifest.executionEffort}else{$null}
    if($effortRaw -and @('low','medium','high','max') -contains ([string]$effortRaw).ToLowerInvariant()){ $effort=([string]$effortRaw).ToLowerInvariant(); $declared=$true }
    $maxStepsRaw=if($manifest.PSObject.Properties['maxSteps']){$manifest.maxSteps}elseif($manifest.PSObject.Properties['executionMaxSteps']){$manifest.executionMaxSteps}else{$null}
    $n=0; if($maxStepsRaw -and [int]::TryParse([string]$maxStepsRaw,[ref]$n) -and $n -gt 0){ $maxSteps=$n }
    $budgetRaw=if($manifest.PSObject.Properties['budgetSteps']){$manifest.budgetSteps}elseif($manifest.PSObject.Properties['executionBudgetSteps']){$manifest.executionBudgetSteps}else{$null}
    $b=0; if($budgetRaw -and [int]::TryParse([string]$budgetRaw,[ref]$b) -and $b -gt 0){ $budget=$b }
    if($manifest.PSObject.Properties['estimatedSteps'] -and -not $manifest.PSObject.Properties['budgetSteps'] -and -not $manifest.PSObject.Properties['executionBudgetSteps'] -and -not $manifest.PSObject.Properties['maxSteps'] -and -not $manifest.PSObject.Properties['executionMaxSteps']){ $script:manifestDiagnostic='manifest.json 使用了不支持的 estimatedSteps 字段，预算按推断处理（请改用 budgetSteps）' }
    $checksRaw=if($manifest.PSObject.Properties['workerChecks']){$manifest.workerChecks}elseif($manifest.PSObject.Properties['executionWorkerChecks']){$manifest.executionWorkerChecks}else{$null}
    if($checksRaw -and $checksRaw -is [System.Array]){ $checks=@($checksRaw | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }) }
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
    # 托管 Reasonix 永远使用 balanced profile：manifest 显式 economy/delivery 仅作为输入被读取（计入 source=manifest），
    # 最终 profile 恒为 balanced；Strict 只映射为 balanced + high（高 effort 由下方 InferEffort 分支负责）。
    $profile='balanced'
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
  if($null-eq$budget){
    if($complexity -eq 'small'){$budget=16}elseif($complexity -eq 'major'){$budget=56}else{$budget=35}
    # P1-5 历史预算校准：仅对推断预算按同项目同复杂度最近成功任务的实际 steps 校准；
    # manifest 显式声明的 budgetSteps 不参与校准（保持权威）。
    $budget=Calibrate-Budget $budget $complexity
  }
  $script:planIntensity=$intensity; $script:planProfile=$profile; $script:planEffort=$effort; $script:planComplexity=$complexity
  $script:planBudget=$budget; $script:planMaxSteps=$maxSteps; $script:planSource=if($declared){'manifest'}else{'inferred'}
  # 所有托管强度都禁止自动启动 review/security-review/explore 子代理；GPT 是唯一评审者。
  $script:allowAutoReview=$false
  $script:workerChecks=$checks
  $script:manifest=$manifest
}
function Test-IsGptOrReleaseCheck([string]$check){
  $t=([string]$check).ToLowerInvariant()
  $vis=@('screenshot','截屏','截图','view image','inspect image','view screenshot','inspect screenshot','看图片','看图','查看图片','查看图像','pixel','像素','dpi','visual acceptance','视觉验收','视觉验证','视觉判断','真实 gui','gui 烟测','gui smoke','gui 交互','gui 操作','gui 验收','gui 截图','screen capture','屏幕捕获','捕获屏幕','color','颜色','occlusion','遮挡','bitblt','printwindow')
  $rel=@('publish','打包','zip','.zip','安装包','installer','setup.exe','build-release','github release','create release','release 页面','releases/download','package release','打包发布','发布 release','发布安装','发布项目','发布工作','发布验收')
  $hit=$false
  foreach($p in $vis){ if($t.Contains($p)){ $hit=$true; break } }
  if(-not $hit){ foreach($p in $rel){ if($t.Contains($p)){ $hit=$true; break } } }
  if(-not $hit){ return $false }
  # 否定约束优先：命中明确视觉/GUI/发布关键词但同时含否定标记，视为“不截图/不看图/不发布”等约束说明而非待执行检查。
  foreach($n in @('不','没有','无','禁止','避免','无需','不要','不得','切勿',"don't",'do not','should not','must not','never')){ if($t.Contains($n)){ return $false } }
  return $true
}
function Write-WorkerAcceptance {
  $legal=@(); $delegated=@()
  foreach($c in $script:workerChecks){ if(Test-IsGptOrReleaseCheck $c){ $delegated += $c } else { $legal += $c } }
  $content=@()
  $content += '# WORKER_ACCEPTANCE.md'
  $content += ''
  $content += '此文件由 Codex Helper 执行器从 manifest.json 的 workerChecks 运行时派生生成，不是原始完整验收账本；完整 GPT 与发布验收仍归 GPT 独立完成。'
  $content += ''
  $content += '## workerChecks（Reasonix 需完成）'
  if($legal.Count -gt 0){ foreach($c in $legal){ $content += ('- ' + $c) } }
  else {
    $content += '- 无显式 workerChecks。按通用自动可验证 worker 规则完成：运行项目现有完整测试套件一次并执行 Release 配置构建一次，均须成功；不得尝试任何视觉/GUI/发布工作。'
  }
  if($delegated.Count -gt 0){
    $content += ''
    $content += '## 已移交 GPT（不属于 Reasonix）'
    $content += "- 共 $($delegated.Count) 项检查被判定为 GPT 视觉/GUI 或 release 打包/发布职责，已整体移交给 GPT 独立验收。其正文不在此披露（Reasonix 不截图、不看图、不作视觉结论，也不打包/发布）；Reasonix 不得尝试执行其中任何一项。"
  }
  $content += ''
  $content += '## 执行规则'
  $content += '- 每个 workerCheck 最多运行一次；已通过的不重跑；不迭代测试/Release 构建来回。'
  $content += '- 不提交、不推送、不打包、不发布、不安装；不得创建实验项目；不运行 publish/package/build-release。'
  $content += '- 视觉/GUI/发布验收全部归 GPT；Reasonix 不截图、不看图、不作视觉结论。'
  $content += ''
  $content += '## 报告要求'
  $content += '- 将实施结果写入 EXECUTION_REPORT.md。'
  $body=($content -join [Environment]::NewLine)
  $tmp=$workerAccept+'.wa-'+[Guid]::NewGuid().ToString('N')+'.tmp'
  try{[IO.File]::WriteAllText($tmp,$body,[Text.UTF8Encoding]::new($false));Move-Item -LiteralPath $tmp -Destination $workerAccept -Force}
  finally{if([IO.File]::Exists($tmp)){Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue}}
}
function Test-Negation([string]$t){
  foreach($n in @('不','没有','无','禁止','避免','无需','不要','不得','切勿',"don't",'do not','should not','must not','never')){ if($t.Contains($n)){ return $true } }
  return $false
}
function Resolve-ContractHealth {
  # 合同启动前体检与安全归一化（B1）：只检查当前任务合同的执行要求，不因历史文档出现单词而误报。
  # 原始 SPEC/ACCEPTANCE/HANDOFF/manifest 不被覆盖；归一化只作用于派生运行合同（workerChecks/状态诊断）。
  $script:contractDiagnostic=$null; $script:contractNormalized=$false; $script:contractBlocked=$false; $script:contractBlockReason=$null
  $diag=New-Object System.Collections.Generic.List[string]
  $handoff=''
  $handoffPath=Join-Path $task 'HANDOFF.md'
  if([IO.File]::Exists($handoffPath)){ try{ $handoff=[IO.File]::ReadAllText($handoffPath,[Text.Encoding]::UTF8) }catch{} }
  $handoffLines=@($handoff -split "`r?`n")
  $specText=''
  $specPath=Join-Path $task 'SPEC.md'
  if([IO.File]::Exists($specPath)){ try{ $specText=[IO.File]::ReadAllText($specPath,[Text.Encoding]::UTF8) }catch{} }
  # 1) HANDOFF 肯定式要求 Reasonix 读取 ACCEPTANCE / 写 REVIEW_PACKET / 截图视觉交付（忽略含否定标记的约束说明行）。
  $requiresReadAcceptance=$false; $requiresWritePacket=$false
  foreach($line in $handoffLines){
    if($line -match 'acceptance' -and $line -match '(read|reading|reads|读取|阅读|读)\s+(the\s+)?acceptance' -and -not (Test-Negation $line)){ $requiresReadAcceptance=$true }
    if($line -match 'review_packet' -and $line -match '(write|writing|writes|写|写入|生成)\s+(the\s+)?review_?packet' -and -not (Test-Negation $line)){ $requiresWritePacket=$true }
    if($line -match '(必须|务必|需)\s*(截图|进行视觉验收|视觉验收)|(must|required|need)\s+(to\s+)?(screenshot|take\s+screenshots|deliver\s+screenshots)|截图交付' -and -not (Test-Negation $line)){
      $script:contractBlocked=$true; $script:contractBlockReason='合同要求 Reasonix 交付截图/视觉验收证据，但视觉验收归 GPT 且 Reasonix 禁止截图，无法安全修正。请修改 HANDOFF.md 后重试。'
    }
  }
  if($requiresReadAcceptance){ $diag.Add('HANDOFF 要求 Reasonix 读取 ACCEPTANCE.md；已归一化：Reasonix 只读 SPEC/HANDOFF/manifest/WORKER_ACCEPTANCE，从不读 ACCEPTANCE。'); $script:contractNormalized=$true }
  if($requiresWritePacket){ $diag.Add('HANDOFF 要求 Reasonix 写 REVIEW_PACKET；已归一化：REVIEW_PACKET 由 Helper 自动生成，Reasonix 只写 EXECUTION_REPORT。'); $script:contractNormalized=$true }
  if($script:contractBlocked){ $script:contractDiagnostic=($diag -join '；'); return }
  # 2) workerChecks 去重（保留首个，忽略空项）——同时供 Write-WorkerAcceptance 使用。
  $unique=New-Object System.Collections.Generic.List[string]; $seen=@{}
  foreach($c in $script:workerChecks){
    $trimmed=([string]$c).Trim()
    if([string]::IsNullOrWhiteSpace($trimmed)){ continue }
    if(-not $seen.ContainsKey($trimmed.ToLowerInvariant())){ $seen[$trimmed.ToLowerInvariant()]=$true; $unique.Add($trimmed) }
  }
  if($unique.Count -ne $script:workerChecks.Count){ $diag.Add("workerChecks 存在 $($script:workerChecks.Count - $unique.Count) 项重复，已去重（保留首个）。"); $script:contractNormalized=$true }
  $script:workerChecks=@($unique)
  # 3) workerChecks 混入视觉/GUI/发布打包：职责过滤，移交给 GPT（Write-WorkerAcceptance 具体执行移交）。
  $delegatedCount=0
  foreach($c in $script:workerChecks){ if(Test-IsGptOrReleaseCheck $c){ $delegatedCount++ } }
  if($delegatedCount -gt 0){ $diag.Add("workerChecks 中 $delegatedCount 项属于视觉/GUI 或 release 打包/发布职责，已移交 GPT，Reasonix 不执行。"); $script:contractNormalized=$true }
  # 4) 普通 DeepSeek 任务错误使用 delivery profile：托管运行始终强制 balanced（manifest 声明仅作输入读取）。
  if($null-ne$script:manifest -and $script:manifest.PSObject.Properties['profile'] -and $null-ne$script:manifest.profile){
    $rawProfile=([string]$script:manifest.profile).ToLowerInvariant()
    if(@('economy','balanced','delivery') -contains $rawProfile -and $rawProfile -ne 'balanced'){
      $diag.Add("manifest 声明 profile=$($script:manifest.profile)；托管 Reasonix 运行始终强制 balanced，声明仅作为输入读取。"); $script:contractNormalized=$true
    }
  }
  # 5) 普通 small/medium DeepSeek 任务显式 high/max：合同预检规范——派生运行计划 effort 降为 low
  #    （不修改用户合同原文件）；strict/major/security/release/migration 任务保留 high。
  if($script:reasonixModel.ToLowerInvariant().Contains('deepseek') -and @('high','max') -contains $script:planEffort -and @('small','medium') -contains $script:planComplexity -and $script:planIntensity -ne 'strict'){
    $spec=$specText.ToLowerInvariant()
    $highRisk=@('credential','crypto','envelope','vault','secret','security','migration','installer','publish','release','凭据','加密','迁移','发布','安装','安全') | Where-Object { $spec.Contains($_) }
    if($highRisk.Count -eq 0){
      $origEffort=$script:planEffort
      $script:planEffort='low'
      $diag.Add("普通 $($script:planComplexity) DeepSeek 任务显式声明 effort=$origEffort；已按合同预检规范把派生运行计划 effort 降为 low（不修改用户合同原文件）。strict/major/security/release/migration 任务保留 high。")
      $script:contractNormalized=$true
    }
  }
  # 6) HANDOFF 缺少允许读取/允许修改/直接依赖范围。
  $hasRead=@($handoffLines | Where-Object { $_ -match '允许读取|allowed-?read|允许读' }).Count -gt 0
  $hasWrite=@($handoffLines | Where-Object { $_ -match '允许修改|allowed-?write|允许写' }).Count -gt 0
  $hasDep=@($handoffLines | Where-Object { $_ -match '直接依赖|direct dependenc' }).Count -gt 0
  if(-not $hasRead -or -not $hasWrite -or -not $hasDep){
    $missing=@(); if(-not $hasRead){ $missing+='允许读取范围' }; if(-not $hasWrite){ $missing+='允许修改范围' }; if(-not $hasDep){ $missing+='直接依赖范围' }
    $diag.Add("HANDOFF 缺少 $($missing -join '、')；已提示 GPT 补全合同，不影响本任务执行。")
  }
  $script:contractDiagnostic=($diag -join '；')
  if($script:contractDiagnostic.Length -gt 500){ $script:contractDiagnostic=$script:contractDiagnostic.Substring(0,500)+'…' }
}
function Get-GitBaseline([string]$root) {
  # 运行前记录 Git 脏文件内容指纹（tracked modified + untracked），供运行后过滤"本次执行新增的变化"。
  # git 不可用或无法枚举时返回 $null，标记无法可靠建立基线（调用方保守按 full 验收）。
  try{
    $tracked=@(& git -C $root diff --name-only HEAD 2>$null)
    $untracked=@(& git -C $root ls-files --others --exclude-standard 2>$null)
    $files=@($tracked + $untracked | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
    $map=@{}
    foreach($f in $files){
      $full=Join-Path $root $f
      $key=$f.ToLowerInvariant()
      if([IO.File]::Exists($full)){
        try{ $map[$key]=(Get-FileHash -LiteralPath $full -Algorithm SHA256 -ErrorAction Stop).Hash }
        catch{ $map[$key]='' }
      } else { $map[$key]='missing' }
    }
    return $map
  }catch{ return $null }
}
function Get-ChangedFiles {
  # 验收范围只依据本次执行新增的变化：运行前基线之上新出现的文件计入；
  # 原已脏文件仅在内容指纹变化时计入；无法可靠建立基线（$null）时返回空（保守 full）。
  if($null-eq$script:gitBaseline){ return @() }
  try{
    $tracked=@(& git -C $project diff --name-only HEAD 2>$null)
    $untracked=@(& git -C $project ls-files --others --exclude-standard 2>$null)
    $current=@($tracked + $untracked | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
    $changed=@()
    foreach($f in $current){
      $key=$f.ToLowerInvariant()
      if(-not $script:gitBaseline.ContainsKey($key)){ $changed+=$f; continue }
      $full=Join-Path $project $f
      $now=''
      if([IO.File]::Exists($full)){ try{ $now=(Get-FileHash -LiteralPath $full -Algorithm SHA256 -ErrorAction Stop).Hash } catch { $now='' } }
      else { $now='missing' }
      if($script:gitBaseline[$key] -ne $now){ $changed+=$f }
    }
    return $changed
  }catch{ return @() }
}
function Recommend-AcceptanceScope {
  # 影响范围增量验收映射（B5）：与 C# ReasonixAcceptanceScope.Recommend 同规则。
  param([string[]]$files)
  $releasePatterns=@('directory.build.props','installer','build-release','publish','.iss','.wxs','.github\workflows','.github/workflows')
  $securityPatterns=@('credential','crypto','envelope','vault','secret','backup','migration','encrypt','decrypt','portable','bundle','officialaccounts','apiprovider','api-provider')
  if($files.Count -eq 0){ return @{Label='full';Scopes=@('full');Rationale='无法识别改动文件（空列表或 git 不可用），按完整回归验收。'} }
  $releaseHit=$false; $securityHit=$false; $visualHit=$false; $sourceCount=0; $docCount=0; $testCount=0; $reasons=@()
  foreach($f in $files){
    $low=($f -replace '/','\').ToLowerInvariant()
    $isRelease=$false; $isSecurity=$false
    foreach($p in $releasePatterns){ if($low.Contains($p)){ $releaseHit=$true; $isRelease=$true; break } }
    if(-not $isRelease){ foreach($p in $securityPatterns){ if($low.Contains($p)){ $securityHit=$true; $isSecurity=$true; break } } }
    if($isRelease -or $isSecurity){ $reasons += $f }
    if($low.EndsWith('.xaml')){ $visualHit=$true }
    if($low.EndsWith('.cs') -and $low.Contains('src')){ $sourceCount++ }
    if($low.EndsWith('.md') -or $low.EndsWith('.txt') -or $low.Contains('readme') -or $low.Contains('\docs\')){ $docCount++ }
    if($low.Contains('\tests\') -or $low.StartsWith('tests\') -or $low.Contains('test')){ $testCount++ }
  }
  if($releaseHit){ return @{Label='release + full';Scopes=@('release','full');Rationale=('改动涉及 installer/版本/发布脚本：'+(($reasons|Select-Object -First 5) -join '、'))} }
  if($securityHit){ return @{Label='security + full';Scopes=@('security','full');Rationale=('改动涉及凭据/加密/备份/迁移：'+(($reasons|Select-Object -First 5) -join '、'))} }
  if($visualHit){ return @{Label='focused + visual';Scopes=@('focused','visual');Rationale=('改动涉及 UI/XAML：'+(($files|Select-Object -First 5) -join '、'))} }
  if($docCount -eq $files.Count){ return @{Label='focused';Scopes=@('focused');Rationale=('纯文案/文档改动：'+(($files|Select-Object -First 5) -join '、'))} }
  if($testCount -eq $files.Count){ return @{Label='focused';Scopes=@('focused');Rationale=('纯测试改动：'+(($files|Select-Object -First 5) -join '、'))} }
  if($files.Count -eq 1){ return @{Label='focused';Scopes=@('focused');Rationale=('单个文件改动：'+$files[0])} }
  if($sourceCount -ge 3 -or $files.Count -ge 3){ return @{Label='full';Scopes=@('full');Rationale=("涉及 $sourceCount 个核心源码文件（共 $($files.Count) 个改动文件），按完整回归验收。")} }
  return @{Label='focused';Scopes=@('focused');Rationale=('少量普通源码/文件改动：'+(($files|Select-Object -First 5) -join '、'))}
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
Resolve-ContractHealth
if($script:contractBlocked){
  $script:failureKind='contract-blocked'
  Save-Status 'failed' 'blocked' $script:contractBlockReason
  Write-FailureReport 'contract-blocked' $script:contractBlockReason 3
  Write-Output "Reasonix task blocked: $taskId. $script:contractBlockReason"
  exit 3
}
Write-WorkerAcceptance
if([IO.File]::Exists($StatusPath)){
  try{
    $prev=[IO.File]::ReadAllText($StatusPath,[Text.Encoding]::UTF8)|ConvertFrom-Json
    $an=0; if($prev.PSObject.Properties['attemptNumber'] -and [int]::TryParse([string]$prev.attemptNumber,[ref]$an) -and $an -gt 0){ $script:attemptNumber=$an }
    # 重启恢复单调：同一 attempt 继承已持久化的预计剩余百分比（继续下降或不变）；新 attempt 不继承（重新初始化）。
    if($prev.PSObject.Properties['attemptNumber'] -and $null-ne$prev.attemptNumber -and [int]$prev.attemptNumber -eq $script:attemptNumber -and $prev.PSObject.Properties['remainingPercent'] -and $null-ne$prev.remainingPercent){
      $rp=0; if([int]::TryParse([string]$prev.remainingPercent,[ref]$rp) -and $rp -ge 5 -and $rp -le 100){ $script:remainingPercent=$rp }
    }
  }catch{}
}
$script:sessionRoot=Get-ProjectSessionRoot
$script:baseline=Get-SessionBaseline $script:sessionRoot
# 运行 Reasonix 前记录 Git 脏文件内容指纹，供运行后按"本次执行新增的变化"过滤验收范围（B5）。
$script:gitBaseline=Get-GitBaseline $project
Save-Status 'starting' 'starting' 'Reading task contract'
$utf8=[Text.UTF8Encoding]::new($false); [IO.File]::WriteAllText($events,'',$utf8)
$lock=Join-Path ([IO.Path]::GetFullPath((Join-Path $project '.codex-helper\runs'))) '.reasonix.lock'; $stream=$null
try{
  $stream=[IO.File]::Open($lock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  Save-Status 'running' 'executing' 'Reasonix is executing the task'
  $workerChecksLine='Satisfy every workerCheck listed in WORKER_ACCEPTANCE.md (derived from manifest workerChecks); gptChecks and releaseChecks belong to GPT, not Reasonix.`n'
  $reviewLine='Do not auto-start review, security-review, or explore subagents; GPT is the reviewer.`n'
  $progressLine='Maintain PROGRESS.json in the task directory atomically (standard JSON, UTF-8 without BOM) right before starting and after finishing each workerCheck, with fields stage/summary/updatedUtc/completedChecks/totalChecks/currentCheck/checks (each check has name and status pending/running/passed/failed); never write outside the task directory.`n'
  $contractNote=if(-not [string]::IsNullOrWhiteSpace($script:contractDiagnostic)){ 'Contract normalization (derived contract only; original SPEC/ACCEPTANCE/HANDOFF/manifest are not modified): '+$script:contractDiagnostic+'`n' }else{''}
  $prompt="Read the task contract from $task (only SPEC.md, HANDOFF.md, manifest.json and WORKER_ACCEPTANCE.md in the current task directory; do not read ACCEPTANCE.md). Implement SPEC.md exactly within project root $project, satisfy HANDOFF.md and the workerChecks in WORKER_ACCEPTANCE.md, and write the execution report to $report (write only that file; do not write REVIEW_PACKET.md — Helper generates it automatically). Do not redesign scope.`n"+
  "HANDOFF.md explicitly lists allowed-read files, allowed-write files, and direct dependencies; read only those files and their direct dependencies, do not scan unrelated parts of the repo, and once the goal is clear do not recursively scan the tree. First list at most 5 concrete implementation actions; then read all needed files together (parallel reads are fine; never re-read a file that has not changed) before forming one consolidated edit set, then apply edits in a few batch passes instead of many small round-trips.`n"+
  "Execution policy (soft budget, not a hard limit): intensity=$($script:planIntensity), profile=$($script:planProfile), effort=$($script:planEffort), estimated ~$($script:planBudget) steps.`n"+
  'GPT is the final reviewer; Reasonix performs implementation and workerChecks only.`n'+
  $workerChecksLine+
  $progressLine+
  $contractNote+
  'Run each workerCheck at most once and deduplicate it: if a normalized check already passed, never re-run it (including during the reporting/readiness phase when the affected files are unchanged); do not iterate test/Release build back and forth.`n'+
  'gptChecks and releaseChecks (visual acceptance, full regression, packaging/release) belong to GPT or a later release phase; do not attempt them.`n'+
  $reviewLine+
  '{{{VisualBoundaryRule}}}`n'+
  'If actual steps exceed the estimate, converge to the remaining acceptance items; never create extra experiment projects and never run publish/package/build-release without explicit authorization.`n'+
  'Do not read old runs or events under .codex-helper/runs, do not recursively scan bin/obj, do not re-read unchanged files, and do not re-run commands that already passed.'
  {{{permissionArgs}}}
  $cliEffort=if($script:planEffort -eq 'medium'){'high'}else{$script:planEffort}
  $runArgs=@('run','--dir',$project,'--profile',$script:planProfile,'--effort',$cliEffort)
  if($null-ne$script:planMaxSteps){ $runArgs+=@('--max-steps',[string]$script:planMaxSteps) }
  $runArgs += $permissionArgs
  # Reasonix 1.19.x requires every option before the final task text.
  $runArgs+=@('--events-jsonl','--metrics',$metrics,$prompt)
  & '{{{executable.Replace("'", "''")}}}' @runArgs 2>$helperErr | ForEach-Object {
    $line=$_.ToString()
    [IO.File]::AppendAllText($events,$line+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
    $script:count++
    $obj=$null
    try { $obj=$line|ConvertFrom-Json } catch {}
    if($null-ne$obj){
      $kind=[string]$obj.kind
      if(-not [string]::IsNullOrWhiteSpace($kind)){ $script:lastEventKind=$kind }
      $eventTool=([string]$obj.tool_name)+([string]$obj.tool)+([string]$obj.name)
      # final_readiness 兼容两种真实格式：direct kind（{"kind":"final_readiness"}）与 Reasonix 1.19.3 的
      # notice.code（{"kind":"notice","code":"final_readiness"}）；并保留 direct tool/name 兼容。
      $eventCode=[string]$obj.code
      $isFinalReadiness=($kind -eq 'final_readiness') -or ([string]::Equals($eventCode,'final_readiness',[StringComparison]::OrdinalIgnoreCase)) -or $eventTool.ToLowerInvariant().Contains('final_readiness')
      if($isFinalReadiness){ $script:finalReadinessSeen=$true }
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
  # 影响范围增量验收建议（B5）：按实际改动文件生成，供 GPT 选择验收范围；只是建议，合同显式要求与高风险规则优先。
  $changedFiles=@(Get-ChangedFiles)
  $scope=Recommend-AcceptanceScope $changedFiles
  $scopeLine="`n- Acceptance scope suggestion: $($scope.Label)`n- Scope details: $(($scope.Scopes) -join ' + ')`n- Rationale: $($scope.Rationale)`n- Changed files: $(if($changedFiles.Count -gt 0){($changedFiles|Select-Object -First 10) -join '; '}else{'（无法枚举）'})"
  # P1-7 避免重复验收：Review Packet 明确记录已通过 workerChecks（排除视觉/GPT 项），后续只运行未完成检查。
  $passedChecks=Get-PassedWorkerChecks
  $passedChecksLine=if($passedChecks.Count -gt 0){('- Passed workerChecks (from PROGRESS.json, GPT-owned items excluded): '+(($passedChecks) -join '; '))}else{'- Passed workerChecks: none recorded'}
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
$scopeLine
$passedChecksLine
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
    if($exit -ne 0){
      # 谨慎分类：仅当 exit=1、事件出现 final_readiness 且存在实际活动（tool/step 证据）时，才把退出码视为
      # "Reasonix 最终门禁未通过"（等待 GPT 复核），而不是普通代码开发失败或伪装成功。
      if($exit -eq 1 -and $script:finalReadinessSeen -and ($script:toolCallCount -gt 0 -or $script:stepCount -gt 0)){
        $script:failureKind='final-readiness-blocked'
        $script:failureSummary='Reasonix 最终门禁未通过；执行与交付报告已生成，等待 GPT 独立复核验收。'
      } else {
        $script:failureKind='cli-exit'
        $script:failureSummary="Reasonix exited $exit but EXECUTION_REPORT.md exists."
      }
    }
  } else {
    if($script:modelRunFailed){ $script:failureKind='model-run-failed' }
    elseif($exit -ne 0){ $script:failureKind='cli-exit' }
    else { $script:failureKind='missing-report' }
    if($script:modelRunFailed -and $script:stepCount -eq 0 -and $script:tokenInput -eq 0){
      $script:failureSummary="Reasonix ended before the first model turn (0 tokens). The installed CLI may not support the configured permission mode. Rescan the Reasonix CLI or switch to Safe mode, then retry."
    } else {
      $script:failureSummary="No EXECUTION_REPORT.md; last stage=$($script:lastStage), last event=$($script:lastEventKind), last tool=$($script:lastToolName)."
    }
    Write-FailureReport $script:failureKind $script:failureSummary $exit
    # P0-1 漏报告自动恢复证据：exit 0、无报告且非模型失败（missing-report）时，把本次执行的活动/
    # 本次新增变化/已通过检查持久化到任务目录，供 Helper 判定并生成自动恢复报告与 Review Packet。
    # 证据只是结构化事实，绝不伪造测试通过；是否恢复由 Helper 按条件独立判定。
    if($script:failureKind -eq 'missing-report'){
      $evidChanged=@(Get-ChangedFiles)
      $evidPassed=@(Get-PassedWorkerChecks)
      $evidence=[ordered]@{taskId=$taskId;attemptNumber=$script:attemptNumber;exitCode=0;hasActivity=($script:stepCount -gt 0 -or $script:toolCallCount -gt 0);stepCount=$script:stepCount;toolCallCount=$script:toolCallCount;changedFiles=$evidChanged;passedChecks=$evidPassed}
      Write-JsonAtomic (Join-Path $task 'auto-recovery-evidence.json') $evidence
    }
  }
  if($reportExists){
    if($exit -eq 0){
      $script:returnState='same-turn-resume'
      Save-Status 'completed' 'awaiting-gpt-review' ('Reasonix completed; GPT can review. Desktop: '+$script:desktopDiagnostic)
      # P1-5 历史预算校准：成功任务结束后记录 (项目, 复杂度) 的实际 steps，供后续任务推导软预算。
      if($script:finalSteps -ge 0){ Record-BudgetSample $script:finalSteps }
    } elseif($script:failureKind -eq 'final-readiness-blocked'){
      $script:returnState='executor-error'
      Save-Status 'failed' 'awaiting-gpt-review' ("Reasonix 最终门禁未通过（exit $exit）；执行与交付报告已生成，等待 GPT 独立复核。Desktop: "+$script:desktopDiagnostic)
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

    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowFailure = false, TimeSpan? timeout = null)
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
        var exitTask = process.WaitForExitAsync(cancellationToken);
        if (timeout is not null)
        {
            // 诊断探测超时保护：CLI 挂起时终止进程树，避免 UI 操作被永久锁住。
            var timeoutTask = Task.Delay(timeout.Value, cancellationToken);
            Task completed;
            try { completed = await Task.WhenAny(exitTask, timeoutTask); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            finally
            {
                if (!exitTask.IsCompleted) try { process.Kill(entireProcessTree: true); } catch { }
            }
            if (completed != exitTask)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return new ProcessResult(-1, string.Empty, $"命令超时（{timeout.Value.TotalSeconds:0} 秒）后已终止。");
            }
        }
        else await exitTask;
        var result = new ProcessResult(process.ExitCode, await stdout, await stderr);
        if (!allowFailure && result.ExitCode != 0) throw new InvalidOperationException(FirstUsefulLine(result.StdErr));
        return result;
    }

    private sealed record IntegrationState(bool Enabled, string ExecutablePath, string DefaultModel, ReasonixPermissionMode? PermissionMode);
    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
