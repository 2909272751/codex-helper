using System.Text;
using System.Text.Json;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 漏报告自动恢复证据：由托管 runner 在 missing-report 场景（Reasonix 退出码 0 且未生成
/// EXECUTION_REPORT.md）持久化到任务目录 <see cref="ReasonixAutoRecovery.EvidenceFileName"/>，
/// Helper 读取后据此判定是否可自动恢复。证据只包含脱敏的结构化事实，不含命令正文或秘密。
/// </summary>
public sealed record ReasonixAutoRecoveryEvidence(
    bool HasActivity,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> PassedChecks,
    int StepCount,
    int ToolCallCount);

/// <summary>
/// P0-1 漏报告自动恢复（纯函数 + 报告生成，供测试与展示）：
/// 当 Reasonix 退出码为 0、存在实际模型/工具活动，并且能证明存在本次新增 diff 或 workerChecks
/// 已通过，但缺少 EXECUTION_REPORT.md 时，由 Helper 生成最小、明确标注为自动恢复的执行报告和
/// Review Packet，把任务置为等待 GPT 验收的完成态。
/// 绝不伪造测试通过；没有活动、没有改动、模型失败（model-run-failed）或非零退出（cli-exit）
/// 一律不恢复。
/// </summary>
public static class ReasonixAutoRecovery
{
    /// <summary>runner 在 missing-report 场景写入的证据文件名（任务目录内）。</summary>
    public const string EvidenceFileName = "auto-recovery-evidence.json";

    /// <summary>宽容读取证据文件；缺失、损坏或字段非法一律返回 null，绝不抛异常。</summary>
    public static ReasonixAutoRecoveryEvidence? TryLoadEvidence(string? taskDirectory)
    {
        if (string.IsNullOrWhiteSpace(taskDirectory)) return null;
        try
        {
            var path = Path.Combine(taskDirectory, EvidenceFileName);
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(
                File.ReadAllText(path, Encoding.UTF8),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var hasActivity = root.TryGetProperty("hasActivity", out var act) && act.ValueKind == JsonValueKind.True;
            var changed = ReadStringArray(root, "changedFiles");
            var passed = ReadStringArray(root, "passedChecks");
            var steps = root.TryGetProperty("stepCount", out var sc) && sc.TryGetInt32(out var stepValue) ? stepValue : 0;
            var tools = root.TryGetProperty("toolCallCount", out var tc) && tc.TryGetInt32(out var toolValue) ? toolValue : 0;
            return new ReasonixAutoRecoveryEvidence(hasActivity, changed, passed, Math.Max(0, steps), Math.Max(0, tools));
        }
        catch { return null; }
    }

    /// <summary>
    /// 是否满足自动恢复条件（全部满足才恢复，反例绝不误判成功）：
    /// 1) 任务仍为 failed 且失败类型为 missing-report（runner 只在退出码 0 且非模型失败时设置该类型，
    ///    因此隐含“非零退出/模型失败不恢复”）；
    /// 2) 证据存在且确有实际模型/工具活动；
    /// 3) 能证明存在本次新增 diff（changedFiles）或 workerChecks 已通过（passedChecks，已排除视觉/GPT 项）。
    /// </summary>
    public static bool ShouldRecover(ReasonixTaskStatus status, ReasonixAutoRecoveryEvidence? evidence)
    {
        if (status is null) return false;
        if (!string.Equals(status.State, "failed", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(status.FailureKind, "missing-report", StringComparison.OrdinalIgnoreCase)) return false;
        if (evidence is null) return false;
        if (!evidence.HasActivity) return false;
        if (evidence.ChangedFiles.Count == 0 && evidence.PassedChecks.Count == 0) return false;
        return true;
    }

    /// <summary>生成最小、明确标注为自动恢复的执行报告；声明未验证 workerChecks、未伪造测试通过。</summary>
    public static string BuildExecutionReport(ReasonixTaskStatus status, ReasonixAutoRecoveryEvidence evidence)
    {
        var files = evidence.ChangedFiles.Count > 0
            ? string.Join("；", evidence.ChangedFiles.Take(10))
            : "（无法枚举，未计入）";
        var passed = evidence.PassedChecks.Count > 0
            ? string.Join("；", evidence.PassedChecks.Take(10))
            : "（无自报通过记录）";
        return $$"""
# 执行报告（自动恢复）

- 任务/尝试：{{status.TaskId}} / 第 {{status.AttemptNumber ?? 1}} 次
- 报告来源：Codex Helper 自动恢复（Reasonix 未生成 EXECUTION_REPORT.md）
- 原因：Reasonix 退出码为 0 且存在实际模型/工具活动，并存在本次新增变化或已通过 workerChecks，但缺少执行报告。
- 活动证据：步骤 {{evidence.StepCount}} · 工具调用 {{evidence.ToolCallCount}}
- 本次新增变化文件：{{files}}
- Reasonix 自报通过的 workerChecks（仅按 PROGRESS.json 记录，尚未经 GPT 复核，已排除视觉/GPT 项）：{{passed}}

> ⚠️ 本报告由 Helper 自动恢复生成，未伪造测试通过：上面的记录只是 Reasonix 自报证据，
> 不代表 GPT 已确认 workerCheck 或测试通过。所有验收证据仍需 GPT 独立复核；仅对已有可信证据的检查避免机械重复。
""";
    }

    /// <summary>生成自动恢复版 Review Packet：明确标注自动恢复与已通过检查，引导 GPT 独立复核。</summary>
    public static string BuildReviewPacket(ReasonixTaskStatus status, ReasonixAutoRecoveryEvidence evidence)
    {
        var files = evidence.ChangedFiles.Count > 0
            ? string.Join("; ", evidence.ChangedFiles.Take(10))
            : "（无法枚举）";
        var passed = evidence.PassedChecks.Count > 0
            ? string.Join("; ", evidence.PassedChecks.Take(10))
            : "none recorded";
        return $$"""
# GPT Review Packet（自动恢复）

- Task: {{status.TaskId}}
- Attempt number: {{status.AttemptNumber ?? 1}}
- 报告来源：Codex Helper 自动恢复（Reasonix 退出码 0 但未生成 EXECUTION_REPORT.md）
- Reasonix exit code: 0
- Model turns (turn_started): {{evidence.StepCount}}
- Tool calls: {{evidence.ToolCallCount}}
- Changed files (this run): {{files}}
- Worker-reported passed checks (from PROGRESS.json; not yet verified by GPT): {{passed}}

> ⚠️ 自动恢复报告不代表 GPT 已确认 workerChecks 或测试通过。GPT 必须读取本任务目录中的
> FAILURE_REPORT.md、PROGRESS.json 与 events.jsonl，检查实际改动，并独立复核验收；
> 对已有可信证据的检查避免机械重复，视觉验收始终由 GPT 负责。
""";
    }

    /// <summary>自动恢复后的任务状态：置为等待 GPT 验收的完成态；保留 FailureKind 供 UI/审计识别来源。</summary>
    public static ReasonixTaskStatus BuildRecoveredStatus(ReasonixTaskStatus status, ReasonixAutoRecoveryEvidence evidence)
        => status with
        {
            State = "completed",
            Phase = "awaiting-gpt-review",
            UpdatedUtc = DateTime.UtcNow,
            Message = "漏报告自动恢复：Reasonix 退出码 0 且存在实际活动与本次新增变化/已通过检查，但未生成 EXECUTION_REPORT.md；Helper 已生成自动恢复报告，等待 GPT 独立验收（未伪造测试通过）。",
            FailureKind = "missing-report",
            FailureSummary = "Helper 自动恢复：未伪造测试通过，workerChecks 未验证，GPT 需独立复核。",
            ProgressStage = "done",
            ProgressSummary = "漏报告自动恢复完成，等待 GPT 独立验收。",
            RemainingPercent = 0,
            ProgressSource = "helper",
            ReturnState = string.IsNullOrWhiteSpace(status.ReturnState) ? "same-turn-resume" : status.ReturnState
        };

    /// <summary>原子写入自动恢复版 EXECUTION_REPORT.md 与 REVIEW_PACKET.md（覆盖 runner 已生成的未标注版本）。</summary>
    public static void WriteReports(ReasonixTaskStatus status, ReasonixAutoRecoveryEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(status.TaskDirectory)) return;
        AtomicFile.WriteAllText(Path.Combine(status.TaskDirectory, "EXECUTION_REPORT.md"), BuildExecutionReport(status, evidence));
        AtomicFile.WriteAllText(Path.Combine(status.TaskDirectory, "REVIEW_PACKET.md"), BuildReviewPacket(status, evidence));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array) return [];
        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                result.Add(item.GetString()!.Trim());
        return result;
    }
}
