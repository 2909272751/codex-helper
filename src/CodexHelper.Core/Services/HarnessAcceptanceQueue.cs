using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 阶段一“本地自动验收编排”的任务目录事件记录（HARNESS_ACCEPTANCE.json）。
/// 只保存调度事实：任务 ID、合同指纹、队列状态、尝试编号、创建/更新 UTC、启动器 PID（若有）与
/// 终态/失败原因的安全摘要；绝不包含合同正文、凭据、token 或聊天内容。
/// 状态契约（为阶段二预留，本阶段绝不把 DSH 完成等同于产品验收通过）：
/// awaiting-gpt（尚未排队；无记录等价于此态）→ acceptance-queued → acceptance-running →
/// accepted / needs-repair / acceptance-failed。记录路径始终绝对，写入一律 UTF-8 无 BOM
/// （<see cref="AtomicFile"/>）；“无记录 → acceptance-queued”的首次占位用 FileMode.CreateNew
/// 跨进程单飞，保证同一 taskId+指纹只排队一次。
/// </summary>
public sealed record HarnessAcceptanceRecord(
    int SchemaVersion,
    string TaskId,
    string ProjectRoot,
    string TaskDirectory,
    string? ContractFingerprint,
    string QueueState,
    int AttemptNumber,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime CreatedUtc,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime UpdatedUtc,
    int? LauncherPid = null,
    string? SafeSummary = null,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime? LauncherStartedUtc = null)
{
    /// <summary>是否已到验收终态（accepted / needs-repair / acceptance-failed）。</summary>
    public bool IsTerminal => HarnessAcceptanceQueue.IsTerminalState(QueueState);
}

/// <summary>
/// 验收队列记录/启动器契约的宿主：文件名、状态契约常量与宽容原子读写。
/// 每个任务目录保存一份小型事件记录，供本地守候去重与阶段二验收驱动接回。
/// </summary>
public static class HarnessAcceptanceQueue
{
    /// <summary>任务目录内的自动验收事件记录文件名。</summary>
    public const string RecordFileName = "HARNESS_ACCEPTANCE.json";
    /// <summary>验收记录 schema 版本。</summary>
    public const int RecordSchemaVersion = 1;

    // 阶段二预留状态契约：awaiting-gpt（尚未排队）→ acceptance-queued → acceptance-running → accepted/needs-repair/acceptance-failed。
    /// <summary>尚未排队（无记录等价于此态）。</summary>
    public const string AwaitingGptState = "awaiting-gpt";
    /// <summary>已去重排队（等待独立验收驱动接手）。</summary>
    public const string QueuedState = "acceptance-queued";
    /// <summary>独立验收正在运行。</summary>
    public const string RunningState = "acceptance-running";
    /// <summary>独立验收通过。</summary>
    public const string AcceptedState = "accepted";
    /// <summary>独立验收结论：产品需修复后重新验收。</summary>
    public const string NeedsRepairState = "needs-repair";
    /// <summary>验收启动失败（可诊断；绝不伪装成通过）。</summary>
    public const string FailedState = "acceptance-failed";

    public static string RecordPath(string taskDirectory) => Path.Combine(taskDirectory, RecordFileName);

    public static bool IsTerminalState(string? state)
        => string.Equals(state, AcceptedState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, NeedsRepairState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, FailedState, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    /// <summary>宽容读取验收记录；缺失/损坏返回 null（绝不抛异常）。</summary>
    public static HarnessAcceptanceRecord? TryReadRecord(string taskDirectory)
    {
        if (string.IsNullOrWhiteSpace(taskDirectory)) return null;
        var path = RecordPath(taskDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<HarnessAcceptanceRecord>(text, Options);
        }
        catch { return null; }
    }

    /// <summary>原子写入验收记录（UTF-8 无 BOM；失败上抛，绝不静默丢记录）。</summary>
    public static void WriteRecord(HarnessAcceptanceRecord record)
        => AtomicFile.WriteAllText(RecordPath(record.TaskDirectory), JsonSerializer.Serialize(record, Options));

    /// <summary>
    /// 跨进程单飞占位：任务目录内尚无验收记录时用 FileMode.CreateNew 独占创建“acceptance-queued”记录。
    /// 并发/重复文件事件/重启读取同一记录时，后到者在此失败并返回 null，由调用方重新读取既有记录，
    /// 绝不重复排队/重复启动。
    /// </summary>
    public static HarnessAcceptanceRecord? TryClaimQueued(HarnessAcceptanceRecord template)
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

    /// <summary>更新记录状态（保留审计事实；更新 UTC 与可选启动器 PID/启动时间/安全摘要）。</summary>
    public static HarnessAcceptanceRecord Update(HarnessAcceptanceRecord record, string state, int? launcherPid = null,
        string? safeSummary = null, DateTime? launcherStartedUtc = null)
    {
        var updated = record with
        {
            QueueState = state,
            LauncherPid = launcherPid ?? record.LauncherPid,
            LauncherStartedUtc = launcherStartedUtc ?? record.LauncherStartedUtc,
            SafeSummary = safeSummary ?? record.SafeSummary,
            UpdatedUtc = DateTime.UtcNow
        };
        WriteRecord(updated);
        return updated;
    }

    /// <summary>安全化文本：脱敏常见凭据片段并截断；绝不把原始正文/密钥写入记录或摘要。</summary>
    public static string Safe(string? text, int maxLength = 400)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var redacted = ReasonixIntegrationService.RedactSecrets(text);
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }
}

/// <summary>
/// 独立“验收启动器”上下文：只交给绝对项目根、任务目录、任务 ID、验收记录路径与合同指纹。
/// 绝不携带合同正文、API Key、Token、Cookie 或 DSH 消息正文（阶段二验收驱动据此从文件读取所需内容）。
/// </summary>
public sealed record HarnessAcceptanceLaunchContext(
    string ProjectRoot,
    string TaskDirectory,
    string TaskId,
    string RecordPath,
    string? ContractFingerprint);

/// <summary>启动器结果：State 只允许 accepted / needs-repair；SafeSummary 为脱敏摘要（可省略）。</summary>
public sealed record HarnessAcceptanceLauncherResult(string State, string? SafeSummary);

/// <summary>
/// 注入式“验收启动器”接口：阶段一使用测试替身即可，生产默认不接线
/// （本阶段绝不实际调用 Codex 模型、绝不消耗用户额度；真实验收驱动由阶段二独立合同实现）。
/// </summary>
public interface IHarnessAcceptanceLauncher
{
    /// <summary>执行一次独立验收；异常由协调器落盘 acceptance-failed（绝不伪装成功）。</summary>
    Task<HarnessAcceptanceLauncherResult> LaunchAsync(HarnessAcceptanceLaunchContext context, CancellationToken cancellationToken);
}
