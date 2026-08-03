namespace CodexHelper.Core.Services;

/// <summary>
/// Reasonix 任务状态的用户可读文案。全部逻辑放在 Core 以便自动化测试；
/// Helper UI 只负责展示。区分执行成功、执行器退出异常但交付报告存在、
/// 真正失败与用户停止，且绝不把 reasoning 碎片当作步骤。
/// </summary>
public static class ReasonixUiText
{
    public static string DesktopStateText(ReasonixTaskStatus task)
    {
        if (task.HasBoundSession) return "已同步到 Reasonix App（会话已注册）";
        if (task.IsRunning) return "Reasonix 会话尚未落盘；当前为实时事件视图（Reasonix Desktop 将在任务结束后同步）";
        return task.DesktopState == "awaiting-session" ? "任务未生成新会话文件（未绑定旧会话）" : "未绑定 Reasonix 会话";
    }

    public static string ReturnStateText(ReasonixTaskStatus task) => task.ReturnState switch
    {
        "same-turn-resume" => "原 GPT 任务已恢复，可开始验收",
        "executor-error" => "执行器异常退出，但已生成交付报告，GPT 可验收",
        "thread-unresolved" => "未识别原 Codex 任务",
        _ => task.IsRunning ? "原 GPT 任务等待 Reasonix 完成" : "原 GPT 任务已恢复（无交付报告）"
    };

    public static string ActivitySummary(ReasonixTaskStatus task)
    {
        var parts = new List<string>();
        parts.Add(task.IsRunning
            ? $"{ModelTurnCountFor(task):N0} 模型轮次"
            : $"{task.StepCount:N0} 实际步骤");
        parts.Add($"{task.ToolCallCount:N0} 次工具调用");
        if (task.ReasoningEventCount > 0) parts.Add($"{task.ReasoningEventCount:N0} 条推理流事件");
        if (task.TokenInput > 0 || task.TokenOutput > 0)
            parts.Add($"输入 {FormatTokens(task.TokenInput)} · 输出 {FormatTokens(task.TokenOutput)} · 缓存命中 {FormatTokens(task.CacheHitTokens)}");
        return string.Join(" · ", parts);
    }

    /// <summary>运行中只有模型轮次（turn_started），不得称作实际步骤；旧状态缺 ModelTurnCount 时回退到 StepCount。</summary>
    private static long ModelTurnCountFor(ReasonixTaskStatus task) => task.ModelTurnCount > 0 ? task.ModelTurnCount : task.StepCount;

    public static string OutcomeLine(ReasonixTaskStatus task)
    {
        var reportExists = File.Exists(Path.Combine(task.TaskDirectory, "EXECUTION_REPORT.md"));
        return task.State.ToLowerInvariant() switch
        {
            "completed" => reportExists ? "执行完成，等待 GPT 独立验收" : "执行完成，但未找到执行报告",
            "failed" when reportExists => "执行器异常退出，但交付报告存在，等待 GPT 验收",
            "failed" => "执行失败：" + (string.IsNullOrWhiteSpace(task.Message) ? "Reasonix 未生成交付报告" : task.Message),
            "cancelled" => "用户已停止任务",
            "interrupted" => reportExists ? "执行进程意外退出，执行报告可供验收" : "执行进程意外退出，未生成执行报告",
            "starting" => "正在读取任务合同",
            _ => task.IsRunning ? "Reasonix 正在执行任务" : task.Message
        };
    }

    public static string SummaryText(ReasonixTaskStatus task)
    {
        var reportExists = File.Exists(Path.Combine(task.TaskDirectory, "EXECUTION_REPORT.md"));
        var strategy = string.IsNullOrWhiteSpace(task.ExecutionIntensity) ? string.Empty : $"\n策略：{task.StrategyDisplay}（{DescribeSource(task)}）";
        var budget = DescribeBudget(task);
        var failure = DescribeFailure(task);
        var attempt = task.AttemptNumber is > 0 ? $"\n尝试：第 {task.AttemptNumber} 次" : string.Empty;
        return $"{task.TaskId} · {DescribeState(task)} · {task.Phase}\n项目：{task.ProjectRoot}\n活动：{ActivitySummary(task)}\nReasonix：{DesktopStateText(task)}\nCodex：{ReturnStateText(task)}\n事件：{task.EventCount:N0} · 更新：{task.UpdatedUtc.ToLocalTime():HH:mm:ss}\n{OutcomeLine(task)}" + budget + failure + attempt + strategy + ProgressLine(task) + (reportExists ? "\n已生成 EXECUTION_REPORT.md" : string.Empty);
    }

    /// <summary>软预算展示：只称“已超过软预算”，绝不称作超时；预算从不终止任务。
    /// 运行中只按模型轮次给出估计提醒，明确标注最终以 metrics 为准。</summary>
    private static string DescribeBudget(ReasonixTaskStatus task)
    {
        var estimate = task.IsRunning ? "（运行中为估计提醒，最终以 metrics 为准）" : string.Empty;
        return task.BudgetState switch
        {
            "exceeded" => $"\n已超过软预算（超支 {task.BudgetOverrunSteps ?? 0} 步，未终止任务）{estimate}",
            "warning" => $"\n已接近软预算（超支 {task.BudgetOverrunSteps ?? 0} 步，未终止任务）{estimate}",
            _ => string.Empty
        };
    }

    /// <summary>失败分类短展示：仅结构化类型，不含命令/正文/秘密。</summary>
    private static string DescribeFailure(ReasonixTaskStatus task)
    {
        if (string.IsNullOrWhiteSpace(task.FailureKind)) return string.Empty;
        var kind = task.FailureKind switch
        {
            "model-run-failed" => "模型运行失败",
            "cli-exit" => "CLI 退出异常",
            "missing-report" => "缺少交付报告",
            "worker-check-failed" => "worker 检查失败",
            "host-error" => "宿主异常",
            "user-stopped" => "用户停止",
            "interrupted" => "中断",
            _ => task.FailureKind
        };
        var summary = string.IsNullOrWhiteSpace(task.FailureSummary) ? string.Empty : $"：{task.FailureSummary}";
        return $"\n失败类型：{kind}{summary}";
    }

    /// <summary>阶段行：只展示结构化摘要（stage/summary/检查计数/旧态提示），绝不展示思维链、命令或源码。</summary>
    public static string ProgressLine(ReasonixTaskStatus task)
    {
        var stage = ProgressStageText(task);
        if (string.IsNullOrWhiteSpace(stage))
        {
            return string.IsNullOrWhiteSpace(task.ProgressDiagnostic)
                ? string.Empty
                : $"\n阶段：{task.ProgressDiagnostic}";
        }
        var summary = string.IsNullOrWhiteSpace(task.ProgressSummary) ? string.Empty : $"：{task.ProgressSummary}";
        var checks = task.TotalChecks is > 0 ? $"（{task.CompletedChecks ?? 0}/{task.TotalChecks} 项检查）" : string.Empty;
        var source = string.IsNullOrWhiteSpace(task.ProgressSource) ? string.Empty
            : task.ProgressSource.Equals("reasonix", StringComparison.OrdinalIgnoreCase) ? "（Reasonix 报告）" : "（Helper 推断）";
        return $"\n阶段：{stage}{source}{summary}{checks}{StaleProgressHint(task)}";
    }

    /// <summary>阶段更新较久未动只做提示，绝不按时间终止任务。</summary>
    public static string StaleProgressHint(ReasonixTaskStatus task)
    {
        if (!task.IsRunning || task.ProgressUpdatedUtc is null) return string.Empty;
        var age = DateTime.UtcNow - task.ProgressUpdatedUtc.Value;
        if (age < TimeSpan.Zero) return string.Empty;   // 防御未来时间：绝不显示负陈旧提示
        if (age.TotalMinutes < 20) return string.Empty;
        return age.TotalHours >= 1
            ? $"（阶段更新已 {age.TotalHours:0.#} 小时，仅提示）"
            : $"（阶段更新已 {age.TotalMinutes:0.#} 分钟，仅提示）";
    }

    private static string ProgressStageText(ReasonixTaskStatus task)
    {
        if (string.IsNullOrWhiteSpace(task.ProgressStage)) return string.Empty;
        return task.ProgressStage.ToLowerInvariant() switch
        {
            "analyzing" => "正在分析",
            "implementing" => "正在实现",
            "testing" => "正在测试",
            "reporting" => "正在整理交付",
            "done" => "已完成",
            "blocked" => "受阻",
            _ => task.ProgressStage
        };
    }

    private static string DescribeSource(ReasonixTaskStatus task) => task.ExecutionSource switch
    {
        "manifest" => "manifest 声明",
        "inferred" => "自动推断",
        _ => "未知"
    };

    private static string DescribeState(ReasonixTaskStatus task) => task.State.ToLowerInvariant() switch
    {
        "running" => "运行中",
        "starting" => "启动中",
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已停止",
        "interrupted" => "中断",
        _ => task.State
    };

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000m:0.#}M",
        >= 1_000 => $"{tokens / 1_000m:0.#}k",
        _ => tokens.ToString("N0")
    };
}

/// <summary>构建“返回原 Codex 任务”的严格 URI：只接受 `codex://threads/&lt;UUID&gt;`。
/// ReturnUri 非法时忽略并用合法 CodexThreadId 重建；两者均无效返回 null（按钮禁用）。</summary>
public static class CodexThreadUri
{
    public static string? Build(string? returnUri, string? codexThreadId)
    {
        if (TryExtractUuid(returnUri, out var fromReturnUri)) return $"codex://threads/{fromReturnUri}";
        if (IsValidUuid(codexThreadId)) return $"codex://threads/{codexThreadId}";
        return null;
    }

    /// <summary>严格 UUID：必须是带连字符的标准 8-4-4-4-12 形式。</summary>
    public static bool IsValidUuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('-')) return false;
        return Guid.TryParse(value, out _);
    }

    private static bool TryExtractUuid(string? returnUri, out string uuid)
    {
        uuid = string.Empty;
        if (string.IsNullOrWhiteSpace(returnUri)) return false;
        if (!Uri.TryCreate(returnUri, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "codex", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "threads", StringComparison.OrdinalIgnoreCase)) return false;
        var path = uri.AbsolutePath.Trim('/');
        if (path.Length == 0 || path.IndexOf('/') >= 0) return false;   // 必须是单段
        if (!IsValidUuid(path)) return false;
        uuid = path;
        return true;
    }
}
