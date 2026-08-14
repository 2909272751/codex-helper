using System.Text.Json;

namespace CodexHelper.Core.Services;

/// <summary>Reasonix 执行强度：软预算与子代理策略，不是时间上限。</summary>
public enum ReasonixExecutionIntensity { Auto, Fast, Standard, Strict }

/// <summary>manifest.json 中可声明的执行策略字段（均可选；缺省时安全推断）。</summary>
public sealed record ReasonixManifestPolicy(
    string? Complexity,
    string? Profile,
    string? Effort,
    string? Intensity,
    int? MaxSteps,
    int? BudgetSteps,
    IReadOnlyList<string>? WorkerChecks,
    IReadOnlyList<string>? GptChecks,
    IReadOnlyList<string>? ReleaseChecks)
{
    public static ReasonixManifestPolicy FromManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return new(null, null, null, null, null, null, null, null, null);
        // 新字段优先；缺失时回退旧 execution* 别名，保证旧 manifest 继续兼容。
        string? ReadString(string name, params string[] legacy)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
            foreach (var alt in legacy)
                if (root.TryGetProperty(alt, out value) && value.ValueKind == JsonValueKind.String) return value.GetString();
            return null;
        }
        int? ReadInt(string name, params string[] legacy)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            foreach (var alt in legacy)
                if (root.TryGetProperty(alt, out value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number)) return number;
            return null;
        }
        IReadOnlyList<string>? ReadStrings(string name, params string[] legacy)
        {
            if (!root.TryGetProperty(name, out var value)) value = default;
            if (value.ValueKind != JsonValueKind.Array)
                foreach (var alt in legacy)
                {
                    if (!root.TryGetProperty(alt, out value)) continue;
                    if (value.ValueKind == JsonValueKind.Array) break;
                }
            if (value.ValueKind != JsonValueKind.Array) return null;
            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())) result.Add(item.GetString()!);
            return result.Count == 0 ? null : result;
        }
        return new(
            ReadString("complexity", "executionComplexity"), ReadString("profile", "executionProfile"), ReadString("effort", "executionEffort"),
            ReadString("intensity", "executionIntensity"), ReadInt("maxSteps", "executionMaxSteps"), ReadInt("budgetSteps", "executionBudgetSteps"),
            ReadStrings("workerChecks", "executionWorkerChecks"), ReadStrings("gptChecks", "executionGptChecks"), ReadStrings("releaseChecks", "executionReleaseChecks"));
    }
}

/// <summary>解析并推断后的执行计划：Reasonix 命令行参数与托管提示的输入。</summary>
public sealed record ReasonixExecutionPlan(
    ReasonixExecutionIntensity Intensity,
    string Complexity,
    string Profile,
    string Effort,
    int? MaxSteps,
    int BudgetSteps,
    bool AllowAutoReviewSubagents,
    string Source,
    IReadOnlyList<string> WorkerChecks)
{
    public string IntensityText => Intensity.ToString().ToLowerInvariant();
    public string DisplayText => $"{IntensityText} / {Profile} / {Effort}（complexity={Complexity}，预计 ≤{BudgetSteps} 步）";
}

/// <summary>
/// 执行强度解析与推断。manifest 显式声明优先；未声明时从合同范围（SPEC 规模与
/// ACCEPTANCE 验收项数量）安全推断。非法值一律回退到安全默认，绝不抛异常。
/// 规则与托管 runner（run-reasonix-job.ps1 内嵌副本）保持完全一致。
/// </summary>
public static class ReasonixExecutionPolicy
{
    public const string DefaultIntensity = "auto";
    private static readonly string[] ComplexityValues = ["small", "medium", "major"];
    private static readonly string[] EffortValues = ["low", "medium", "high", "max"];
    private static readonly string[] MajorHints = ["major", "product delivery", "release delivery"];
    private static readonly string[] SmallHints = ["minor", "small fix", "focused fix"];
    // 合同预检规范（B1）认定的明确高风险信号：命中则保留 high 的 effort（不降级）。
    private static readonly string[] HighRiskHints =
    [
        "credential", "crypto", "envelope", "vault", "secret", "security",
        "migration", "installer", "publish", "release",
        "凭据", "加密", "迁移", "发布", "安装", "安全"
    ];

    public static ReasonixExecutionPlan Resolve(ReasonixManifestPolicy? manifest, string specText, string acceptanceText, string? defaultIntensity, string? defaultModel = null)
    {
        var intensity = ParseIntensity(manifest?.Intensity) ?? ParseIntensity(defaultIntensity) ?? ReasonixExecutionIntensity.Auto;
        var complexity = Normalize(manifest?.Complexity, ComplexityValues) ?? InferComplexity(specText, acceptanceText);
        // 托管 Reasonix 永远使用 balanced：manifest 即使显式声明 economy/delivery 也仅作为输入被读取
        // （仍计入 source=manifest），最终命令、状态与展示恒为 balanced；Strict 仍为 balanced + high。
        var profile = InferProfile(intensity, complexity);
        var effort = Normalize(manifest?.Effort, EffortValues) ?? InferEffort(intensity, complexity, defaultModel);
        // 合同预检规范（B1）：普通 small/medium DeepSeek 显式 high/max，在非 strict、非 major 且 spec
        // 无明确高风险信号时，派生运行计划 effort 降为 low（只作用于派生合同，绝不改写用户合同原文件）；
        // strict/major/security/release/migration 保留 high。与托管 runner 的 run-reasonix-job.ps1 副本一致。
        if (IsDeepSeekModel(defaultModel)
            && (effort == "high" || effort == "max")
            && (complexity == "small" || complexity == "medium")
            && intensity != ReasonixExecutionIntensity.Strict
            && !HasHighRiskSignal(specText))
        {
            effort = "low";
        }
        var maxSteps = manifest?.MaxSteps is > 0 ? manifest.MaxSteps : null;
        var budgetSteps = manifest?.BudgetSteps is > 0 ? manifest.BudgetSteps!.Value : InferBudget(complexity);
        // 所有托管强度都禁止自动启动 review/security-review/explore 子代理；GPT 是唯一评审者。
        var allowAutoReviewSubagents = false;
        var source = HasAnyStrategyField(manifest) ? "manifest" : "inferred";
        var workerChecks = manifest?.WorkerChecks is { Count: > 0 } ? manifest.WorkerChecks : Array.Empty<string>();
        return new(intensity, complexity, profile, effort, maxSteps, budgetSteps, allowAutoReviewSubagents, source, workerChecks);
    }

    public static ReasonixExecutionIntensity? ParseIntensity(string? value)
        => value is null ? null : Enum.TryParse<ReasonixExecutionIntensity>(value, ignoreCase: true, out var parsed) ? parsed : null;

    /// <summary>统计 ACCEPTANCE 的验收项数量（Markdown 列表项与编号项）。</summary>
    public static int CountAcceptanceItems(string acceptanceText)
    {
        if (string.IsNullOrWhiteSpace(acceptanceText)) return 0;
        var count = 0;
        foreach (var line in acceptanceText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            if (trimmed[0] is '-' or '*' || (char.IsAsciiDigit(trimmed[0]) && trimmed.Length > 1 && trimmed[1] == '.')) count++;
        }
        return count;
    }

    private static string InferComplexity(string specText, string acceptanceText)
    {
        var spec = specText ?? string.Empty;
        if (spec.Length >= 9000 || ContainsAny(spec, MajorHints) || CountAcceptanceItems(acceptanceText) >= 12) return "major";
        if (spec.Length <= 2500 || ContainsAny(spec, SmallHints)) return "small";
        return "medium";
    }

    /// <summary>托管 Reasonix 永远使用 balanced profile：任何执行强度（Strict/Fast/Standard/Auto）都不再生成 delivery，
    /// 且不受 manifest 显式 profile 影响（economy/delivery 声明仅作为输入读取，最终仍归一化为 balanced）。
    /// Strict 只映射为 balanced + high（高 effort 由 InferEffort 负责）。</summary>
    private static string InferProfile(ReasonixExecutionIntensity intensity, string complexity)
        => "balanced";

    private static string InferEffort(ReasonixExecutionIntensity intensity, string complexity, string? defaultModel)
    {
        var deepSeek = IsDeepSeekModel(defaultModel);
        return intensity switch
        {
            ReasonixExecutionIntensity.Strict => "high",
            ReasonixExecutionIntensity.Fast => "low",
            ReasonixExecutionIntensity.Standard => deepSeek ? "low" : "medium",
            _ => complexity switch { "major" => "high", "small" => "low", _ => deepSeek ? "low" : "medium" }
        };
    }

    /// <summary>判定默认模型是否 OpenCode Go 的 DeepSeek thinking（只接受 low/high/max/disabled，不得生成 medium）。</summary>
    public static bool IsDeepSeekModel(string? defaultModel)
        => !string.IsNullOrWhiteSpace(defaultModel)
           && defaultModel.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    private static int InferBudget(string complexity) => complexity switch { "small" => 16, "major" => 56, _ => 35 };

    private static bool HasAnyStrategyField(ReasonixManifestPolicy? manifest)
        => manifest is not null && (manifest.Complexity is not null || manifest.Profile is not null || manifest.Effort is not null || manifest.Intensity is not null);

    private static string? Normalize(string? value, string[] allowed)
        => value is null ? null : allowed.FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string text, string[] hints)
        => hints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static bool HasHighRiskSignal(string specText)
    {
        var spec = (specText ?? string.Empty).ToLowerInvariant();
        return HighRiskHints.Any(hint => spec.Contains(hint, StringComparison.Ordinal));
    }
}

/// <summary>
/// workerChecks 职责过滤：把明显属于 GPT 视觉/GUI 验收或 release 打包/发布的工作识别出来，移交给 GPT，
/// 同时绝不吞掉普通 build/test/source inspection。规则与托管 runner（run-reasonix-job.ps1 内嵌副本）保持完全一致。
/// 关键词刻意避开裸 "release"/"发布"/"gui"（会误伤"Release configuration build"/"发布模式构建"/"GUI 项目构建"等普通构建）。
/// </summary>
public static class ReasonixAcceptanceFilter
{
    /// <summary>
    /// 否定约束标记：识别“不截图、不看图、不启动 GUI、不进行视觉判断、不发布”等约束说明。
    /// 这类文本不是待执行检查，也不应导致相邻的正常结构测试被移交 GPT。
    /// </summary>
    private static readonly string[] NegationMarkers =
    [
        "不", "没有", "无", "禁止", "避免", "无需", "不要", "不得", "切勿",
        "don't", "do not", "should not", "must not", "never"
    ];

    /// <summary>
    /// 明确要求才移交 GPT 的视觉/GUI 关键词。刻意避开 layout/布局/image/图片/gui/屏幕/视觉/颜色 等裸普通词，
    /// 它们常出现在 XAML/XML/DOM 布局数学、图片资源存在性、GUI 项目构建等普通 worker 检查中，不得误伤。
    /// 只保留“明确要求截图/看图/像素分析/真实 GUI 操作或烟测/DPI 视觉判断/颜色遮挡视觉判断/屏幕捕获”等明确语境。
    /// </summary>
    private static readonly string[] VisualGpuPatterns =
    [
        "screenshot", "截屏", "截图",
        "view image", "inspect image", "view screenshot", "inspect screenshot", "看图片", "看图", "查看图片", "查看图像",
        "pixel", "像素",
        "dpi",
        "visual acceptance", "视觉验收", "视觉验证", "视觉判断",
        "真实 gui", "gui 烟测", "gui smoke", "gui 交互", "gui 操作", "gui 验收", "gui 截图",
        "screen capture", "屏幕捕获", "捕获屏幕",
        "color", "颜色", "occlusion", "遮挡",
        "bitblt", "printwindow"
    ];

    private static readonly string[] ReleasePackagingPatterns =
    [
        "publish", "打包",
        "zip", ".zip",
        "安装包", "installer", "setup.exe",
        "build-release", "github release", "create release", "release 页面",
        "releases/download", "package release", "打包发布", "发布 release",
        "发布安装", "发布项目", "发布工作", "发布验收"
    ];

    /// <summary>该 workerCheck 是否属于 GPT 视觉/GUI 验收或 release 打包/发布工作（应移交 GPT，不交给 Reasonix）。
    /// 命中任一明确视觉/GUI/发布关键词后，若文本同时含否定约束标记，则视为约束说明而非待执行检查，返回 false。</summary>
    public static bool ShouldDelegateToGpt(string check)
    {
        if (string.IsNullOrWhiteSpace(check)) return false;
        var lower = check.ToLowerInvariant();
        var hitsExplicitPattern = VisualGpuPatterns.Any(pattern => lower.Contains(pattern, StringComparison.Ordinal))
            || ReleasePackagingPatterns.Any(pattern => lower.Contains(pattern, StringComparison.Ordinal));
        if (!hitsExplicitPattern) return false;
        // 否定约束优先：命中明确模式但同时含否定标记，视为“不截图/不看图/不发布”等约束说明，不应移交。
        return !NegationMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>过滤 workerChecks：返回 (合法 worker 项, 应移交 GPT 的项)。普通 build/test/source inspection 保留。</summary>
    public static (IReadOnlyList<string> Worker, IReadOnlyList<string> DelegatedToGpt) Partition(IEnumerable<string> checks)
    {
        var worker = new List<string>();
        var delegated = new List<string>();
        if (checks is null) return (worker, delegated);
        foreach (var check in checks)
        {
            if (string.IsNullOrWhiteSpace(check)) continue;
            if (ShouldDelegateToGpt(check)) delegated.Add(check.Trim());
            else worker.Add(check.Trim());
        }
        return (worker, delegated);
    }
}
