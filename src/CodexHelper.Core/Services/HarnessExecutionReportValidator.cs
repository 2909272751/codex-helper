using System.Text;

namespace CodexHelper.Core.Services;

/// <summary>
/// 校验当前合同的 EXECUTION_REPORT.md。报告必须属于当前任务与合同、晚于任务开始时间，
/// 并包含固定键、成功退出码、修改文件、workerChecks 和风险说明。
/// </summary>
public static class HarnessExecutionReportValidator
{
    public const string TaskIdKey = "任务 ID";
    public const string FingerprintKey = "合同指纹";
    public const string ExitCodeKey = "退出码";
    public const string ModifiedFilesKey = "修改文件";
    public const string WorkerChecksKey = "workerChecks";
    public const string RisksKey = "风险/未完成项";

    public sealed record ValidationResult(bool Valid, string Reason)
    {
        public static ValidationResult Ok() => new(true, "通过");
        public static ValidationResult Fail(string reason) => new(false, reason);
    }

    public static ValidationResult Validate(
        string taskDirectory,
        string taskId,
        string? contractFingerprint,
        DateTime taskStartedUtc)
    {
        if (string.IsNullOrWhiteSpace(contractFingerprint))
            return ValidationResult.Fail("任务状态缺少合同指纹，无法验证报告归属");

        var reportPath = Path.Combine(taskDirectory, "EXECUTION_REPORT.md");
        if (!File.Exists(reportPath))
            return ValidationResult.Fail("EXECUTION_REPORT.md 缺失");

        DateTime lastWriteUtc;
        try { lastWriteUtc = File.GetLastWriteTimeUtc(reportPath); }
        catch (Exception ex) { return ValidationResult.Fail("EXECUTION_REPORT.md 不可读（" + Truncate(ex.Message) + "）"); }
        if (lastWriteUtc < taskStartedUtc.AddSeconds(-2))
            return ValidationResult.Fail("EXECUTION_REPORT.md 早于任务开始时间，属于陈旧报告");

        string text;
        try { text = File.ReadAllText(reportPath, Encoding.UTF8); }
        catch (Exception ex) { return ValidationResult.Fail("EXECUTION_REPORT.md 不可读（" + Truncate(ex.Message) + "）"); }
        if (string.IsNullOrWhiteSpace(text))
            return ValidationResult.Fail("EXECUTION_REPORT.md 为空");

        var reportedTaskId = FindValue(text, TaskIdKey);
        if (!string.Equals(reportedTaskId, taskId, StringComparison.Ordinal))
            return ValidationResult.Fail(reportedTaskId is null ? "报告缺少任务 ID" : "报告任务 ID 与当前任务不一致");

        var reportedFingerprint = FindValue(text, FingerprintKey);
        if (!string.Equals(reportedFingerprint, contractFingerprint, StringComparison.Ordinal))
            return ValidationResult.Fail(reportedFingerprint is null ? "报告缺少合同指纹" : "报告合同指纹与当前合同不一致");

        var exitCodeText = FindValue(text, ExitCodeKey);
        if (!int.TryParse(exitCodeText, out var exitCode))
            return ValidationResult.Fail("报告退出码缺失或不是整数");
        if (exitCode != 0)
            return ValidationResult.Fail("workerChecks 退出码非零（" + exitCode + "）");

        if (FindValue(text, ModifiedFilesKey) is null)
            return ValidationResult.Fail("报告缺少修改文件");
        if (FindValue(text, WorkerChecksKey) is null)
            return ValidationResult.Fail("报告缺少 workerChecks 结果");
        if (FindValue(text, RisksKey) is null)
            return ValidationResult.Fail("报告缺少风险/未完成项");

        return ValidationResult.Ok();
    }

    private static string? FindValue(string text, string key)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var line = rawLine.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal)) continue;
            var body = line[2..].TrimStart();
            if (!body.StartsWith(key, StringComparison.Ordinal)) continue;
            var rest = body[key.Length..];
            if (rest.Length == 0 || rest[0] is not ('：' or ':')) continue;
            var inline = rest[1..].Trim();
            // 行内值非空（单行写法，如“- 修改文件：src/...”）直接返回。
            if (inline.Length > 0) return inline;
            // 行内值为空时支持缩进 Markdown 列表（“- 修改文件：” 后跟一个或多个缩进子条目）。
            // 收集后续“以空白开头”的连续内容行，直到遇到下一个非缩进的“- ”条目；内容非空即视为该键已填入。
            var collected = new StringBuilder();
            for (var next = index + 1; next < lines.Length; next++)
            {
                var nextLine = lines[next];
                if (nextLine.Length == 0) { if (collected.Length > 0) break; continue; }
                if (char.IsWhiteSpace(nextLine[0]))
                {
                    var text2 = nextLine.Trim();
                    if (text2.Length > 0)
                    {
                        if (collected.Length > 0) collected.Append('\n');
                        collected.Append(text2);
                    }
                    continue;
                }
                break;
            }
            return collected.Length == 0 ? null : collected.ToString();
        }
        return null;
    }

    private static string Truncate(string message)
        => message.Length <= 120 ? message : message[..120] + "…";
}
