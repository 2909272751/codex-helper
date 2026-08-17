namespace CodexHelper.Core.Infrastructure;

/// <summary>
/// 滚轮链纯逻辑：决定某次鼠标滚轮是否应交给外层主滚动容器消费。
/// 与 WPF 事件层解耦，纯函数便于单元测试。
/// 约定：extent 为内容总高度，viewport 为可视区高度，offset 为当前滚动偏移（0..extent-viewport）；
/// delta 与 WPF MouseWheelEventArgs.Delta 一致：正值表示向上滚动（内容向下移动），负值表示向下滚动。
/// </summary>
public static class ScrollChainLogic
{
    /// <summary>
    /// 判断内层滚动区域是否应把该次滚轮传递给外层。
    /// 规则：
    ///   1. 没有溢出（extent &lt;= viewport，含滚动条禁用/空内容）或可视区非法（viewport &lt;= 0）→ 向外传递；
    ///   2. 向上滚动（delta &gt; 0）且已到顶（offset &lt;= 0）→ 向外传递；
    ///   3. 向下滚动（delta &lt; 0）且已到底（offset &gt;= extent - viewport）→ 向外传递；
    ///   4. 其余（内层仍可按该方向滚动）→ 由内层消费，不传递。
    /// </summary>
    public static bool ShouldPassWheel(double extent, double viewport, double offset, double delta)
    {
        if (viewport <= 0 || extent <= viewport) return true;
        if (delta > 0) return offset <= 0;
        if (delta < 0) return offset >= extent - viewport;
        // delta == 0：无滚动意图，不传递。
        return false;
    }
}
