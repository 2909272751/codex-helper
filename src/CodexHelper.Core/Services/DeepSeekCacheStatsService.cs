using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// Reads locally persisted DeepSeek usage from two sources without intercepting
/// requests or credentials: (1) this machine's Codex session logs and (2) Codex
/// Helper's own Reasonix task statistics. Each source is read independently and
/// tolerantly so a single damaged/locked file never fails the whole refresh.
/// </summary>
public sealed class DeepSeekCacheStatsService
{
    private readonly string codexRoot;
    private readonly string? reasonixTasksDirectory;

    public DeepSeekCacheStatsService(string codexRoot, string? reasonixTasksDirectory = null)
    {
        this.codexRoot = codexRoot;
        this.reasonixTasksDirectory = reasonixTasksDirectory;
    }

    public DeepSeekCacheStats ReadRecent(TimeSpan? lookback = null)
        => Scan(DateTime.UtcNow - (lookback ?? TimeSpan.FromDays(14)), CancellationToken.None, FormatRangeLabel(lookback ?? TimeSpan.FromDays(14)));

    /// <summary>
    /// 异步可取消统计；range 为空（null）表示扫描全部符合数据文件。保留 ReadRecent 同步包装供测试兼容。
    /// </summary>
    public Task<DeepSeekCacheStats> ReadAsync(TimeSpan? range, CancellationToken cancellationToken = default)
    {
        var since = range is null ? DateTime.MinValue : DateTime.UtcNow - range.Value;
        var label = range is null ? "全部" : FormatRangeLabel(range.Value);
        return Task.Run(() => Scan(since, cancellationToken, label), cancellationToken);
    }

    private static string FormatRangeLabel(TimeSpan range)
    {
        if (range.TotalDays >= 30) return "最近 30 天";
        if (range.TotalDays >= 14) return "最近 14 天";
        if (range.TotalDays >= 7) return "最近 7 天";
        return "最近 24 小时";
    }

    private DeepSeekCacheStats Scan(DateTime since, CancellationToken cancellationToken, string rangeLabel)
    {
        long codexRequests = 0, codexHit = 0, codexInput = 0;
        long reasonixTasks = 0, reasonixHit = 0, reasonixInput = 0;
        DateTime? latest = null;
        var scanned = 0;
        var skipped = 0;        // 非 DeepSeek 或缺少模型/用量而安全跳过的条目
        var corrupt = 0;        // 无法解析的损坏文件
        var unreadable = 0;     // 锁定 / 无访问权限等不可读取文件
        var clamped = 0;        // 数值越界（负数、cached>input）被夹取的次数

        // ---------------------------------------------------------------- Codex
        var sessions = Path.Combine(codexRoot, "sessions");
        if (Directory.Exists(sessions))
        {
            foreach (var path in Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories)
                         .Select(candidate => new FileInfo(candidate))
                         .Where(file => file.LastWriteTimeUtc >= since)
                         .OrderByDescending(file => file.LastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                try
                {
                    // 头部识别与正文统计两次均显式共享读取：活跃超大文件允许写进程并发写入，
                    // 且避免识别阶段用默认独占 StreamReader 导致活跃 JSONL 被判不可读。
                    if (!IsDeepSeekSession(path.FullName)) { skipped++; continue; }
                    using var stream = new FileStream(path.FullName, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!TryReadUsage(line, out var input, out var cached)) continue;
                        var safeCached = Math.Clamp(cached, 0, input);
                        if (safeCached != cached) clamped++;
                        codexRequests++;
                        AddClamped(ref codexInput, input, ref clamped);
                        AddClamped(ref codexHit, safeCached, ref clamped);
                    }
                    if (latest is null || path.LastWriteTimeUtc > latest) latest = path.LastWriteTimeUtc;
                }
                catch (IOException) { unreadable++; }
                catch (UnauthorizedAccessException) { unreadable++; }
            }
        }

        // ------------------------------------------------------------- Reasonix
        if (!string.IsNullOrWhiteSpace(reasonixTasksDirectory) && Directory.Exists(reasonixTasksDirectory))
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var file in Directory.EnumerateFiles(reasonixTasksDirectory, "*.json")
                         .Select(candidate => new FileInfo(candidate))
                         .Where(file => file.LastWriteTimeUtc >= since))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                ReasonixTaskStatus? status;
                try { status = JsonSerializer.Deserialize<ReasonixTaskStatus>(File.ReadAllText(file.FullName, Encoding.UTF8), options); }
                catch { corrupt++; continue; }
                if (status is null) { corrupt++; continue; }
                if (!IsDeepSeekReasonix(status)) { skipped++; continue; }   // 旧状态缺 ExecutionModel 也在此安全跳过
                var input = ClampNonNegative(status.TokenInput, ref clamped);
                var hit = Math.Clamp(status.CacheHitTokens, 0, input);
                if (hit != status.CacheHitTokens) clamped++;
                if (input <= 0 && hit <= 0) { skipped++; continue; }        // 无任何用量记录
                reasonixTasks++;
                AddClamped(ref reasonixInput, input, ref clamped);
                AddClamped(ref reasonixHit, hit, ref clamped);
                if (latest is null || status.UpdatedUtc > latest) latest = status.UpdatedUtc;
            }
        }

        return new DeepSeekCacheStats(
            codexRequests, codexHit, codexInput,
            reasonixTasks, reasonixHit, reasonixInput,
            latest, scanned, skipped, corrupt, unreadable, clamped,
            rangeLabel);
    }

    /// <summary>
    /// 历史回填（D1–D3）：仅当状态缺 ExecutionModel 时尝试，用严格证据白名单（session meta/文件名、
    /// manifest executionModel|model、REVIEW_PACKET 独立 Model 行）判断；报告正文、默认模型、项目名、
    /// 任务名不作为证据；明确非 DeepSeek 记已确认但不补写；证据冲突或无法确认则安全跳过。
    /// 补写原子、保留所有原字段，只写 ExecutionModelEvidence 证据类型。幂等：已补写不重复执行。
    /// </summary>
    public ReasonixBackfillResult BackfillReasonixExecutionModel(CancellationToken cancellationToken = default)
    {
        var scanned = 0; var backfilled = 0; var alreadyNew = 0; var nonDeepSeek = 0; var unconfirmed = 0; var corrupt = 0;
        if (string.IsNullOrWhiteSpace(reasonixTasksDirectory) || !Directory.Exists(reasonixTasksDirectory))
            return new(0, 0, 0, 0, 0, 0);
        foreach (var file in Directory.EnumerateFiles(reasonixTasksDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            try
            {
                var raw = File.ReadAllText(file, Encoding.UTF8);
                var node = JsonNode.Parse(raw);
                if (node is not JsonObject obj) { corrupt++; continue; }
                var executionModel = obj["ExecutionModel"]?.GetValue<string>() ?? obj["executionModel"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(executionModel)) { alreadyNew++; continue; }   // 已是新格式，幂等跳过
                var (verdict, evidenceType, model) = ResolveEvidence(obj);
                switch (verdict)
                {
                    case "deepseek":
                        obj["ExecutionModel"] = model;
                        obj["ExecutionModelEvidence"] = evidenceType;   // 只写证据类型，不写路径/正文
                        AtomicFile.WriteAllText(file, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        backfilled++;
                        break;
                    case "non-deepseek":
                        nonDeepSeek++;      // 明确非 DeepSeek：已确认但不补写
                        break;
                    default:
                        unconfirmed++;      // 冲突或无法确认：安全跳过
                        break;
                }
            }
            catch { corrupt++; }            // 单个损坏文件不阻断批次
        }
        return new(scanned, backfilled, alreadyNew, nonDeepSeek, unconfirmed, corrupt);
    }

    private static (string Verdict, string? EvidenceType, string? Model) ResolveEvidence(JsonObject obj)
    {
        var projectRoot = obj["ProjectRoot"]?.GetValue<string>();
        var taskDir = obj["TaskDirectory"]?.GetValue<string>();
        var sessionPath = obj["ReasonixSessionPath"]?.GetValue<string>();
        string? deepSeekEvidence = null, nonDeepSeekEvidence = null, modelFromEvidence = null;

        // 1) 关联 Reasonix session 文件名或 meta 中 model：仅当位于 reasonix projects 根内才读取，越界不采信。
        if (!string.IsNullOrWhiteSpace(sessionPath) && IsWithinReasonixProjects(sessionPath))
        {
            if (Path.GetFileName(sessionPath).Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                deepSeekEvidence ??= "session-filename";
            }
            var metaModel = ReadSessionMetaModel(sessionPath);
            if (!string.IsNullOrWhiteSpace(metaModel))
            {
                if (IsDeepSeekModel(metaModel)) { deepSeekEvidence ??= "session-meta"; modelFromEvidence ??= metaModel; }
                else nonDeepSeekEvidence ??= "session-meta";
            }
        }
        // 2) manifest / 3) REVIEW_PACKET：仅当 task 目录在该状态 ProjectRoot 下的 .codex-helper/runs 内才读取，越界不采信。
        if (IsValidTaskEvidencePath(projectRoot, taskDir) && !string.IsNullOrWhiteSpace(taskDir))
        {
            var manifestModel = ReadManifestModel(taskDir);
            if (!string.IsNullOrWhiteSpace(manifestModel))
            {
                if (IsDeepSeekModel(manifestModel)) { deepSeekEvidence ??= "manifest"; modelFromEvidence ??= manifestModel; }
                else nonDeepSeekEvidence ??= "manifest";
            }
            var packetModel = ReadReviewPacketModel(taskDir);
            if (!string.IsNullOrWhiteSpace(packetModel))
            {
                if (IsDeepSeekModel(packetModel)) { deepSeekEvidence ??= "review-packet-model"; modelFromEvidence ??= packetModel; }
                else nonDeepSeekEvidence ??= "review-packet-model";
            }
        }
        if (deepSeekEvidence is not null && nonDeepSeekEvidence is not null) return ("conflict", null, null);
        if (deepSeekEvidence is not null) return ("deepseek", deepSeekEvidence, modelFromEvidence);
        if (nonDeepSeekEvidence is not null) return ("non-deepseek", null, null);
        return ("unconfirmed", null, null);
    }

    /// <summary>task 证据路径必须位于该状态 ProjectRoot 的 .codex-helper/runs 内，否则不读取。</summary>
    private static bool IsValidTaskEvidencePath(string? projectRoot, string? taskDir)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(taskDir)) return false;
        try
        {
            var runs = Path.Combine(Path.GetFullPath(projectRoot), ".codex-helper", "runs");
            return PathSafety.IsWithin(Path.GetFullPath(taskDir), runs);
        }
        catch { return false; }
    }

    /// <summary>Reasonix 会话证据只允许位于 reasonix projects 根（%APPDATA%\reasonix\projects，可被 CODEX_HELPER_REASONIX_HOME 覆盖）内。</summary>
    private static bool IsWithinReasonixProjects(string sessionPath)
    {
        try
        {
            var projectsRoot = ReasonixProjectsRoot();
            return !string.IsNullOrEmpty(projectsRoot) && PathSafety.IsWithin(Path.GetFullPath(sessionPath), projectsRoot);
        }
        catch { return false; }
    }

    private static string ReasonixProjectsRoot()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HELPER_REASONIX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "reasonix");
            return Path.Combine(home, "projects");
        }
        catch { return string.Empty; }
    }

    private static bool IsDeepSeekModel(string model)
        => !string.IsNullOrWhiteSpace(model) && model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    private static string ReadSessionMetaModel(string? sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) return string.Empty;
        foreach (var meta in new[] { sessionPath + ".jsonl.meta", sessionPath + ".meta" })
        {
            if (!File.Exists(meta)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(meta, Encoding.UTF8)) is JsonObject metaObject &&
                    metaObject["model"]?.GetValue<string>() is { } model) return model;
            }
            catch { }
        }
        return string.Empty;
    }

    private static string ReadManifestModel(string taskDir)
    {
        var path = Path.Combine(taskDir, "manifest.json");
        if (!File.Exists(path)) return string.Empty;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is JsonObject manifest)
            {
                if (manifest["executionModel"]?.GetValue<string>() is { } executionModel) return executionModel;
                if (manifest["model"]?.GetValue<string>() is { } model) return model;
            }
        }
        catch { }
        return string.Empty;
    }

    private static string ReadReviewPacketModel(string taskDir)
    {
        var path = Path.Combine(taskDir, "REVIEW_PACKET.md");
        if (!File.Exists(path)) return string.Empty;
        try
        {
            // 整行锚定：只接受行首（允许前导空白）的 `Model:` 或 `- Model:` 前缀，
            // 绝不匹配 `NotModel:` 或正文中间出现的 "Model:" 字符串。
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var trimmed = line.TrimStart();
                string? value;
                if (trimmed.StartsWith("- Model:", StringComparison.OrdinalIgnoreCase))
                    value = trimmed["- Model:".Length..].Trim();
                else if (trimmed.StartsWith("Model:", StringComparison.OrdinalIgnoreCase))
                    value = trimmed["Model:".Length..].Trim();
                else continue;
                if (value.Length is > 0 and < 256) return value;
            }
        }
        catch { }
        return string.Empty;
    }

    private static long ClampNonNegative(long value, ref int clamped)
    {
        if (value < 0) { clamped++; return 0; }
        return value;
    }

    /// <summary>饱和加法：防止恶意/损坏极大数值使聚合回绕为负数；溢出时夹到 long.MaxValue 并计诊断。</summary>
    private static void AddClamped(ref long total, long value, ref int clamped)
    {
        if (value <= 0) return;
        if (long.MaxValue - total < value) { total = long.MaxValue; clamped++; }
        else total += value;
    }

    /// <summary>
    /// 会话级 DeepSeek 判定：只扫描有界头部。provider 或 model 不区分大小写包含
    /// "deepseek" 即命中（覆盖 deepseek_plan_worker、deepseek-v4-flash/pro 及未来变体）；
    /// 旧 "responses_subagent" provider 必须结合 turn_context.model 含 deepseek 才计入，
    /// 避免把非 DeepSeek 的旧 provider 误算；普通 OpenAI 会话不计入。
    /// 使用显式共享读取流（FileShare.ReadWrite|Delete），活跃 JSONL 也可在识别阶段读取；
    /// 保持流式读取与 BOM 支持。头部识别与正文统计各开一次共享流。
    /// </summary>
    private static bool IsDeepSeekSession(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        const int maximumHeaderLines = 64;
        const int maximumHeaderCharacters = 256 * 1024;
        var characters = 0;
        var sawDeepSeekModel = false;
        var sawDeepSeekProvider = false;
        var sawResponsesSubagent = false;
        for (var lineNumber = 0; lineNumber < maximumHeaderLines; lineNumber++)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            characters += line.Length;
            if (characters > maximumHeaderCharacters) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                // 根或 payload 不是对象（null/数组/标量）时不尝试读取，直接跳过该行继续扫描。
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                if (payload.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String &&
                    model.GetString() is { } modelText &&
                    modelText.Contains("deepseek", StringComparison.OrdinalIgnoreCase)) sawDeepSeekModel = true;
                if (payload.TryGetProperty("model_provider", out var provider) && provider.ValueKind == JsonValueKind.String &&
                    provider.GetString() is { } providerText)
                {
                    if (providerText.Contains("deepseek", StringComparison.OrdinalIgnoreCase)) sawDeepSeekProvider = true;
                    else if (string.Equals(providerText, "responses_subagent", StringComparison.OrdinalIgnoreCase)) sawResponsesSubagent = true;
                }
            }
            catch (JsonException) { /* 活跃 JSONL 行可能暂不完整；在有限头部内继续扫描。 */ }
        }
        return sawDeepSeekProvider || sawDeepSeekModel || (sawResponsesSubagent && sawDeepSeekModel);
    }

    /// <summary>Reasonix 任务仅在能够确认使用 DeepSeek 时纳入，避免"所有 Reasonix 都是 DeepSeek"的永久假设。</summary>
    private static bool IsDeepSeekReasonix(ReasonixTaskStatus status)
        => !string.IsNullOrWhiteSpace(status.ExecutionModel) &&
           status.ExecutionModel.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    /// <summary>单行解析，任何损坏只返回 false，绝不中断同文件后续行的统计。</summary>
    private static bool TryReadUsage(string line, out long input, out long cached)
    {
        input = cached = 0;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            // 每一级在 TryGetProperty 前验证父节点是对象，且 type 必须为 String、
            // 数值字段必须为 Number 且可读 Int64。任何 null/数组/字符串/布尔/异常形态返回 false。
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "event_msg") return false;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return false;
            if (!payload.TryGetProperty("type", out var eventType) || eventType.ValueKind != JsonValueKind.String || eventType.GetString() != "token_count") return false;
            if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return false;
            if (!info.TryGetProperty("last_token_usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return false;
            if (!usage.TryGetProperty("input_tokens", out var inputs) || inputs.ValueKind != JsonValueKind.Number || !inputs.TryGetInt64(out input)) return false;
            if (usage.TryGetProperty("cached_input_tokens", out var cachedTokens) && cachedTokens.ValueKind == JsonValueKind.Number)
                cachedTokens.TryGetInt64(out cached);
            return input > 0;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>
/// 历史回填结果分类（D4）：扫描数、已补写、已是新格式、明确非 DeepSeek、无法确认/冲突、损坏/不可读。
/// 不含任何路径、正文、命令或秘密。
/// </summary>
public sealed record ReasonixBackfillResult(
    int Scanned, int Backfilled, int AlreadyNewFormat, int NonDeepSeek, int Unconfirmed, int CorruptOrUnreadable)
{
    public string ToDisplayText()
    {
        var lines = new List<string>
        {
            $"扫描 {Scanned} 个 Reasonix 状态文件：已补写 {Backfilled}，已是新格式 {AlreadyNewFormat}，明确非 DeepSeek {NonDeepSeek}，无法确认/冲突 {Unconfirmed}，损坏或不可读 {CorruptOrUnreadable}。",
            "回填只采用严格证据（session 模型、manifest executionModel/model、Review Packet 独立 Model 行）；自由文本、默认模型、项目名、任务名不作为证据。"
        };
        return string.Join("\n", lines);
    }
}

/// <summary>
/// 聚合统计结果，来源拆分：Codex 会话请求数与 Reasonix 任务数分开计量；
/// 附带扫描/跳过/损坏/夹取的诊断数量。不含任何路径、正文、命令或秘密。
/// </summary>
public sealed record DeepSeekCacheStats(
    long CodexRequests, long CodexHitTokens, long CodexInputTokens,
    long ReasonixTaskCount, long ReasonixHitTokens, long ReasonixInputTokens,
    DateTime? LastRecordedUtc,
    int ScannedFiles, int SkippedFiles, int CorruptFiles, int UnreadableFiles, int ClampedValues,
    string? RangeLabel = null)
{
    /// <summary>动态范围文案；未指定时兼容旧默认“最近 14 天”。</summary>
    public string Range => string.IsNullOrWhiteSpace(RangeLabel) ? "最近 14 天" : RangeLabel;
    /// <summary>聚合采用饱和加法，极端输入也不会回绕为负；命中率保证 0–100%。</summary>
    public long HitTokens => SaturatingAdd(CodexHitTokens, ReasonixHitTokens);
    public long TotalInputTokens => SaturatingAdd(CodexInputTokens, ReasonixInputTokens);
    public long MissTokens { get { var total = TotalInputTokens; return total - Math.Min(HitTokens, total); } }
    public decimal HitRatePercent { get { var total = TotalInputTokens; if (total <= 0) return 0m; var hit = Math.Min(HitTokens, total); return Math.Round(hit * 100m / total, 1); } }
    public bool HasData => TotalInputTokens > 0 || CodexRequests > 0 || ReasonixTaskCount > 0;
    /// <summary>真正异常计数：损坏/不可读/数值越界。正常过滤（非 DeepSeek、缺用量）不计入。</summary>
    public int IssueCount => CorruptFiles + UnreadableFiles + ClampedValues;

    private static long SaturatingAdd(long a, long b)
    {
        if (a <= 0) return b;
        if (b <= 0) return a;
        return long.MaxValue - a < b ? long.MaxValue : a + b;
    }

    public string ToDisplayText()
    {
        var lines = new List<string>();
        if (!HasData)
        {
            lines.Add($"{Range}未发现 DeepSeek 用量记录。");
            lines.Add("请检查：是否用 DeepSeek（如 deepseek-v4-flash / deepseek-v4-pro）实际完成过任务（本机 Codex 会话或 Reasonix 任务），且相关数据目录存在。DeepSeek 无需设为 Codex 主模型，只要实际使用过即可被统计。");
            lines.Add(SourceLine());
            return string.Join("\n", lines);
        }

        var time = LastRecordedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
        lines.Add($"{Range}：总输入 {TotalInputTokens:N0} tokens，缓存命中 {HitTokens:N0} tokens（{HitRatePercent:0.#}%），未命中 {MissTokens:N0} tokens。最近记录：{time}。");
        lines.Add($"  · Codex 会话：{CodexRequests} 次请求，输入 {CodexInputTokens:N0}，命中 {CodexHitTokens:N0}");
        lines.Add($"  · Reasonix 任务：{ReasonixTaskCount} 个，输入 {ReasonixInputTokens:N0}，命中 {ReasonixHitTokens:N0}");
        lines.Add(SourceLine());
        if (SkippedFiles > 0) lines.Add($"  · 已过滤 {SkippedFiles} 条非 DeepSeek 或缺少用量记录的项目");

        if (IssueCount > 0)
        {
            var details = new List<string>();
            if (CorruptFiles > 0) details.Add($"{CorruptFiles} 个文件损坏");
            if (UnreadableFiles > 0) details.Add($"{UnreadableFiles} 个文件不可访问");
            if (ClampedValues > 0) details.Add($"{ClampedValues} 处数值越界已夹取");
            lines.Add($"\n⚠ 部分数据不可用：{string.Join("、", details)}，已自动忽略；以下为可用结果。");
        }
        return string.Join("\n", lines);
    }

    private string SourceLine()
        => "统计同时读取本机 Codex 会话用量与 Helper 的 Reasonix 任务统计，不读取密钥、不拦截请求、不展示文件路径或对话内容。";
}
