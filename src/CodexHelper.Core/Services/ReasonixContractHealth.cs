using System.Text.RegularExpressions;

namespace CodexHelper.Core.Services;

/// <summary>合同启动前体检结果：是否阻止、阻止原因、诊断摘要、是否发生安全归一化、去重后的 workerChecks。</summary>
public sealed record ReasonixContractHealthResult(
    bool Blocked,
    string? BlockReason,
    IReadOnlyList<string> Diagnostics,
    bool Normalized,
    IReadOnlyList<string> DeduplicatedChecks);

/// <summary>
/// 合同启动前体检与安全归一化（纯函数，供测试与展示；托管 runner 的 run-reasonix-job.ps1
/// 内嵌同一规则的 PowerShell 副本用于实际执行）。检查当前任务合同（HANDOFF/manifest）的执行要求：
/// HANDOFF 是否要求 Reasonix 读取 ACCEPTANCE 或写 REVIEW_PACKET；workerChecks 是否重复或混入
/// 视觉/GUI/发布打包；普通 DeepSeek 任务是否错误使用 delivery profile（运行时仍强制 balanced）；
/// 普通 small/medium DeepSeek 任务是否显式 high/max；HANDOFF 是否缺少允许读取/允许修改/直接依赖范围。
/// 只检查当前任务合同的执行要求，不因历史文档中出现单词而误报。
/// </summary>
public static class ReasonixContractHealth
{
    private static readonly string[] NegationMarkers =
    [
        "不", "没有", "无", "禁止", "避免", "无需", "不要", "不得", "切勿",
        "don't", "do not", "should not", "must not", "never"
    ];

    // 肯定式"要求读取 ACCEPTANCE"；"read ACCEPTANCE" 或 "读取 ACCEPTANCE" 等。
    private static readonly Regex ReadAcceptancePattern = new(
        @"(?:read|reading|reads|读取|阅读|读)\s+(?:the\s+)?acceptance(?:\.md)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 肯定式"要求写 REVIEW_PACKET"；"write REVIEW_PACKET" 或 "写 REVIEW_PACKET" 等。
    private static readonly Regex WriteReviewPacketPattern = new(
        @"(?:write|writing|writes|写|写入|生成)\s+(?:the\s+)?review_?packet(?:\.md)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 肯定式"必须截图/视觉验收交付"——无法安全修正的阻止项（视觉验收归 GPT）。
    private static readonly Regex MustVisualDeliverPattern = new(
        @"(?:必须|务必|需)\s*(?:截图|进行视觉验收|视觉验收)|(?:must|required|need)\s+(?:to\s+)?(?:screenshot|take\s+screenshots|deliver\s+screenshots)|截图交付",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>执行体检：返回诊断列表、阻止标记与去重后的 workerChecks。</summary>
    public static ReasonixContractHealthResult Inspect(
        ReasonixManifestPolicy? manifest,
        string? handoffText,
        IReadOnlyList<string>? workerChecks,
        bool deepSeek)
    {
        var diagnostics = new List<string>();
        var normalized = false;

        // 1) HANDOFF 要求 Reasonix 读取 ACCEPTANCE / 写 REVIEW_PACKET：逐行检查，忽略含否定标记的约束说明。
        var handoffLines = (handoffText ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var requiresReadAcceptance = false;
        var requiresWriteReviewPacket = false;
        var requiresVisualDelivery = false;
        foreach (var line in handoffLines)
        {
            if (line.Contains("ACCEPTANCE", StringComparison.OrdinalIgnoreCase) && ReadAcceptancePattern.IsMatch(line) && !HasNegation(line))
                requiresReadAcceptance = true;
            if (line.Contains("REVIEW_PACKET", StringComparison.OrdinalIgnoreCase) && WriteReviewPacketPattern.IsMatch(line) && !HasNegation(line))
                requiresWriteReviewPacket = true;
            if (MustVisualDeliverPattern.IsMatch(line) && !HasNegation(line))
                requiresVisualDelivery = true;
        }
        if (requiresReadAcceptance)
        {
            diagnostics.Add("HANDOFF 要求 Reasonix 读取 ACCEPTANCE.md；已归一化：Reasonix 只读 SPEC/HANDOFF/manifest/WORKER_ACCEPTANCE，从不读 ACCEPTANCE。");
            normalized = true;
        }
        if (requiresWriteReviewPacket)
        {
            diagnostics.Add("HANDOFF 要求 Reasonix 写 REVIEW_PACKET；已归一化：REVIEW_PACKET 由 Helper 自动生成，Reasonix 只写 EXECUTION_REPORT。");
            normalized = true;
        }
        if (requiresVisualDelivery)
        {
            return new(true,
                "合同要求 Reasonix 交付截图/视觉验收证据，但视觉验收归 GPT 且 Reasonix 禁止截图，无法安全修正。请修改 HANDOFF.md 后重试。",
                diagnostics, normalized, Deduplicate(workerChecks));
        }

        // 2) workerChecks 重复：去重（保留首个），并给出诊断。
        var deduplicated = Deduplicate(workerChecks);
        if (workerChecks is { Count: > 0 } && deduplicated.Count != workerChecks.Count)
        {
            diagnostics.Add($"workerChecks 存在 {workerChecks.Count - deduplicated.Count} 项重复，已去重（保留首个）。");
            normalized = true;
        }

        // 3) workerChecks 混入视觉/GUI/发布打包：职责过滤，移交给 GPT。
        var (_, delegated) = ReasonixAcceptanceFilter.Partition(deduplicated);
        if (delegated.Count > 0)
        {
            diagnostics.Add($"workerChecks 中 {delegated.Count} 项属于视觉/GUI 或 release 打包/发布职责，已移交 GPT，Reasonix 不执行。");
            normalized = true;
        }

        // 4) 普通 DeepSeek 任务错误使用 delivery profile：托管运行始终强制 balanced。
        var profile = manifest?.Profile;
        if (!string.IsNullOrWhiteSpace(profile)
            && (string.Equals(profile, "delivery", StringComparison.OrdinalIgnoreCase)
                || string.Equals(profile, "economy", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add($"manifest 声明 profile={profile}；托管 Reasonix 运行始终强制 balanced，声明仅作为输入读取。");
            normalized = true;
        }

        // 5) 普通 small/medium DeepSeek 任务显式 high/max：合同预检规范——派生运行计划 effort 降为 low
        //    （不修改用户合同原文件）；strict/major/security/release/migration 任务保留 high。
        var complexity = manifest?.Complexity;
        var effort = manifest?.Effort;
        var intensity = manifest?.Intensity;
        if (deepSeek
            && complexity is not null
            && (string.Equals(complexity, "small", StringComparison.OrdinalIgnoreCase) || string.Equals(complexity, "medium", StringComparison.OrdinalIgnoreCase))
            && effort is not null
            && (string.Equals(effort, "high", StringComparison.OrdinalIgnoreCase) || string.Equals(effort, "max", StringComparison.OrdinalIgnoreCase))
            && !string.Equals(intensity, "strict", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add($"普通 {complexity} DeepSeek 任务显式声明 effort={effort}；已按合同预检规范把派生运行计划 effort 降为 low（不修改用户合同原文件）。strict/major/security/release/migration 任务保留 high。");
            normalized = true;
        }

        // 6) HANDOFF 缺少允许读取/允许修改/直接依赖范围。
        var hasAllowedRead = HandoffHasAny(handoffLines, ["允许读取", "allowed-read", "allowed read", "允许读"]);
        var hasAllowedWrite = HandoffHasAny(handoffLines, ["允许修改", "allowed-write", "allowed write", "允许写"]);
        var hasDependencies = HandoffHasAny(handoffLines, ["直接依赖", "direct dependenc"]);
        if (!hasAllowedRead || !hasAllowedWrite || !hasDependencies)
        {
            var missing = new List<string>();
            if (!hasAllowedRead) missing.Add("允许读取范围");
            if (!hasAllowedWrite) missing.Add("允许修改范围");
            if (!hasDependencies) missing.Add("直接依赖范围");
            diagnostics.Add("HANDOFF 缺少" + string.Join("、", missing) + "；已提示 GPT 补全合同，不影响本任务执行。");
        }

        return new(false, null, diagnostics, normalized, deduplicated);
    }

    /// <summary>去重：按去空白后的文本保留首个出现，忽略空项。</summary>
    public static IReadOnlyList<string> Deduplicate(IReadOnlyList<string>? checks)
    {
        if (checks is null || checks.Count == 0) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var check in checks)
        {
            var trimmed = check?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (seen.Add(trimmed)) result.Add(trimmed);
        }
        return result;
    }

    private static bool HandoffHasAny(IReadOnlyList<string> lines, string[] markers)
        => lines.Any(line => markers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    private static bool HasNegation(string text)
        => NegationMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
