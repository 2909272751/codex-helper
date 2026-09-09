using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 阶段四 remediation 上下文：只含绝对项目根/任务目录/任务 ID/合同指纹（命令行与记录绝不带合同正文/凭据）。
/// </summary>
public sealed record HarnessRemediationContext(
    string ProductProjectRoot,
    string TaskDirectory,
    string TaskId,
    string? ContractFingerprint);

/// <summary>
/// triage 线程在任务目录内落盘的账本 GPT_REMEDIATION.json（UTF-8 无 BOM 原子写）。
/// 字段：schema、taskId、fingerprint、verdict、安全摘要、修复范围摘要、UTC；
/// 绝不含合同正文/凭据/token/DSH 消息正文。
/// </summary>
public sealed record GptRemediationRecord(
    int SchemaVersion,
    string TaskId,
    string? ContractFingerprint,
    string Verdict,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime VerdictUtc,
    string? SafeSummary,
    string? ScopeSummary);

/// <summary>GPT_REMEDIATION.json 常量与宽容原子读写宿主（显式 key 映射，见 <see cref="GptLedgerJson"/>）。</summary>
public static class GptRemediationStore
{
    public const string RecordFileName = "GPT_REMEDIATION.json";
    public const int RecordSchemaVersion = 1;

    // triage verdict（本机 schema/App Server 可用时由独立验收线程判读后落盘）
    public const string VerdictProductRepair = "product-repair";
    public const string VerdictHelperRepair = "helper-repair";
    public const string VerdictNoRepairNeeded = "no-repair-needed";
    public const string VerdictIndeterminate = "remediation-indeterminate";
    public const string VerdictNeedsUser = "needs-user";

    public static string RecordPath(string taskDirectory) => Path.Combine(taskDirectory, RecordFileName);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    /// <summary>宽容读取（绝不抛异常）：显式同义 key 投影；taskId/fingerprint 严格匹配由协调器强制。</summary>
    public static GptRemediationRecord? TryReadRecord(string taskDirectory)
    {
        var path = RecordPath(taskDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null) return null;
            return new GptRemediationRecord(
                GptLedgerJson.GetInt(node, "schema") ?? RecordSchemaVersion,
                GptLedgerJson.GetText(node, "taskId") ?? string.Empty,
                GptLedgerJson.GetText(node, "fingerprint"),
                GptLedgerJson.GetText(node, "verdict") ?? string.Empty,
                GptLedgerJson.GetUtc(node, "verdictUtc") ?? DateTime.UnixEpoch,
                GptLedgerJson.GetText(node, "safeSummary"),
                GptLedgerJson.GetText(node, "scopeSummary"));
        }
        catch { return null; }
    }

    /// <summary>原子写入（UTF-8 无 BOM；稳定 schema/taskId/fingerprint/… key 集合）。</summary>
    public static void WriteRecord(string taskDirectory, GptRemediationRecord record)
    {
        var json = new JsonObject
        {
            ["schema"] = record.SchemaVersion,
            ["taskId"] = record.TaskId,
            ["fingerprint"] = record.ContractFingerprint,
            ["verdict"] = record.Verdict,
            ["verdictUtc"] = GptLedgerJson.FormatUtc(record.VerdictUtc),
            ["safeSummary"] = record.SafeSummary,
            ["scopeSummary"] = record.ScopeSummary
        };
        AtomicFile.WriteAllText(RecordPath(taskDirectory), json.ToJsonString(WriteOptions));
    }
}

/// <summary>
/// 阶段四修复编排记录（任务目录 HARNESS_REMEDIATION.json）：状态机
/// needs-repair|acceptance-failed（触发）→ triage-queued → triage-running →
/// product-repair-queued | helper-repair-queued | no-repair-needed | remediation-indeterminate →
/// repair-running → revalidation-running → accepted | remediation-failed | needs-user。
/// 每个 taskId+指纹最多一次 round（triage 一次 / 修复合同一次 / 独立复验一次）；
/// 只含调度事实（两绝对路径、指纹、状态、round、UTC、启动器 PID+启动时间、verdict、修复合同目录、
/// 修复合同指纹、安全摘要），不含合同正文/凭据/聊天/推理内容；写入一律 UTF-8 无 BOM 原子。
/// </summary>
public sealed record HarnessRemediationRecord(
    int SchemaVersion,
    string TaskId,
    string ProjectRoot,
    string TaskDirectory,
    string? ContractFingerprint,
    string State,
    int Round,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime CreatedUtc,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime UpdatedUtc,
    int? LauncherPid = null,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime? LauncherStartedUtc = null,
    string? Verdict = null,
    string? RepairTaskDirectory = null,
    string? RepairFingerprint = null,
    string? SafeSummary = null)
{
    public bool IsTerminal => HarnessRemediationStore.IsTerminalState(State);
}

/// <summary>HARNESS_REMEDIATION.json 的状态常量与宽容原子读写宿主。</summary>
public static class HarnessRemediationStore
{
    public const string RecordFileName = "HARNESS_REMEDIATION.json";
    public const int RecordSchemaVersion = 1;
    public const int MaxRoundsPerTask = 1;

    // 状态机
    public const string TriageQueuedState = "triage-queued";
    public const string TriageRunningState = "triage-running";
    public const string ProductRepairQueuedState = "product-repair-queued";
    public const string HelperRepairQueuedState = "helper-repair-queued";
    public const string NoRepairNeededState = "no-repair-needed";
    public const string IndeterminateState = "remediation-indeterminate";
    public const string RepairRunningState = "repair-running";
    public const string RevalidationRunningState = "revalidation-running";
    public const string AcceptedState = "accepted";
    public const string RemediationFailedState = "remediation-failed";
    public const string NeedsUserState = "needs-user";

    public static string RecordPath(string taskDirectory) => Path.Combine(taskDirectory, RecordFileName);

    public static bool IsTerminalState(string? state)
        => string.Equals(state, AcceptedState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, RemediationFailedState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, NeedsUserState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, NoRepairNeededState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, IndeterminateState, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    public static HarnessRemediationRecord? TryReadRecord(string taskDirectory)
    {
        if (string.IsNullOrWhiteSpace(taskDirectory)) return null;
        var path = RecordPath(taskDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<HarnessRemediationRecord>(text, Options);
        }
        catch { return null; }
    }

    public static void WriteRecord(HarnessRemediationRecord record)
        => AtomicFile.WriteAllText(RecordPath(record.TaskDirectory), JsonSerializer.Serialize(record, Options));

    /// <summary>跨进程单飞占位：CreateNew 独占创建首条 triage-queued 记录；并发/重启/重复文件事件
    /// 后到者返回 null（绝不第二轮）。</summary>
    public static HarnessRemediationRecord? TryClaimQueued(HarnessRemediationRecord template)
    {
        var path = RecordPath(template.TaskDirectory);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(template, Options));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return template;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>更新记录（保留审计；更新 UTC/启动器身份/可选 verdict/修复目录/指纹/安全摘要）。</summary>
    public static HarnessRemediationRecord Update(HarnessRemediationRecord record, string state,
        int? launcherPid = null, DateTime? launcherStartedUtc = null, string? verdict = null,
        string? repairTaskDirectory = null, string? repairFingerprint = null, string? safeSummary = null)
    {
        var updated = record with
        {
            State = state,
            LauncherPid = launcherPid ?? record.LauncherPid,
            LauncherStartedUtc = launcherStartedUtc ?? record.LauncherStartedUtc,
            Verdict = verdict ?? record.Verdict,
            RepairTaskDirectory = repairTaskDirectory ?? record.RepairTaskDirectory,
            RepairFingerprint = repairFingerprint ?? record.RepairFingerprint,
            SafeSummary = safeSummary ?? record.SafeSummary,
            UpdatedUtc = DateTime.UtcNow
        };
        WriteRecord(updated);
        return updated;
    }
}
