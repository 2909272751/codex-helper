using System.Text;
using System.Text.RegularExpressions;

namespace CodexHelper.Core.Services;

/// <summary>
/// 校验当前合同的 EXECUTION_REPORT.md。报告必须属于当前任务与合同、晚于任务开始时间，
/// 并包含修改文件、workerChecks 与未完成/风险说明。报告可用两种结构：固定键格式
/// （“- 任务 ID：…”这类 dash-prefixed 键）或 Web-composer/DSH 语义标题格式
/// （“## 实际修改 / ## 验证结果 / ## 未完成项与说明”）。身份与时效校验不变；
/// 标题格式必须有明确成功证据（exit 0 / 退出码 0），无退出码或非零退出码一律不放行。
/// </summary>
public static class HarnessExecutionReportValidator
{
    public const string TaskIdKey = "任务 ID";
    public const string FingerprintKey = "合同指纹";
    public const string ExitCodeKey = "退出码";
    public const string ModifiedFilesKey = "修改文件";
    public const string WorkerChecksKey = "workerChecks";
    public const string RisksKey = "风险/未完成项";

    // DSH / Web-composer 语义标题的同义集合：修改文件、验证结果（含 workerChecks）、未完成/风险。
    private static readonly string[] ModifiedHeadings =
        ["## 实际修改", "## 修改文件", "## 本次修改", "## 变更文件"];
    private static readonly string[] VerificationHeadings =
        ["## 验证结果", "## workerChecks", "## Worker Checks", "## 验证与检查"];
    private static readonly string[] CompletionHeadings =
        ["## 未完成项与说明", "## 未完成项", "## 风险与未完成项", "## 风险", "## 未完成项及说明"];

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

        // 身份与时效保持严格：两个不透明值必须原样出现在这份新鲜报告中（明文子串；
        // Web-composer 在行内用反引号包裹时仍是完整子串，Ordinal Contains 即可命中）。
        if (!text.Contains(taskId, StringComparison.Ordinal))
            return ValidationResult.Fail("报告缺少任务 ID 或任务标识");
        if (!text.Contains(contractFingerprint, StringComparison.Ordinal))
            return ValidationResult.Fail("报告缺少或不匹配合同指纹");

        // 标题式（DSH/Web-composer）报告：三类语义标题（修改/验证/未完成）各任一个命中即按标题语义走；
        // 但必须有明确的成功证据（exit 0），绝不因标题好看而放行无退出码/失败退出码。
        if (HasAny(ModifiedHeadings, text) && HasAny(VerificationHeadings, text) && HasAny(CompletionHeadings, text))
        {
            if (HasExplicitSuccess(text)) return ValidationResult.Ok();
            var nonZero = FindNonZeroExit(text);
            return nonZero is not null
                ? ValidationResult.Fail("报告退出码非零（" + nonZero + "），不能判定通过")
                : ValidationResult.Fail("报告缺少明确成功证据（exit 0/退出码 0），不能判定通过");
        }

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

    private static bool HasAny(string[] headings, string text)
        => headings.Any(heading => text.Contains(heading, StringComparison.Ordinal));

    /// <summary>
    /// 标题式报告的明确成功证据：报告必须自述 exit 0 / 退出码 0 / 全部通过（含 workerChecks 通过）。
    /// 只把“成功”文本当作证据，绝不把无退出结果或缺省“0”当作成功。
    /// </summary>
    private static bool HasExplicitSuccess(string text)
    {
        var normalized = text.Replace("：", ":").Replace("。", " ").Replace("，", " ");
        if (Regex.IsMatch(normalized, @"(?i)exit\s+code\s*[:=]\s*0(?![0-9])") || Regex.IsMatch(normalized, @"(?i)exit\s+0\b"))
            return true;
        if (Regex.IsMatch(normalized, @"退出码\s*[:=为]?\s*0(?![0-9])"))
            return true;
        if (Regex.IsMatch(normalized, @"(?i)worker\s*checks?\s*[:：]?\s*全部通过"))
            return true;
        return false;
    }

    /// <summary>提取标题式报告中的非零退出码文本（exit 1 / 退出码 1 等）；无则返回 null。</summary>
    private static string? FindNonZeroExit(string text)
    {
        var normalized = text.Replace("：", ":").Replace("。", " ").Replace("，", " ");
        var match = Regex.Match(normalized, @"(?i)(?:exit\s+(?:code\s*[:=]\s*)?|退出码\s*[:=为]?\s*)(\d+)");
        if (!match.Success) return null;
        var code = match.Groups[1].Value;
        return int.TryParse(code, out var value) && value != 0 ? code : null;
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
