using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// 任务快照内容指纹：覆盖所有会影响列表、统计、诊断与选中步骤展示的字段。
/// 以稳定顺序（任务按 TaskId、诊断按文件名）序列化后取 SHA-256，保证相同内容产出相同指纹。
/// 可选叠加选中任务的 workerChecks，使 manifest 变化也能触发列表重建。
/// </summary>
public static class ReasonixTaskSnapshotFingerprint
{
    public static string Compute(ReasonixTasksSnapshot snapshot, IReadOnlyList<string>? workerChecks = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var canonical = snapshot with
        {
            Tasks = snapshot.Tasks?.OrderBy(task => task.TaskId, StringComparer.Ordinal).ToList() ?? [],
            Diagnostics = snapshot.Diagnostics?.OrderBy(diagnostic => diagnostic.FileName, StringComparer.Ordinal).ToList() ?? []
        };
        var text = JsonSerializer.Serialize(canonical);
        if (workerChecks is not null) text += "\nW:" + string.Join('|', workerChecks);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

/// <summary>
/// Reasonix 任务自动刷新协调器（纯逻辑，无 UI 依赖，可单测）：
/// - 单飞：刷新进行中时自动 Tick 直接跳过；绝不并发读取。
/// - 手动合并：手动刷新进行中时再次手动刷新会排队，当前读取完成后自动再执行一次。
/// - 页面可见：仅在协作开发页可见时允许自动刷新；切离页面后停止昂贵刷新。
/// - 关闭：窗口关闭后拒绝任何新读取并清空排队。
/// - 变化门控：无变化快照不触发 UI 重建（由调用方在渲染后调用 <see cref="RecordRendered"/>）。
/// 所有方法仅由 UI 线程调用，无需加锁。
/// </summary>
public sealed class ReasonixTaskRefreshCoordinator
{
    private string? _renderedFingerprint;
    private bool _readInFlight;
    private bool _manualQueued;

    /// <summary>协作开发页面当前是否可见（由 UI 导航状态维护，非易变视觉属性）。</summary>
    public bool PageVisible { get; set; }

    /// <summary>窗口是否已关闭；关闭后拒绝任何新读取。</summary>
    public bool Closed { get; set; }

    /// <summary>自动 Tick：仅在协作页可见、未关闭、无进行中读取且无排队手动刷新时允许发起一次读取。</summary>
    public bool TryStartAutoRead()
    {
        if (Closed || !PageVisible || _readInFlight || _manualQueued) return false;
        _readInFlight = true;
        return true;
    }

    /// <summary>手动刷新：立即生效；若已有读取进行中则排队，绝不并发读取。返回 true 表示本次调用可发起读取。</summary>
    public bool TryStartManualRead()
    {
        if (Closed) return false;
        if (_readInFlight)
        {
            _manualQueued = true;
            return false;
        }
        _readInFlight = true;
        return true;
    }

    /// <summary>一次读取完成。返回 true 表示存在排队的手动刷新，调用方应再发起一次读取（按手动语义）。</summary>
    public bool CompleteRead()
    {
        _readInFlight = false;
        if (_manualQueued && !Closed)
        {
            _manualQueued = false;
            _readInFlight = true;
            return true;
        }
        return false;
    }

    /// <summary>内容是否相对上次已渲染发生变化；变化或首次返回 true。不改变内部状态。</summary>
    public bool HasContentChanged(ReasonixTasksSnapshot snapshot, IReadOnlyList<string>? workerChecks = null)
        => !string.Equals(_renderedFingerprint, ReasonixTaskSnapshotFingerprint.Compute(snapshot, workerChecks), StringComparison.Ordinal);

    /// <summary>在 UI 实际渲染（重建控件）后调用，记录当前内容指纹。</summary>
    public void RecordRendered(ReasonixTasksSnapshot snapshot, IReadOnlyList<string>? workerChecks = null)
        => _renderedFingerprint = ReasonixTaskSnapshotFingerprint.Compute(snapshot, workerChecks);

    /// <summary>关闭窗口：停止后续读取并清空排队。</summary>
    public void Close()
    {
        Closed = true;
        _manualQueued = false;
    }
}
