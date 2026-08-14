using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>单任务 worktree/隔离目录准备结果状态。</summary>
public enum ReasonixWorktreeStatus { ParallelReady, SerialFallback, Blocked }

/// <summary>
/// Git 仓库只读探测契约。本服务不真正调用 git worktree add/remove，也不启动进程；
/// 生产接线方提供实现，测试用注入探针。返回的仓库文件均为相对项目根的路径。
/// </summary>
public interface IReasonixGitProbe
{
    bool IsRepository(string projectRoot);
    string? GetHead(string projectRoot);
    IReadOnlyList<string> GetDirtyFiles(string projectRoot);
    IReadOnlyList<string> GetUntrackedFiles(string projectRoot);
    bool DirectoryExists(string path);
}

/// <summary>worktree 准备输入：任务标识、项目根、配置的 worktree 根与允许写文件范围。</summary>
public sealed record ReasonixWorktreePreparationRequest(
    string TaskId,
    string ProjectRoot,
    string WorktreeRoot,
    IReadOnlyList<string> AllowedWriteFiles,
    bool DependsOnUntracked = false);

/// <summary>
/// 准备结果：parallelReady（可并行）/serialFallback（必须串行）/blocked（阻断）。
/// 并行就绪时给出唯一 worktreePath、branch 与 ref（HEAD）。
/// </summary>
public sealed record ReasonixWorktreePreparationResult(
    string TaskId,
    ReasonixWorktreeStatus Status,
    string Reason,
    string? WorktreePath = null,
    string? Branch = null,
    string? Ref = null,
    IReadOnlyList<string>? Conflicts = null);

/// <summary>cleanup plan 的单项：仅列出待清理路径，本合同不执行删除。</summary>
public sealed record ReasonixWorktreeCleanupItem(string WorktreePath, string Reason);

/// <summary>只读清理计划：列出准备阶段生成、可用于后续清理的路径。</summary>
public sealed record ReasonixCleanupPlan(
    string TaskId,
    string ProjectRoot,
    IReadOnlyList<ReasonixWorktreeCleanupItem> Items);

/// <summary>
/// Reasonix 独立工作区准备领域层（SPEC 3.4.0 合同 C）。纯函数判定 + 注入探针：
/// 生成安全、唯一、限定在配置 worktree 根内的隔离目录（优先 git worktree）；
/// 运行前验证 Git 仓库、HEAD、目标目录、任务 ID、允许写文件；路径越界或宽泛通配符阻断；
/// 触及脏文件必须串行；依赖未跟踪文件转串行。本类型不真正创建/删除 worktree。
/// </summary>
public sealed class ReasonixWorktreePreparationService
{
    private const int MaxUniqueAttempts = 50;
    private readonly IReasonixGitProbe probe;

    public ReasonixWorktreePreparationService(IReasonixGitProbe probe)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>生成并判定某任务的准备结果。autoWorktreeEnabled 来自协作设置，关闭时转串行。</summary>
    public ReasonixWorktreePreparationResult Prepare(ReasonixWorktreePreparationRequest request, bool autoWorktreeEnabled)
    {
        var taskId = request?.TaskId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(taskId))
            return Block(taskId, "任务 ID 为空");

        var projectRoot = SafeFull(request!.ProjectRoot);
        var worktreeRoot = SafeFull(request.WorktreeRoot);
        if (projectRoot is null || worktreeRoot is null)
            return Block(taskId, "项目根或 worktree 根无效");

        // 写范围验证：宽泛通配符或越界一律阻断。
        var ranges = new List<string>();
        foreach (var entry in request.AllowedWriteFiles ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (entry.IndexOfAny(new[] { '*', '?' }) >= 0)
                return Block(taskId, $"宽泛通配符写路径被阻断：{entry}", conflicts: [$"通配符:{entry}"]);
            string resolved;
            try
            {
                resolved = Path.IsPathRooted(entry)
                    ? Path.GetFullPath(entry)
                    : PathSafety.CombineWithin(projectRoot, entry);
            }
            catch (Exception)
            {
                return Block(taskId, $"写路径无法解析：{entry}");
            }
            if (!PathSafety.IsWithin(resolved, projectRoot))
                return Block(taskId, $"写路径越界被阻断：{entry}");
            ranges.Add(Normalize(resolved));
        }
        if (ranges.Count == 0)
            return Block(taskId, "未声明允许写文件范围");

        if (!autoWorktreeEnabled)
            return new(taskId, ReasonixWorktreeStatus.SerialFallback, "自动 worktree 已关闭，任务需串行执行");

        if (!probe.IsRepository(projectRoot))
            return new(taskId, ReasonixWorktreeStatus.SerialFallback, "非 Git 仓库无法创建独立 worktree");

        var head = probe.GetHead(projectRoot);
        if (string.IsNullOrWhiteSpace(head))
            return new(taskId, ReasonixWorktreeStatus.SerialFallback, "仓库无 HEAD 提交，无法创建 worktree");

        var branch = BuildBranch(taskId);
        var worktreePath = ResolveUnique(worktreeRoot, branch);
        if (worktreePath is null)
            return Block(taskId, "目标 worktree 目录已存在且无法唯一化", branch, head, [$"重复目标:{branch}"]);

        // 脏文件：任务触及任一脏文件必须串行；不触及则允许创建 worktree。
        var dirty = NormalizeRepoFiles(projectRoot, probe.GetDirtyFiles(projectRoot));
        if (Overlaps(ranges, dirty))
            return new(taskId, ReasonixWorktreeStatus.SerialFallback, "任务触及仓库脏文件，必须串行", null, branch, head, ["脏文件冲突"]);

        // 未跟踪：worktree 中未跟踪文件不会自动存在，依赖则转串行。
        var untracked = NormalizeRepoFiles(projectRoot, probe.GetUntrackedFiles(projectRoot));
        if (request.DependsOnUntracked || Overlaps(ranges, untracked))
            return new(taskId, ReasonixWorktreeStatus.SerialFallback, "任务依赖未跟踪文件，worktree 中不会自动存在", null, branch, head, ["未跟踪文件依赖"]);

        return new(taskId, ReasonixWorktreeStatus.ParallelReady, "可创建独立 worktree", worktreePath, branch, head);
    }

    /// <summary>只读清理计划：列出准备阶段产生的待清理路径，不执行任何删除。</summary>
    public ReasonixCleanupPlan GetCleanupPlan(ReasonixWorktreePreparationRequest request, string? preparedWorktreePath)
    {
        var taskId = request?.TaskId?.Trim() ?? string.Empty;
        var worktreeRoot = request is null ? null : SafeFull(request.WorktreeRoot);
        var items = new List<ReasonixWorktreeCleanupItem>();
        if (!string.IsNullOrWhiteSpace(preparedWorktreePath) && worktreeRoot is not null)
        {
            var candidate = SafeFull(preparedWorktreePath);
            // 只清理限定在配置 worktree 根内的路径；根外或无法规范化的路径一律不纳入。
            if (candidate is not null && PathSafety.IsWithin(candidate, worktreeRoot))
                items.Add(new(candidate, "仅列出待清理路径，本合同不执行删除"));
        }
        return new(taskId, request?.ProjectRoot ?? string.Empty, items);
    }

    private string? ResolveUnique(string worktreeRoot, string branch)
    {
        for (var i = 1; i <= MaxUniqueAttempts; i++)
        {
            var candidate = Normalize(Path.Combine(worktreeRoot, i == 1 ? branch : $"{branch}-{i}"));
            if (!probe.DirectoryExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>从任务 ID 生成安全、唯一、git 合法的 branch 名。</summary>
    private static string BuildBranch(string taskId)
    {
        var cleaned = new string(taskId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
        if (cleaned.Length == 0) cleaned = "task";
        if (char.IsAsciiDigit(cleaned[0])) cleaned = "t-" + cleaned;
        return "reasonix/" + cleaned.ToLowerInvariant();
    }

    private static IReadOnlyList<string> NormalizeRepoFiles(string projectRoot, IReadOnlyList<string> files)
    {
        var result = new List<string>();
        foreach (var file in files ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            try
            {
                var full = Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file);
                result.Add(Normalize(full));
            }
            catch (Exception) { /* 跳过无法规范化的仓库文件 */ }
        }
        return result;
    }

    private static bool Overlaps(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        foreach (var x in a)
            foreach (var y in b)
                if (RangesOverlap(x, y)) return true;
        return false;
    }

    private static bool RangesOverlap(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var aDir = a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var bDir = b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(bDir, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(aDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static string? SafeFull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }

    private static ReasonixWorktreePreparationResult Block(
        string taskId, string reason, string? branch = null, string? @ref = null, IReadOnlyList<string>? conflicts = null)
        => new(taskId, ReasonixWorktreeStatus.Blocked, reason, null, branch, @ref, conflicts ?? []);
}
