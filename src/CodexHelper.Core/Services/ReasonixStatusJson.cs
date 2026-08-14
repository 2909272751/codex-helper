using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// Reasonix 任务状态的统一 JSON 序列化入口：标准 <see cref="System.Text.Json"/> 序列化、
/// UTF-8 无 BOM 原子写入、宽容读取旧损坏状态。所有 ReasonixTaskStatus 的持久化读写
/// 必须经过这里，禁止手工拼接 JSON 字符串写状态；Windows 路径反斜杠由标准序列化器
/// 正确转义，中文路径保持不变。写出的文件必须能被 System.Text.Json 与 PowerShell
/// ConvertFrom-Json 正常读取。
/// </summary>
public static class ReasonixStatusJson
{
    /// <summary>
    /// 统一选项：属性名大小写不敏感（兼容 PowerShell ConvertTo-Json 的 PascalCase 键）、
    /// 缩进输出、Reasonix 日期格式转换器（兼容 /Date(ms)/、ISO 8601 与 Unix 毫秒）。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new ReasonixDateTimeConverter() }
    };

    public static string Serialize(ReasonixTaskStatus status) => JsonSerializer.Serialize(status, Options);

    /// <summary>UTF-8 无 BOM 原子写入任务状态文件（临时文件 + 替换/移动，绝不半写）。</summary>
    public static void WriteStatus(string path, ReasonixTaskStatus status)
        => AtomicFile.WriteAllText(path, Serialize(status));

    /// <summary>宽容读取：文件缺失、损坏或字段非法一律返回 null，绝不抛异常（调用方决定回退）。
    /// 先按标准 JSON 解析；失败后兼容历史状态文件中未转义的 Windows 反斜杠路径及中文路径
    /// （仅在内存中修复未转义反斜杠，绝不修改原文件，也不吞掉合法转义）。</summary>
    public static ReasonixTaskStatus? TryReadStatus(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : Deserialize(text);
        }
        catch { return null; }
    }

    /// <summary>
    /// P0-3 终态归一化（内存级，不写回；写入侧由 runner/Helper 保证源头一致）：
    /// 完成、失败、取消、中断与等待 GPT 验收等非运行状态，剩余百分比一律归零（运行中才允许 5%~100%）；
    /// 阶段归一：完成必为 done，其余终态保留失败发生阶段（绝不残留 done 之外的进行中百分比语义）。
    /// 只做展示与审计一致性，不改变 FailureKind 等业务字段。任何状态文件写入都必须走
    /// <see cref="WriteStatus"/>（标准 JSON 原子写）。调用方保证传入非 null 状态。
    /// </summary>
    public static ReasonixTaskStatus NormalizeTerminalState(ReasonixTaskStatus status)
    {
        if (status.IsRunning) return status;
        var remaining = status.RemainingPercent is > 0 ? 0 : status.RemainingPercent;
        var stage = status.ProgressStage;
        if (string.Equals(status.State, "completed", StringComparison.OrdinalIgnoreCase))
        {
            stage = "done";
        }
        return status with { RemainingPercent = remaining, ProgressStage = stage };
    }

    // 历史状态文件可能含未转义的 Windows 反斜杠路径（如 "C:\项目\文件"）或中文路径，标准 JSON 解析会失败。
    // 兼容修复：仅把"单个反斜杠后跟非 JSON 合法转义字符"的 \ 补成 \\（已转义的 \\ \n \t \uXXXX 等保持原样，
    // 即不得吞掉合法转义）。此修复只作用于内存副本，不写回文件。
    private static readonly Regex UnescapedBackslash =
        new(@"(?<!\\)\\(?![""\\/bfnrtu])", RegexOptions.Compiled);

    private static ReasonixTaskStatus? Deserialize(string text)
    {
        try { return JsonSerializer.Deserialize<ReasonixTaskStatus>(text, Options); }
        catch (JsonException) { }
        try { return JsonSerializer.Deserialize<ReasonixTaskStatus>(UnescapedBackslash.Replace(text, @"\\"), Options); }
        catch { return null; }
    }
}
