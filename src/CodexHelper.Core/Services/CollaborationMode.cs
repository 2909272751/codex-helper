namespace CodexHelper.Core.Services;

/// <summary>协作开发执行器选择。Off=关闭协作；Reasonix=现有执行器；Harness=DeepSeek Harness（开发者预览）。</summary>
public enum CollaborationMode
{
    /// <summary>关闭协作，移除 Helper 管理的协作规则，GPT 独立开发。</summary>
    Off,
    /// <summary>Reasonix 执行器（现有成熟路径）。</summary>
    Reasonix,
    /// <summary>DeepSeek Harness 执行器（官方开发者预览，GPT 规划/验收、Harness 实现）。</summary>
    Harness
}

/// <summary>执行器选择帮助方法：字符串解析、持久化表示与显示文本。</summary>
public static class CollaborationModeExtensions
{
    /// <summary>解析持久化字符串；空/非法值安全回退 Off（避免误开启任意执行器）。</summary>
    public static CollaborationMode ParseCollaborationMode(string? value)
        => Enum.TryParse<CollaborationMode>(value, ignoreCase: true, out var mode) ? mode : CollaborationMode.Off;

    /// <summary>规范化持久化字符串，保证 settings.json 始终写入三态之一。</summary>
    public static string ToPersisted(this CollaborationMode mode) => mode.ToString();

    public static string DisplayName(this CollaborationMode mode) => mode switch
    {
        CollaborationMode.Reasonix => "Reasonix",
        CollaborationMode.Harness => "DeepSeek Harness",
        _ => "关闭协作"
    };
}
