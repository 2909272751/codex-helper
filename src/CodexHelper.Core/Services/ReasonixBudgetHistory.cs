using System.Text;
using System.Text.Json;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// P1-5 历史预算校准：根据同一项目最近成功的相同复杂度任务的实际 steps 推导软预算。
/// 纯函数 <see cref="Calibrate"/> 负责截尾与上下限；<see cref="Record"/>/<see cref="LoadSamples"/>
/// 负责小型持久化统计（原子 JSON 写入，UTF-8 无 BOM）。
/// 样本不足回退现有默认值；异常值截尾；结果有合理上下限；不使用 token 数或运行时长作为硬上限。
/// 规则与托管 runner（run-reasonix-job.ps1 内嵌副本）保持完全一致。
/// </summary>
public static class ReasonixBudgetHistory
{
    /// <summary>持久化文件名（AppPaths.BaseDirectory 下）。</summary>
    public const string FileName = "reasonix-budget-history.json";

    /// <summary>每个 (项目, 复杂度) 键保留的最近成功样本数上限。</summary>
    public const int MaxSamplesPerKey = 20;

    /// <summary>参与校准的最小样本数；不足时回退默认预算。</summary>
    public const int MinSamplesForCalibration = 3;

    /// <summary>校准结果的合理下限（默认预算的一半，至少 8 步）。</summary>
    public const int CalibrationLowerBudget = 8;

    /// <summary>校准结果的合理上限（默认预算的两倍，至多 200 步）。</summary>
    public const int CalibrationUpperBudget = 200;

    /// <summary>
    /// 纯函数：对最近成功样本做截尾平均后推导软预算。
    /// 规则：样本数不足 <see cref="MinSamplesForCalibration"/> 时回退默认值；排序后去掉最小与最大各
    /// 一个异常值，取剩余样本平均；结果钳制到 [max(8, default/2), min(200, default*2)]。
    /// </summary>
    public static int Calibrate(int defaultBudget, IReadOnlyList<int>? samples)
    {
        var valid = samples?.Where(step => step >= 0).ToList() ?? [];
        if (valid.Count < MinSamplesForCalibration) return defaultBudget;
        var sorted = valid.OrderBy(step => step).ToList();
        var trimmedCount = Math.Max(0, sorted.Count - 2);
        var trimmed = sorted.Skip(1).Take(trimmedCount).ToList();
        if (trimmed.Count == 0) return defaultBudget;
        var average = (int)Math.Round(trimmed.Average());
        var lower = Math.Max(CalibrationLowerBudget, defaultBudget / 2);
        var upper = Math.Min(CalibrationUpperBudget, defaultBudget * 2);
        return Math.Clamp(average, lower, upper);
    }

    /// <summary>项目路径 → 稳定 slug（与 runner 的 Get-ProjectSessionRoot 规则一致）。</summary>
    public static string ProjectSlug(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return string.Empty;
        var normalized = projectRoot.TrimEnd('\\', '/').ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            builder.Append(ch is ':' or '\\' or '/' ? '-' : ch);
        return builder.ToString();
    }

    /// <summary>历史键：&lt;项目slug&gt;|&lt;complexity&gt;。</summary>
    public static string Key(string projectSlug, string complexity)
        => projectSlug + "|" + (complexity ?? string.Empty).ToLowerInvariant();

    public static string HistoryPathFor(AppPaths paths)
        => Path.Combine(paths.BaseDirectory, FileName);

    /// <summary>读取某 (项目, 复杂度) 的最近成功样本（非负整数，按记录顺序）。</summary>
    public static IReadOnlyList<int> LoadSamples(string historyPath, string projectSlug, string complexity)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(historyPath) || !File.Exists(historyPath)) return [];
            using var document = JsonDocument.Parse(
                File.ReadAllText(historyPath, Encoding.UTF8),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!document.RootElement.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Object) return [];
            var key = Key(projectSlug, complexity);
            if (!samples.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) return [];
            var result = new List<int>();
            foreach (var item in list.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var step) && step >= 0)
                    result.Add(step);
            return result;
        }
        catch { return []; }
    }

    /// <summary>原子追加一条最近成功样本（保留最近 <see cref="MaxSamplesPerKey"/> 条，忽略非法值）。</summary>
    public static void Record(string historyPath, string projectSlug, string complexity, int steps)
    {
        if (string.IsNullOrWhiteSpace(historyPath) || steps < 0) return;
        try
        {
            var key = Key(projectSlug, complexity);
            var root = new Dictionary<string, object>();
            if (File.Exists(historyPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(historyPath, Encoding.UTF8));
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("samples", out var samples)
                        && samples.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in samples.EnumerateObject())
                        {
                            var values = new List<int>();
                            foreach (var item in property.Value.EnumerateArray())
                                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var step) && step >= 0)
                                    values.Add(step);
                            root[property.Name] = values;
                        }
                    }
                }
                catch { /* 损坏历史文件：安全重建，绝不中断任务 */ }
            }
            if (!root.TryGetValue(key, out var existing) || existing is not List<int> list) list = new List<int>();
            list.Add(steps);
            if (list.Count > MaxSamplesPerKey) list = list.Skip(list.Count - MaxSamplesPerKey).ToList();
            root[key] = list;
            var payload = new Dictionary<string, object> { ["samples"] = root };
            AtomicFile.WriteAllText(historyPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 统计写入失败不影响任务本身 */ }
    }
}
