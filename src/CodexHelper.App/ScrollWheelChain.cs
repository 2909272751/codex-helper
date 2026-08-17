using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.App;

/// <summary>
/// 全局滚轮链 WPF 事件层：挂在唯一的主 ScrollViewer（MainWindow.MainScrollViewer）上。
/// 事件源位于内层可滚动控件（DataGrid/ListBox/ListView/含内部 ScrollViewer 的只读多行 TextBox）时，
/// 用 ScrollChainLogic 判定：内层仍可按该方向滚动 → 不干预，由内层原生消费；
/// 内层已到顶/底、无溢出或不可滚动 → 截获该次滚轮并转发给主滚动容器。
/// 只在 PreviewMouseWheel（隧道）阶段处理一次并标记 Handled，不递归、不双倍滚动、不反转方向；
/// 鼠标拖动滚动条、键盘导航与选择行为保持原生。
/// </summary>
public static class ScrollWheelChain
{
    /// <summary>WPF 每个滚轮 notch 的增量。</summary>
    private const double WheelNotchDelta = 120.0;

    /// <summary>逻辑像素行高（与 ScrollViewer 默认行高一致）。</summary>
    private const double LineHeight = 16.0;

    /// <summary>挂载到主 ScrollViewer；重复挂载同一实例会先移除旧处理（幂等）。</summary>
    public static void Attach(ScrollViewer main)
    {
        if (main is null) return;
        main.PreviewMouseWheel -= OnPreviewMouseWheel;
        main.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        var main = sender as ScrollViewer;
        if (main is null) return;

        var source = e.OriginalSource as DependencyObject;
        if (source is null) return;

        var inner = FindAncestorScrollViewer(source);
        // 没有内层滚动容器（鼠标在页面空白或就在主容器上）：保持原生，由主 ScrollViewer 消费。
        if (inner is null || ReferenceEquals(inner, main)) return;

        // 内层仍可按该方向滚动：不标记 Handled，事件继续隧道到内层由内层原生消费。
        if (!ScrollChainLogic.ShouldPassWheel(inner.ExtentHeight, inner.ViewportHeight, inner.VerticalOffset, e.Delta)) return;

        // 内层已到边界/无溢出：截获该次滚轮并转发给主 ScrollViewer（ScrollToVerticalOffset 自动钳制，主容器到边界也安全）。
        e.Handled = true;
        var pixels = e.Delta / WheelNotchDelta * SystemParameters.WheelScrollLines * LineHeight;
        main.ScrollToVerticalOffset(main.VerticalOffset - pixels);
    }

    /// <summary>沿视觉树（必要时逻辑树）向上寻找最近的 ScrollViewer；找不到返回 null（不崩溃）。</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        for (var current = start; current is not null;)
        {
            if (current is ScrollViewer viewer) return viewer;
            current = VisualTreeHelper.GetParent(current) as DependencyObject ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
