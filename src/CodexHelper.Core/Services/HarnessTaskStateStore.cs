using System.Text;
using System.Text.Json;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// Harness 任务状态的项目内真相源协议（第一阶段收敛）：
/// <list type="bullet">
/// <item>任务真实状态以任务目录 <c>HARNESS_STATUS.json</c> 为准（<c>&lt;project&gt;/.codex-helper/runs/&lt;run-id&gt;/</c>）；</item>
/// <item><c>%LocalAppData%/CodexHelper/harness-tasks/</c> 仅作为轻量索引/兼容入口（旧版本 Helper 仍可读），不再是状态真相源；</item>
/// <item>写入一律双写（真相源 + 索引），读取一律真相源优先、旧索引宽容回退，并把旧注册表状态安全迁移（提升）为真相源，历史任务不消失；</item>
/// <item>进度协议与 Reasonix 兼容：任务目录内 <c>PROGRESS.json</c>（stage/summary/updatedUtc/completedChecks/totalChecks/currentCheck/checks），
/// 仅由真实事件/工具/可信终态驱动投影，绝不把推理 token 数当作完成进度；</item>
/// <item>项目级单飞/停止/对账统一引用项目内 <c>active-harness-task</c> 原子记录（<c>&lt;project&gt;/.codex-helper/active-harness-task.json</c>），
/// 停止期间持久化取消意图，停止终态核验后才允许新合同提交。</item>
/// </list>
/// 所有写入均为 UTF-8 无 BOM 原子写入（<see cref="AtomicFile"/>）。
/// </summary>
public static class HarnessTaskStateStore
{
    /// <summary>任务目录内的状态真相源文件名。</summary>
    public const string StatusFileName = "HARNESS_STATUS.json";
    /// <summary>任务目录内的进度协议文件名（与 Reasonix 兼容）。</summary>
    public const string ProgressFileName = "PROGRESS.json";
    /// <summary>项目内活动任务原子记录文件名（位于 &lt;project&gt;/.codex-helper/）。</summary>
    public const string ActiveTaskRecordFileName = "active-harness-task.json";

    private static readonly JsonSerializerOptions StatusOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string StatusPath(string taskDirectory) => Path.Combine(taskDirectory, StatusFileName);
    public static string ProgressPath(string taskDirectory) => Path.Combine(taskDirectory, ProgressFileName);
    public static string ActiveRecordPath(string projectRoot) => Path.Combine(projectRoot, ".codex-helper", ActiveTaskRecordFileName);

    /// <summary>
    /// 双写任务状态：任务目录真相源（主，失败必须上抛，绝不静默丢状态）+ 注册表兼容索引（轻量，尽力而为）。
    /// 真相源文件名与索引内容同构，保证旧版本 Helper/Rescue 仍可读取、新版本以真相源为准。
    /// </summary>
    public static void WriteStatus(string registryDirectory, HarnessTaskStatus status)
    {
        var directory = status.TaskDirectory;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            AtomicFile.WriteAllText(StatusPath(directory), JsonSerializer.Serialize(status, StatusOptions));
        }
        if (!string.IsNullOrWhiteSpace(status.TaskId))
        {
            try
            {
                AtomicFile.WriteAllText(Path.Combine(EnsureDirectory(registryDirectory), RegistryFileName(status.TaskId)),
                    JsonSerializer.Serialize(status, StatusOptions));
            }
            catch
            {
                // 兼容索引写入失败不阻断真相源；下次写入会重试补齐。
            }
        }
    }

    /// <summary>
    /// 宽容读取任务状态：先经注册表索引定位任务目录（只读已登记任务目录，不做全盘递归扫描），
    /// 再以任务目录真相源优先；真相源缺失/损坏时回退旧注册表索引并把旧状态迁移（提升）为真相源，
    /// 旧状态文件不消失、不误标终态。返回 null 表示该任务未登记。
    /// </summary>
    public static HarnessTaskStatus? TryReadStatus(string registryDirectory, string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || !IsSafeTaskId(taskId)) return null;
        var registry = TryReadFile<HarnessTaskStatus>(Path.Combine(registryDirectory, RegistryFileName(taskId)));
        if (registry is null) return null;
        return ResolveStatus(registry);
    }

    /// <summary>投影进度并容忍损坏：PROGRESS 解析失败视为不存在（调用方重新初始化）。</summary>
    public static void ProjectProgress(string taskDirectory, HarnessTaskStatus status)
    {
        if (string.IsNullOrWhiteSpace(taskDirectory) || string.IsNullOrWhiteSpace(status.TaskId)) return;
        var existing = TryReadProgress(taskDirectory);
        var file = new HarnessProgressFile(
            SchemaVersion: 1,
            TaskId: status.TaskId,
            Stage: NormalizeProgressStage(status),
            Summary: Truncate(status.Message, 400),
            UpdatedUtc: status.UpdatedUtc,
            CompletedChecks: existing?.CompletedChecks,
            TotalChecks: existing?.TotalChecks,
            CurrentCheck: existing?.CurrentCheck,
            Checks: existing?.Checks);
        AtomicFile.WriteAllText(ProgressPath(taskDirectory), JsonSerializer.Serialize(file, FileOptions));
    }

    /// <summary>
    /// 初始化进度协议：写入 totalChecks 与 pending 检查清单。若 worker 已写出较新的进度
    /// （含真实检查状态/计数），绝不覆盖其进度；Runner 只从真实事件投影 stage/summary。
    /// </summary>
    public static void EnsureProgress(string taskDirectory, string taskId, IReadOnlyList<string> checks)
    {
        if (string.IsNullOrWhiteSpace(taskDirectory) || string.IsNullOrWhiteSpace(taskId)) return;
        var existing = TryReadProgress(taskDirectory);
        if (existing is { UpdatedUtc: { } updated } && updated >= DateTime.UtcNow.AddMinutes(-2))
            return; // worker（或本轮初始化）已写入有效进度，保留。
        var items = (checks ?? []).Select(check => new HarnessProgressCheckItem(check, "pending")).ToList();
        var file = new HarnessProgressFile(1, taskId, "starting", "正在校验合同并准备任务执行。", DateTime.UtcNow,
            0, items.Count, null, items);
        AtomicFile.WriteAllText(ProgressPath(taskDirectory), JsonSerializer.Serialize(file, FileOptions));
    }

    /// <summary>宽容读取 PROGRESS.json；缺失/损坏返回 null（绝不抛异常）。</summary>
    public static HarnessProgressFile? TryReadProgress(string taskDirectory)
    {
        if (string.IsNullOrWhiteSpace(taskDirectory)) return null;
        return TryReadFile<HarnessProgressFile>(ProgressPath(taskDirectory));
    }

    // ---- active-harness-task 原子记录 ----

    /// <summary>写入当前项目的活动任务记录（启动/接回/取消意图）。失败静默：项目锁文件仍是原子互斥事实来源。</summary>
    public static void WriteActiveRecord(string projectRoot, string taskId, string? sessionId, string state)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathRooted(projectRoot) || string.IsNullOrWhiteSpace(taskId)) return;
        try
        {
            var record = new HarnessActiveTaskRecord(taskId, projectRoot, sessionId, state, DateTime.UtcNow);
            AtomicFile.WriteAllText(ActiveRecordPath(projectRoot), JsonSerializer.Serialize(record, FileOptions));
        }
        catch
        {
            // 尽力而为：active 记录是统一语义的可读事实，写入失败不影响原子互斥与真相源。
        }
    }

    /// <summary>宽容读取活动任务记录；缺失/损坏返回 null。</summary>
    public static HarnessActiveTaskRecord? TryReadActiveRecord(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathRooted(projectRoot)) return null;
        return TryReadFile<HarnessActiveTaskRecord>(ActiveRecordPath(projectRoot));
    }

    /// <summary>活动记录仍属于该任务时清除（终态后调用）；属于其他任务时不动（机械并行/他人占位）。</summary>
    public static void TryRemoveActiveRecord(string projectRoot, string taskId)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(taskId) || !Path.IsPathRooted(projectRoot)) return;
        var record = TryReadActiveRecord(projectRoot);
        if (record is null || !string.Equals(record.TaskId, taskId, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(ActiveRecordPath(projectRoot)); } catch { /* 删除失败时记录保持，读方以真相源为准 */ }
    }

    // ---- 内部 ----

    /// <summary>把注册表状态解析为"真相源优先"：任务目录存在真源 → 用真源；否则回退旧索引并迁移一次。</summary>
    private static HarnessTaskStatus ResolveStatus(HarnessTaskStatus registry)
    {
        var directory = registry.TaskDirectory;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var truth = TryReadFile<HarnessTaskStatus>(StatusPath(directory));
            if (truth is not null)
            {
                // `task-directory` 是持久状态的真相源；只有 Runner 明确写入的实时
                // 传输来源才可穿透该标签。旧索引来源（legacy-registry）若被迁移到
                // 任务目录，必须改标为 task-directory，否则会破坏真相源优先语义。
                var transportSource = truth.StateSource is "node-relay" or "dotnet-websocket";
                return truth with { StateSource = transportSource ? truth.StateSource : "task-directory" };
            }
            if (Directory.Exists(directory))
            {
                // 旧索引迁移：把可读旧状态提升为任务目录真相源（幂等，失败不影响读取结果）。
                try { AtomicFile.WriteAllText(StatusPath(directory), JsonSerializer.Serialize(registry, StatusOptions)); }
                catch { /* 迁移失败：继续按旧状态展示，不误标终态 */ }
            }
        }
        return registry with { StateSource = "legacy-registry" };
    }

    /// <summary>进度阶段归一：可信终态变化才写终态阶段，绝不把进行中阶段残留为完成。</summary>
    private static string? NormalizeProgressStage(HarnessTaskStatus status)
    {
        if (string.Equals(status.State, "awaiting-gpt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.State, "completed", StringComparison.OrdinalIgnoreCase))
            return "done";
        if (string.Equals(status.State, "cancelled", StringComparison.OrdinalIgnoreCase))
            return "cancelled";
        if (string.Equals(status.State, "busy", StringComparison.OrdinalIgnoreCase))
            return "blocked";
        if (!status.IsRunning)
            return status.Stage ?? "failed";
        return status.Stage ?? (string.Equals(status.State, "starting", StringComparison.OrdinalIgnoreCase) ? "starting" : "running");
    }

    private static string RegistryFileName(string taskId) => taskId + ".json";

    private static bool IsSafeTaskId(string taskId)
        => taskId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && taskId.IndexOf(Path.DirectorySeparatorChar) < 0
            && taskId.IndexOf(Path.AltDirectorySeparatorChar) < 0;

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static T? TryReadFile<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            // HarnessTaskStatus 需要兼容旧日期格式（/Date(ms)/、Unix 毫秒与 ISO），其余文件用标准选项。
            var options = typeof(T) == typeof(HarnessTaskStatus) ? StatusOptions : FileOptions;
            return JsonSerializer.Deserialize<T>(text, options);
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string? text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max] + "…");
}

/// <summary>PROGRESS.json 单条检查（与 Reasonix 协议兼容：名称 + pending/running/passed/failed）。</summary>
public sealed record HarnessProgressCheckItem(string Name, string Status);

/// <summary>
/// 任务目录内 PROGRESS.json 的标准结构（字段与 Reasonix 兼容：
/// stage/summary/updatedUtc/completedChecks/totalChecks/currentCheck/checks）。
/// </summary>
public sealed record HarnessProgressFile(
    int? SchemaVersion,
    string? TaskId,
    string? Stage,
    string? Summary,
    DateTime? UpdatedUtc,
    int? CompletedChecks,
    int? TotalChecks,
    string? CurrentCheck,
    IReadOnlyList<HarnessProgressCheckItem>? Checks);

/// <summary>项目内 active-harness-task 原子记录：启动/接回/停止/对账统一引用的活动任务事实。</summary>
public sealed record HarnessActiveTaskRecord(
    string TaskId,
    string ProjectRoot,
    string? SessionId,
    string State,
    DateTime UpdatedUtc);
