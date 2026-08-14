namespace CodexHelper.Core.Services;

/// <summary>任务规模路由结果（3.4.1）：GPT 直接实现 / 单个 Reasonix 合同 / Reasonix 有限并行候选。</summary>
public enum ReasonixRoutingKind
{
    GptDirect,
    ReasonixSingle,
    ReasonixParallelCandidate
}

/// <summary>
/// 任务路由输入摘要：GPT 规划时对合同规模的预估。纯数据、无 IO、可单元测试。
/// 均为默认路由依据，不做安全授权扩张。
/// </summary>
public sealed record ReasonixRoutingRequest
{
    /// <summary>预计涉及文件数。</summary>
    public int EstimatedFileCount { get; init; }

    /// <summary>预计有效改动行数。</summary>
    public int EstimatedChangedLines { get; init; }

    /// <summary>是否涉及安全、凭据、数据迁移、备份恢复、并发协调、安装升级、公共 runner 或核心配置结构。</summary>
    public bool HighRisk { get; init; }

    /// <summary>用户是否明确要求 Reasonix/DeepSeek 实施。</summary>
    public bool UserRequestedReasonix { get; init; }

    /// <summary>是否为 Reasonix 主体完成后的验收微修（仅剩不超过 2 文件/80 行且低风险）。</summary>
    public bool AcceptanceMicroFix { get; init; }

    /// <summary>是否含至少两个独立模块（中大型并行候选前置）。</summary>
    public bool HasIndependentModules { get; init; }

    /// <summary>是否需二次接线（接口未冻结、需跨模块/公共接口接线）。</summary>
    public bool RequiresWiring { get; init; }
}

/// <summary>任务路由决策：三档结果之一与中文原因。</summary>
public sealed record ReasonixRoutingDecision(ReasonixRoutingKind Kind, string Reason);

/// <summary>
/// 3.4.1 任务规模路由纯函数：判定实现类任务应交给 GPT 直接实现、单个 Reasonix 合同，
/// 还是 Reasonix 有限并行候选。默认路由而非安全授权扩张；无副作用、可单元测试。
/// </summary>
public static class ReasonixTaskRouter
{
    /// <summary>微任务阈值：预计不超过 2 个文件。</summary>
    public const int MicroMaxFiles = 2;

    /// <summary>微任务阈值：预计有效改动不超过约 80 行。</summary>
    public const int MicroMaxLines = 80;

    public static ReasonixRoutingDecision Decide(ReasonixRoutingRequest request)
    {
        // 用户明确指定 Reasonix/DeepSeek：即使任务很小也走 Reasonix 单合同。
        if (request.UserRequestedReasonix)
            return new(ReasonixRoutingKind.ReasonixSingle, "用户明确要求 Reasonix/DeepSeek 实施，即使任务很小也走 Reasonix 单合同。");

        // 验收微修：Reasonix 主体完成后仅剩不超过 2 文件/80 行且低风险的修复，由 GPT 直接修，不再启动新 Reasonix。
        if (request.AcceptanceMicroFix && !request.HighRisk
            && request.EstimatedFileCount <= MicroMaxFiles
            && request.EstimatedChangedLines <= MicroMaxLines)
            return new(ReasonixRoutingKind.GptDirect, "仅剩不超过 2 文件/80 行且低风险的验收微修，由 GPT 直接修复，不再启动新 Reasonix 合同。");

        // 微任务（GPT 直接实现）：同时满足 ≤2 文件、≤80 行、低风险、不新增跨模块/公共接口。
        bool isMicro = request.EstimatedFileCount <= MicroMaxFiles
            && request.EstimatedChangedLines <= MicroMaxLines
            && !request.HighRisk
            && !request.RequiresWiring;
        if (isMicro)
            return new(ReasonixRoutingKind.GptDirect, "预计不超过 2 个文件、约 80 行，且不新增跨模块接口、不涉及高风险，一次聚焦测试即可验收，由 GPT 直接实现。");

        // Reasonix 有限并行候选：中大型任务含至少两个独立模块，接口冻结、写集合不重叠、无需二次接线、可机械合并。
        if (request.HasIndependentModules && !request.RequiresWiring)
            return new(ReasonixRoutingKind.ReasonixParallelCandidate, "含至少两个独立模块，接口冻结且写集合不重叠、无需二次接线、可机械合并，适合 Reasonix 有限并行。");

        // 其余一律单个 Reasonix 合同。
        return new(ReasonixRoutingKind.ReasonixSingle, "命中 Reasonix 单合同条件（规模较大、新增完整功能/跨模块接口、高风险或需多轮实现测试），交给单个 Reasonix 合同。");
    }
}
