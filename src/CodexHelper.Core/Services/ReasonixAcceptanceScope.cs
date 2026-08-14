namespace CodexHelper.Core.Services;

/// <summary>GPT 增量验收范围建议：按实际改动文件与合同风险映射出的验收组合。</summary>
public sealed record ReasonixAcceptanceSuggestion(
    IReadOnlyList<string> Scopes,
    string Label,
    string Rationale);

/// <summary>
/// 影响范围增量验收映射（纯函数）：根据实际 changed files 返回 GPT 验收建议
/// focused/full/release/security/visual。推荐规则：
/// 文案/文档、单个策略/纯函数 → focused；
/// UI/XAML → focused + visual；
/// 凭据、加密、备份、迁移 → security + full；
/// installer、版本、发布脚本 → release + full；
/// 多核心模块或无法识别 → full。
/// 这只是建议；合同显式要求与高风险规则优先，不能跳过必须验收。
/// 规则与托管 runner（run-reasonix-job.ps1 内嵌副本）保持一致。
/// </summary>
public static class ReasonixAcceptanceScope
{
    private static readonly string[] ReleasePatterns =
    [
        "directory.build.props", "installer", "build-release", "publish",
        ".iss", ".wxs", ".github\\workflows", ".github/workflows"
    ];

    private static readonly string[] SecurityPatterns =
    [
        "credential", "crypto", "envelope", "vault", "secret",
        "backup", "migration", "encrypt", "decrypt", "portable", "bundle",
        "officialaccounts", "apiprovider", "api-provider"
    ];

    /// <summary>推荐验收范围。文件列表为空或无法识别时保守建议 full。</summary>
    public static ReasonixAcceptanceSuggestion Recommend(IReadOnlyList<string>? changedFiles)
    {
        var files = (changedFiles ?? []).Where(file => !string.IsNullOrWhiteSpace(file)).ToList();
        if (files.Count == 0)
            return new(["full"], "full", "无法识别改动文件（空列表或 git 不可用），按完整回归验收。");

        var scopes = new List<string>();
        var reasons = new List<string>();
        var releaseHit = false;
        var securityHit = false;
        var visualHit = false;
        var sourceCount = 0;
        var docCount = 0;
        var testCount = 0;

        foreach (var file in files)
        {
            var normalized = file.Replace('/', '\\');
            var lower = normalized.ToLowerInvariant();
            if (ReleasePatterns.Any(pattern => lower.Contains(pattern, StringComparison.Ordinal)))
            {
                releaseHit = true;
                reasons.Add(file);
            }
            else if (SecurityPatterns.Any(pattern => lower.Contains(pattern, StringComparison.Ordinal)))
            {
                securityHit = true;
                reasons.Add(file);
            }

            if (lower.EndsWith(".xaml", StringComparison.Ordinal)) visualHit = true;
            if (lower.EndsWith(".cs", StringComparison.Ordinal) && lower.Contains("src", StringComparison.Ordinal)) sourceCount++;
            if (lower.EndsWith(".md", StringComparison.Ordinal) || lower.EndsWith(".txt", StringComparison.Ordinal)
                || lower.Contains("readme", StringComparison.Ordinal) || lower.Contains("\\docs\\", StringComparison.Ordinal)) docCount++;
            if (lower.Contains("\\tests\\", StringComparison.Ordinal) || lower.StartsWith("tests\\", StringComparison.Ordinal)
                || lower.Contains("test", StringComparison.Ordinal)) testCount++;
        }

        // 高风险规则优先：release/security 触发 full 且建议对应专项。
        if (releaseHit)
        {
            scopes.Add("release");
            scopes.Add("full");
            return new(scopes, "release + full", "改动涉及 installer/版本/发布脚本：" + string.Join("、", reasons.Take(5)));
        }
        if (securityHit)
        {
            scopes.Add("security");
            scopes.Add("full");
            return new(scopes, "security + full", "改动涉及凭据/加密/备份/迁移：" + string.Join("、", reasons.Take(5)));
        }

        // UI/XAML：focused + visual。
        if (visualHit)
        {
            scopes.Add("focused");
            scopes.Add("visual");
            var extra = docCount == files.Count ? "（纯文档）" : string.Empty;
            return new(scopes, "focused + visual" + extra, "改动涉及 UI/XAML：" + string.Join("、", files.Take(5)));
        }

        // 文案/文档、纯测试 → focused。
        if (docCount == files.Count)
            return new(["focused"], "focused", "纯文案/文档改动：" + string.Join("、", files.Take(5)));
        if (testCount == files.Count)
            return new(["focused"], "focused", "纯测试改动：" + string.Join("、", files.Take(5)));
        if (files.Count == 1)
            return new(["focused"], "focused", "单个文件改动：" + files[0]);

        // 多核心模块或文件数较多 → full。
        if (sourceCount >= 3 || files.Count >= 3)
            return new(["full"], "full", $"涉及 {sourceCount} 个核心源码文件（共 {files.Count} 个改动文件），按完整回归验收。");
        // 少量普通源码/文件 → focused。
        return new(["focused"], "focused", "少量普通源码/文件改动：" + string.Join("、", files.Take(5)));
    }
}
