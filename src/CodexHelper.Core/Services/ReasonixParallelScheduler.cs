namespace CodexHelper.Core.Services;

/// <summary>Reasonix 任务合同的输入状态。调度器只据此决策，不创建 worktree 或进程。</summary>
public enum ReasonixTaskState { Pending, Queued, Running, Completed, Failed }

/// <summary>Reasonix 多任务调度用的任务合同摘要（SPEC 3.4.0 合同 A）。</summary>
/// <remarks>
/// <c>MergeStrategy</c> 是 3.4.0 补入的合并策略对象，用于表达“接口已冻结/无需二次接线/可由 Git/Helper 机械合并”。
/// 默认（null）保守：候选组内存在多个待执行任务时不派并行，保持单一任务仍可直接执行。
/// </remarks>
public sealed record ReasonixSchedulerTask(
    string TaskId,
    string DisplayName,
    string TaskDirectory,
    string ProjectRoot,
    IReadOnlyList<string> AllowedWriteFiles,
    IReadOnlyList<string> DependencyTaskIds,
    ReasonixTaskState State,
    ReasonixMergeStrategy? MergeStrategy = null);

/// <summary>
/// 机械合并策略（SPEC 3.4.0）：只有接口已冻结、无需二次接线、不共享公共入口、且声明可由 Git/Helper
/// 机械合并时，候选组内多个任务才允许并行；否则保守串行等待。
/// </summary>
public sealed record ReasonixMergeStrategy(
    bool InterfaceFrozen = false,
    bool NeedsRewiring = false,
    bool SharesPublicEntry = false,
    bool CanMergeMechanically = false)
{
    /// <summary>是否允许机械并行合并：声明可合并且接口冻结、无需二次接线、不共享公共入口。</summary>
    public bool CanParallelMerge =>
        CanMergeMechanically && InterfaceFrozen && !NeedsRewiring && !SharesPublicEntry;
}

/// <summary>单任务调度决策结果：ready/running/waiting_dependency/waiting_conflict/waiting_merge/queued/completed/failed。</summary>
public enum ReasonixDecisionStatus { Ready, Running, WaitingDependency, WaitingConflict, WaitingMerge, Queued, Completed, Failed }

/// <summary>每项任务的调度决策：状态、可读原因与（冲突时的）冲突任务 ID。</summary>
public sealed record ReasonixTaskDecision(
    string TaskId,
    ReasonixDecisionStatus Status,
    string Reason,
    string? ConflictTaskId = null);

/// <summary>可供 UI 使用的调度快照统计：running/queued/blocked/completed/failed/maxConcurrency。</summary>
public sealed record ReasonixSchedulerSnapshot(
    int Running, int Queued, int Blocked, int Completed, int Failed, int MaxConcurrency);

/// <summary>一轮纯函数调度的结果：决策列表与快照统计。</summary>
public sealed record ReasonixScheduleResult(
    IReadOnlyList<ReasonixTaskDecision> Decisions,
    ReasonixSchedulerSnapshot Snapshot);

/// <summary>
/// Reasonix 多任务并行调度领域层。纯函数调度：已运行任务占槽；依赖未成功则等待；
/// 写文件集合重叠则冲突并转串行等待；不同项目/不同文件且有空槽则可启动。
/// 路径比较在 Windows 下大小写不敏感、统一绝对/相对分隔符；目录所有权覆盖其子文件；
/// 通配符或无法规范化的范围保守视为冲突。本类型不真正创建 worktree 或进程，
/// 也不修改现有单任务执行行为。
/// </summary>
public sealed class ReasonixParallelScheduler
{
    public const int DefaultMaxConcurrency = 2;
    public const int MinMaxConcurrency = 1;
    public const int MaxMaxConcurrency = 3;

    public ReasonixParallelScheduler(int maxConcurrency = DefaultMaxConcurrency)
    {
        if (maxConcurrency < MinMaxConcurrency || maxConcurrency > MaxMaxConcurrency)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), $"并发必须位于 {MinMaxConcurrency}..{MaxMaxConcurrency}。");
        MaxConcurrency = maxConcurrency;
    }

    public int MaxConcurrency { get; }

    public ReasonixScheduleResult Schedule(IReadOnlyList<ReasonixSchedulerTask> tasks)
    {
        var list = (tasks ?? Array.Empty<ReasonixSchedulerTask>()).ToList();
        var byId = new Dictionary<string, ReasonixSchedulerTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in list)
            if (!string.IsNullOrWhiteSpace(task.TaskId)) byId[task.TaskId] = task;

        var decisions = new List<ReasonixTaskDecision>(list.Count);
        // 已占用写范围的进行中任务（running/ready/queued），用于冲突判定与串行等待排队。
        var occupied = new List<WriteScope>();
        // 已占槽数：输入 running 即已占槽，本批 ready 亦立即占槽；queued 等待槽位不占。
        var runningSlots = list.Count(t => t.State == ReasonixTaskState.Running);
        // SPEC 3.4.0：候选组内待执行任务数。>1 表示“拆成多个并行子合同”的候选组，须显式声明可机械合并且接口冻结。
        var pendingCount = list.Count(t => t.State == ReasonixTaskState.Pending);

        foreach (var task in list)
        {
            switch (task.State)
            {
                case ReasonixTaskState.Completed:
                    decisions.Add(new(task.TaskId, ReasonixDecisionStatus.Completed, "已完成"));
                    continue;
                case ReasonixTaskState.Failed:
                    decisions.Add(new(task.TaskId, ReasonixDecisionStatus.Failed, "已失败"));
                    continue;
                case ReasonixTaskState.Running:
                    decisions.Add(new(task.TaskId, ReasonixDecisionStatus.Running, "运行中"));
                    occupied.Add(BuildScope(task));
                    continue;
            }

            var unsatisfied = FirstUnsatisfiedDependency(task, byId);
            if (unsatisfied is not null)
            {
                decisions.Add(new(task.TaskId, ReasonixDecisionStatus.WaitingDependency, "依赖未成功：" + unsatisfied));
                continue;
            }

            // SPEC 3.4.0：候选组内多个待执行任务须显式声明可机械合并且接口冻结等，才允许并行；否则保守串行等待并给中文原因。
            // 单一待执行任务（无并行拆分候选组）不受此规则阻断，保持可执行。写冲突优先于合并判定报告。
            var scope = BuildScope(task);
            var conflict = occupied.FirstOrDefault(candidate => TasksConflict(candidate, scope));
            if (conflict is not null)
            {
                decisions.Add(new(task.TaskId, ReasonixDecisionStatus.WaitingConflict, "写文件范围冲突", conflict.TaskId));
                continue;
            }

            if (pendingCount > 1)
            {
                var blockReason = MergeBlockReason(task.MergeStrategy);
                if (blockReason is not null)
                {
                    decisions.Add(new(task.TaskId, ReasonixDecisionStatus.WaitingMerge, blockReason));
                    continue;
                }
            }

            if (runningSlots >= MaxConcurrency)
            {
                decisions.Add(new(task.TaskId, ReasonixDecisionStatus.Queued, "并发槽位已满"));
                occupied.Add(scope);
                continue;
            }

            decisions.Add(new(task.TaskId, ReasonixDecisionStatus.Ready, "可启动"));
            runningSlots++;
            occupied.Add(scope);
        }

        return new(decisions, BuildSnapshot(decisions, runningSlots));
    }

    private static string? FirstUnsatisfiedDependency(ReasonixSchedulerTask task, IReadOnlyDictionary<string, ReasonixSchedulerTask> byId)
    {
        if (task.DependencyTaskIds is null) return null;
        foreach (var dependency in task.DependencyTaskIds)
        {
            if (string.IsNullOrWhiteSpace(dependency)) continue;
            if (!byId.TryGetValue(dependency, out var dependent) || dependent.State != ReasonixTaskState.Completed)
                return dependency;
        }
        return null;
    }

    /// <summary>返回该任务不可机械合并的阻断中文原因；null 表示允许并行 ready。</summary>
    private static string? MergeBlockReason(ReasonixMergeStrategy? strategy)
    {
        if (strategy is null) return "未声明可机械合并，保守按串行执行";
        if (!strategy.CanMergeMechanically) return "未声明可由 Git/Helper 机械合并";
        if (!strategy.InterfaceFrozen) return "接口未冻结，需二次接线后合并";
        if (strategy.NeedsRewiring) return "需要二次接线，不可并行合并";
        if (strategy.SharesPublicEntry) return "共享 UI/配置/公共模型或公共入口，需串行合并";
        return null;
    }

    private static bool TasksConflict(WriteScope a, WriteScope b)
    {
        // 不同项目永不冲突（只受并发槽位限制）。
        if (!string.Equals(a.ProjectRoot, b.ProjectRoot, StringComparison.OrdinalIgnoreCase)) return false;
        // 通配符或无法规范化的范围保守视为冲突。
        if (a.Unresolvable || b.Unresolvable) return true;
        foreach (var x in a.Ranges)
            foreach (var y in b.Ranges)
                if (RangesOverlap(x, y)) return true;
        return false;
    }

    /// <summary>两条规范化路径是否重叠：相等，或一条是另一条的目录前缀（目录所有权覆盖其子文件）。</summary>
    private static bool RangesOverlap(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var aDir = a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var bDir = b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(bDir, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(aDir, StringComparison.OrdinalIgnoreCase);
    }

    private static WriteScope BuildScope(ReasonixSchedulerTask task)
    {
        var root = Normalize(task.ProjectRoot ?? string.Empty);
        var ranges = new List<string>();
        var unresolvable = false;
        foreach (var entry in task.AllowedWriteFiles ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var full = NormalizeWrite(root, entry);
            if (full is null) { unresolvable = true; continue; }
            ranges.Add(full);
        }
        return new WriteScope(task.TaskId, root, ranges, unresolvable);
    }

    /// <summary>相对条目相对 projectRoot 解析并统一为绝对规范化路径；通配符或无法规范化返回 null。</summary>
    private static string? NormalizeWrite(string root, string entry)
    {
        if (entry.IndexOfAny(new[] { '*', '?' }) >= 0) return null;
        try
        {
            var combined = Path.IsPathRooted(entry) ? entry : Path.Combine(root, entry);
            return Normalize(combined);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>统一绝对路径分隔符并规范化（Windows 下大小写比较在调用处使用 OrdinalIgnoreCase）。</summary>
    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private ReasonixSchedulerSnapshot BuildSnapshot(IEnumerable<ReasonixTaskDecision> decisions, int runningSlots)
    {
        var queued = 0;
        var blocked = 0;
        var completed = 0;
        var failed = 0;
        foreach (var decision in decisions)
        {
            switch (decision.Status)
            {
                case ReasonixDecisionStatus.Queued: queued++; break;
                case ReasonixDecisionStatus.WaitingDependency:
                case ReasonixDecisionStatus.WaitingConflict:
                case ReasonixDecisionStatus.WaitingMerge: blocked++; break;
                case ReasonixDecisionStatus.Completed: completed++; break;
                case ReasonixDecisionStatus.Failed: failed++; break;
            }
        }
        return new ReasonixSchedulerSnapshot(runningSlots, queued, blocked, completed, failed, MaxConcurrency);
    }

    private sealed class WriteScope
    {
        public WriteScope(string taskId, string projectRoot, List<string> ranges, bool unresolvable)
        {
            TaskId = taskId;
            ProjectRoot = projectRoot;
            Ranges = ranges;
            Unresolvable = unresolvable;
        }

        public string TaskId { get; }
        public string ProjectRoot { get; }
        public List<string> Ranges { get; }
        public bool Unresolvable { get; }
    }
}
