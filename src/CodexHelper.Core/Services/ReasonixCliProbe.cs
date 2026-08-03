using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexHelper.Core.Services;

/// <summary>进程执行结果（探测的原始输入）。</summary>
public sealed record ReasonixProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>单个候选的快速能力探测结果（版本与 doctor JSON 结构）。</summary>
public sealed record ReasonixCliProbeResult(
    string Path,
    ReasonixCliSource Source,
    string Version,
    bool ProbeOk,
    int DoctorExitCode,
    bool DoctorExitOk,
    bool HasConfig,
    bool HasProviders,
    string? DoctorJson,
    string? Error);

/// <summary>最终 CLI 选择结果：最优候选 + 全部探测候选（供 UI 多候选提示与诊断）。</summary>
public sealed record ReasonixCliSelection(
    ReasonixCliProbeResult? Best,
    IReadOnlyList<ReasonixCliProbeResult> Candidates,
    string? SavedPath,
    bool SavedPathMissing,
    string? DiscoveryNote);

/// <summary>
/// 候选能力探测：对每个候选执行 `--version` 与 `doctor --json`，解析 doctor JSON
/// （容忍 BOM、ANSI 转义与前后噪声），单个候选失败/超时不得阻断其他候选。
/// doctor 非零退出码也先尝试解析 stdout；stdout/stderr 摘要会脱敏。
/// </summary>
public sealed class ReasonixCliProbe
{
    /// <summary>进程执行注入点（测试用）；默认真实启动进程。</summary>
    public Func<string, IReadOnlyList<string>, ReasonixProcessResult>? ProcessRunner { get; init; }

    /// <summary>文件存在性检查（测试用）；默认 File.Exists。</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>单次探测超时（默认 5 秒，防损坏 CLI 挂起）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>同步探测单个候选（内部异步等待；FindExecutable 等同步调用点使用）。</summary>
    public ReasonixCliProbeResult Probe(ReasonixCliCandidate candidate)
        => ProbeAsync(candidate, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<ReasonixCliProbeResult> ProbeAsync(ReasonixCliCandidate candidate, CancellationToken cancellationToken = default)
    {
        var versionResult = await RunAsync(candidate.Path, ["--version"], cancellationToken);
        var version = versionResult is null ? string.Empty : CleanVersionOutput(versionResult.StdOut);

        var doctorResult = await RunAsync(candidate.Path, ["doctor", "--json"], cancellationToken);
        var doctorExitCode = doctorResult?.ExitCode ?? -1;
        var doctorJson = doctorResult is null ? null : CleanDoctorOutput(doctorResult.StdOut);
        using var parsed = TryParseJson(doctorJson);
        var hasConfig = parsed is not null && parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("config", out _);
        var hasProviders = parsed is not null && parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Array;

        // 探测可用 = 拿到版本 或 doctor stdout 能解析为有效 JSON；非 JSON 垃圾输出不算可用。
        var probeOk = !string.IsNullOrWhiteSpace(version) || parsed is not null;
        var error = probeOk ? null : DescribeProbeFailure(candidate.Path, doctorResult);
        return new ReasonixCliProbeResult(
            candidate.Path,
            candidate.Source,
            version,
            probeOk,
            doctorExitCode,
            doctorResult is null || doctorResult.ExitCode == 0,
            hasConfig,
            hasProviders,
            doctorJson,
            error);
    }

    /// <summary>探测全部候选、去重并选出最优。单个损坏候选不影响其他候选。</summary>
    public async Task<ReasonixCliSelection> SelectBestAsync(
        IReadOnlyList<ReasonixCliCandidate> candidates,
        string? savedPath,
        CancellationToken cancellationToken = default)
    {
        var savedPathMissing = !string.IsNullOrWhiteSpace(savedPath) && !FileExists(PathUtil.GetFullPathSafe(savedPath));
        var probed = new List<ReasonixCliProbeResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            try { probed.Add(await ProbeAsync(candidate, cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch { probed.Add(new ReasonixCliProbeResult(candidate.Path, candidate.Source, string.Empty, false, -1, false, false, false, null, "探测失败")); }
        }

        var distinct = probed
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var best = distinct.Count == 0 ? null : distinct.MinBy(item => item, Comparer<ReasonixCliProbeResult>.Create((a, b) => Compare(a, b, savedPath)));

        var note = BuildDiscoveryNote(best, distinct, savedPath, savedPathMissing);
        return new ReasonixCliSelection(best, distinct, savedPath, savedPathMissing, note);
    }

    /// <summary>
    /// 评分排序。优先级桶（数值小者优先）：
    /// 0 用户保存路径 且 探测可用（含旧协议结构，但排除 npm shim）——用户固定选择优先；
    /// 1 探测可用 且 兼容新结构（config+providers）的正式 Desktop/CLI；
    /// 2 探测可用 但不兼容新结构（旧协议，非 npm）；
    /// 3 npm shim（无论是否被保存，永远兜底，避免掩盖更兼容的 Desktop）；
    /// 4 文件存在但探测失败（损坏候选；仅当没有更好候选时兜底）。
    /// 同桶内版本较新者优先；仍相同按来源排序。
    /// 保存路径迁移规则：仅当保存路径被删除（不在候选）、探测完全失败（损坏）
    /// 或保存的是 npm 旧版 shim 时，才自动迁移到更兼容的候选。
    /// </summary>
    public static int Compare(ReasonixCliProbeResult a, ReasonixCliProbeResult b, string? savedPath)
    {
        var aTier = Tier(a, savedPath);
        var bTier = Tier(b, savedPath);
        if (aTier != bTier) return aTier.CompareTo(bTier);
        var version = CompareVersions(a.Version, b.Version);
        if (version != 0) return -version; // 版本高者优先
        return SourceRank(a.Source).CompareTo(SourceRank(b.Source));
    }

    public static int Tier(ReasonixCliProbeResult result, string? savedPath)
    {
        var isSaved = !string.IsNullOrWhiteSpace(savedPath)
            && string.Equals(PathUtil.GetFullPathSafe(result.Path), PathUtil.GetFullPathSafe(savedPath), StringComparison.OrdinalIgnoreCase);
        var compatible = result.ProbeOk && result.HasConfig && result.HasProviders;
        var isNpm = result.Source == ReasonixCliSource.Npm;
        if (isSaved && result.ProbeOk && !isNpm) return 0;
        if (compatible) return 1;
        if (result.ProbeOk && !isNpm) return 2;
        if (isNpm && result.ProbeOk) return 3;
        return 4;
    }

    /// <summary>比较版本字符串：x.y.z 数值比较，解析失败视为旧版本。返回 a 相对 b 的差值方向。</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var aParts = ParseVersion(a);
        var bParts = ParseVersion(b);
        var length = Math.Max(aParts.Count, bParts.Count);
        for (var i = 0; i < length; i++)
        {
            var left = i < aParts.Count ? aParts[i] : 0;
            var right = i < bParts.Count ? bParts[i] : 0;
            if (left != right) return left.CompareTo(right);
        }
        return 0;
    }

    private static List<int> ParseVersion(string? text)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var match = Regex.Match(text, @"\d+(?:\.\d+){0,3}");
        if (!match.Success) return result;
        foreach (var part in match.Value.Split('.'))
            if (int.TryParse(part, out var number)) result.Add(number);
        return result;
    }

    private static int SourceRank(ReasonixCliSource source) => source switch
    {
        ReasonixCliSource.Saved => 0,
        ReasonixCliSource.Registry => 1,
        ReasonixCliSource.RunningProcess => 2,
        ReasonixCliSource.CommonLocation => 3,
        ReasonixCliSource.Path => 4,
        _ => 5
    };

    /// <summary>构建诊断说明：保存路径失效迁移、多候选择优。</summary>
    private static string? BuildDiscoveryNote(ReasonixCliProbeResult? best, IReadOnlyList<ReasonixCliProbeResult> candidates, string? savedPath, bool savedPathMissing)
    {
        if (best is null)
        {
            return savedPathMissing
                ? $"已保存的 Reasonix CLI 路径不存在，且未发现其他可用候选。"
                : "未发现任何 Reasonix CLI 候选。";
        }
        if (savedPathMissing)
            return $"已保存的 Reasonix CLI 路径不存在，已自动重新发现并切换到 {DescribeSource(best.Source)}：{best.Path}。";
        var isSavedBest = !string.IsNullOrWhiteSpace(savedPath)
            && string.Equals(PathUtil.GetFullPathSafe(best.Path), PathUtil.GetFullPathSafe(savedPath), StringComparison.OrdinalIgnoreCase);
        if (!isSavedBest && candidates.Count > 1)
            return $"检测到 {candidates.Count} 个 Reasonix CLI 候选，已按能力择优选择 {DescribeSource(best.Source)}：{best.Path}。";
        return null;
    }

    public static string DescribeSource(ReasonixCliSource source) => source switch
    {
        ReasonixCliSource.Saved => "用户指定",
        ReasonixCliSource.Registry => "安装注册表",
        ReasonixCliSource.RunningProcess => "运行中的程序",
        ReasonixCliSource.CommonLocation => "常见安装位置",
        ReasonixCliSource.Path => "PATH",
        _ => "npm"
    };

    /// <summary>清洗 doctor 输出：去 BOM、剥离 ANSI 转义、截取首个 { 到最后一个 } 的 JSON 主体。</summary>
    public static string CleanDoctorOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var cleaned = text.TrimStart('\uFEFF');
        if (cleaned.Length == 0) return string.Empty;
        // 剥离 ANSI 转义序列（ESC[...m 等）
        cleaned = AnsiEscapeRegex.Replace(cleaned, string.Empty);
        // 保留首个 { 到最后一个 } 之间的内容（容忍 PowerShell 横幅/日志前缀等噪声）
        var firstBrace = cleaned.IndexOf('{');
        var lastBrace = cleaned.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace) cleaned = cleaned[firstBrace..(lastBrace + 1)];
        return cleaned.Trim();
    }

    private static readonly Regex AnsiEscapeRegex = new(@"\u001B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    /// <summary>清洗版本输出：去 BOM、剥离 ANSI 转义与首尾空白。</summary>
    public static string CleanVersionOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var cleaned = text.TrimStart('\uFEFF');
        cleaned = AnsiEscapeRegex.Replace(cleaned, string.Empty);
        return cleaned.Trim();
    }

    /// <summary>宽容解析 JSON（复用现有 Windows 路径修复逻辑），失败返回 null。</summary>
    public static JsonDocument? TryParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return ReasonixIntegrationService.ParseLenientWindowsJson(text); }
        catch { return null; }
    }

    /// <summary>探测失败原因（脱敏、非空）：含路径、退出码与脱敏后的 stderr 摘要。</summary>
    private static string? DescribeProbeFailure(string path, ReasonixProcessResult? doctor)
    {
        if (doctor is null) return $"无法启动 {path}（探测超时或进程异常）。";
        var stderr = ReasonixIntegrationService.RedactSecrets(doctor.StdErr).Trim();
        var stderrSummary = string.IsNullOrWhiteSpace(stderr)
            ? string.Empty
            : "，输出：" + (stderr.Length > 120 ? stderr[..120] + "…" : stderr);
        return $"Reasonix CLI {path} 未产生有效版本或诊断输出（退出码 {doctor.ExitCode}{stderrSummary}）。";
    }

    private async Task<ReasonixProcessResult?> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (ProcessRunner is not null) return ProcessRunner(executable, arguments);
        var isPowerShell = string.Equals(Path.GetExtension(executable), ".ps1", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo start;
        if (isPowerShell)
        {
            // .ps1 不是可直接启动的可执行文件，需经 powershell.exe -File 包装。
            start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(executable);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        }
        else
        {
            start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) { return new ReasonixProcessResult(-1, string.Empty, ex.Message); }
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(Timeout, cancellationToken);
        Task completed;
        try
        {
            completed = await Task.WhenAny(exitTask, timeoutTask);
        }
        catch (OperationCanceledException)
        {
            // 取消：先终止进程树，避免子进程残留后台，再传播取消。
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
                // 取消被 Task.WhenAny 呈现为“超时”分支：显式终止进程并传播取消。
                try { process.Kill(entireProcessTree: true); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
            }
            return null; // 超时：视为探测失败（进程已在 finally 中终止）
        }
        await exitTask; // 传播取消/异常
        var result = new ReasonixProcessResult(process.ExitCode, await stdout, await stderr);
        return result;
    }
}
