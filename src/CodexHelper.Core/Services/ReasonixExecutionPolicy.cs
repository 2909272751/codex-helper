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
    private static readonly string[] ProfileValues = ["economy", "balanced", "delivery"];
    private static readonly string[] EffortValues = ["low", "medium", "high", "max"];
    private static readonly string[] MajorHints = ["major", "product delivery", "release delivery"];
    private static readonly string[] SmallHints = ["minor", "small fix", "focused fix"];

    public static ReasonixExecutionPlan Resolve(ReasonixManifestPolicy? manifest, string specText, string acceptanceText, string? defaultIntensity, string? defaultModel = null)
    {
        var intensity = ParseIntensity(manifest?.Intensity) ?? ParseIntensity(defaultIntensity) ?? ReasonixExecutionIntensity.Auto;
        var complexity = Normalize(manifest?.Complexity, ComplexityValues) ?? InferComplexity(specText, acceptanceText);
        var profile = Normalize(manifest?.Profile, ProfileValues) ?? InferProfile(intensity, complexity);
        var effort = Normalize(manifest?.Effort, EffortValues) ?? InferEffort(intensity, complexity, defaultModel);
        var maxSteps = manifest?.MaxSteps is > 0 ? manifest.MaxSteps : null;
        var budgetSteps = manifest?.BudgetSteps is > 0 ? manifest.BudgetSteps!.Value : InferBudget(complexity);
        var allowAutoReviewSubagents = intensity == ReasonixExecutionIntensity.Strict
            || (intensity == ReasonixExecutionIntensity.Auto && complexity == "major");
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

    private static string InferProfile(ReasonixExecutionIntensity intensity, string complexity)
        => intensity switch
        {
            ReasonixExecutionIntensity.Strict => "delivery",
            ReasonixExecutionIntensity.Fast or ReasonixExecutionIntensity.Standard => "balanced",
            _ => complexity == "major" ? "delivery" : "balanced"
        };

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

    private static int InferBudget(string complexity) => complexity switch { "small" => 25, "major" => 200, _ => 80 };

    private static bool HasAnyStrategyField(ReasonixManifestPolicy? manifest)
        => manifest is not null && (manifest.Complexity is not null || manifest.Profile is not null || manifest.Effort is not null || manifest.Intensity is not null);

    private static string? Normalize(string? value, string[] allowed)
        => value is null ? null : allowed.FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string text, string[] hints)
        => hints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
