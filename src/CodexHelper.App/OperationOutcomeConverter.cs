using System;
using System.Globalization;
using System.Windows.Data;
using CodexHelper.Core.Models;

namespace CodexHelper.App;

/// <summary>
/// OperationOutcome 枚举 → 中文状态文案（已完成/部分完成/失败/已取消）。
/// 只做展示层翻译，不修改持久化枚举本身。
/// </summary>
public sealed class OperationOutcomeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is OperationOutcome outcome)
        {
            return outcome switch
            {
                OperationOutcome.Success => "已完成",
                OperationOutcome.PartialSuccess => "部分完成",
                OperationOutcome.Failed => "失败",
                OperationOutcome.Cancelled => "已取消",
                _ => outcome.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
