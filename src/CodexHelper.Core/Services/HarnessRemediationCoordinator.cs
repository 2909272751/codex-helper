using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>修复编排决策：Outcome 见 <see cref="HarnessRemediationCoordinator"/>（含终态/接回/不适用）。</summary>
public sealed record HarnessRemediationDecision(
    string Outcome,
    string Message,
    string TaskId,
    string TaskDirectory,
    HarnessRemediationRecord? Record);

/// <summary>triage turn 结果（verdict 为 <see cref="GptRemediationStore"/> 取值）。</summary>
public sealed record HarnessTriageResult(string Verdict, string? SafeSummary, string? ScopeSummary);

/// <summary>修复 DSH 运行结果（CompletedWithGate=真实完成 + 报告门禁通过）。</summary>
public sealed record HarnessRepairRunResult(bool CompletedWithGate, string Message);

/// <summary>复验结果（verdict：accepted / needs-repair / acceptance-failed）。</summary>
public sealed record HarnessRevalidationResult(string Verdict, string? SafeSummary);

/// <summary>
/// 阶段四失败合同编排协调器（一次且仅一次）：needs-repair / acceptance-failed / DSH failed/cancelled/
/// 报告门禁失败 → 一次独立 triage（App Server 线程，最小权限）→ 按 verdict 串行：
/// product-repair：在原项目唯一创建同根因键修复合同并启动一次受管 DSH；helper-repair：在 Codex Helper
/// 项目建独立根因键合同（需 HelperProjectRootProvider，否则 needs-user，绝不假称修复）；
/// no-repair-needed / remediation-indeterminate：终态不启动 DSH；needs-user：终态。
/// 修复 DSH 真实完成 + 报告门禁后执行一次独立复验，accepted 才终态 accepted；其余落
/// remediation-failed / needs-user，绝不自动第二轮（容量上限 1，防额度循环）。
/// 全程串行：原 Runner 已退出、triage/复验为独立线程终态后才启动 DSH；不 GPT/DSH 并发写同一项目。
/// 所有命令行只带绝对路径/模式/安全 ID；合同正文、密钥、Token、Cookie、DSH 消息正文绝不进入命令行/记录。
/// </summary>
public sealed class HarnessRemediationCoordinator
{
    public const string OutcomeNotApplicable = "not-applicable";
    public const string OutcomeAlreadyInProgress = "already-in-progress";
    /// <summary>原 DSH Runner 未证明确实退出：返回继续等待/不适用，绝不启动 GPT/DSH（阶段五串行门禁）。</summary>
    public const string OutcomeRunnerActive = "runner-active";

    // ---- 可注入边界（测试用假实现；生产用默认） ----

    /// <summary>triage 传输工厂（默认生成真实 codex app-server stdio 传输）。</summary>
    public Func<CancellationToken, Task<ICodexAppServerTransport>>? TriageTransportFactory { get; init; }

    /// <summary>复验传输工厂（默认同 triage）。</summary>
    public Func<CancellationToken, Task<ICodexAppServerTransport>>? RevalidateTransportFactory { get; init; }

    /// <summary>codex 可执行解析（默认 <see cref="CodexExecutableResolver.Resolve"/>）。</summary>
    public Func<string?>? CodexPathResolver { get; init; }

    /// <summary>triage 指令构建器（默认固定规则，测试可注入以捕获断言）。</summary>
    public Func<HarnessRemediationContext, string>? TriageInstructionBuilder { get; init; }

    /// <summary>triage runner（默认走 Codex App Server turn + GPT_REMEDIATION.json）。</summary>
    public Func<HarnessRemediationContext, CancellationToken, Task<HarnessTriageResult>>? TriageRunner { get; init; }

    /// <summary>修复 DSH runner（默认：受管子进程 legacy 同步 + 本地真相/报告门禁）。</summary>
    public Func<string, string, CancellationToken, Task<HarnessRepairRunResult>>? RepairRunner { get; init; }

    /// <summary>
    /// 修复 DSH 受管监督器工厂（第八阶段）：默认 <c>new HarnessSupervisor()</c>，使默认修复 DSH
    /// 以受管机制启动/等待并生成可信终态证据（PID/启动身份、监督终态 completed、报告门禁）。
    /// 测试注入 fake child supervisor；生产用真实子进程（Environment.ProcessPath 修复 runner）。
    /// </summary>
    public Func<HarnessSupervisor>? RepairSupervisorFactory { get; init; }

    /// <summary>复验 runner（默认：修复合同目录的独立验收 turn + GPT_ACCEPTANCE.json）。</summary>
    public Func<string, string, string, CancellationToken, Task<HarnessRevalidationResult>>? RevalidateRunner { get; init; }

    /// <summary>helper-repair 所需的 Helper 项目根解析器；缺失/解析失败时 helper 修复 → needs-user（不猜测）。
    /// 生产默认接线 <see cref="HarnessHelperRootResolver.TryResolve"/>（源码布局标记识别，拒绝把任意项目误认成 Helper）。</summary>
    public Func<string?>? HelperProjectRootProvider { get; init; }

    /// <summary>受管 Runner PID + 启动 UTC 双重身份接回（串行门禁用；测试注入受控实现）。</summary>
    public Func<int, DateTime, IHarnessSupervisedProcess?>? AttachRunner { get; init; }

    public TimeSpan TurnTimeout { get; init; } = CodexAcceptanceLauncher.DefaultTurnTimeout;

    /// <summary>
    /// 为原任务执行一次（最多一次）失败修复编排。可重复/多进程调用：任务已有 remediation 记录时只接回
    /// （运行中 → already-in-progress；终态 → 终态接回），绝不开启第二轮。
    /// </summary>
    public async Task<HarnessRemediationDecision> TryRemediationOnceAsync(
        string productProjectRoot,
        string taskDirectory,
        CancellationToken cancellationToken = default)
    {
        productProjectRoot = Path.GetFullPath(productProjectRoot);
        taskDirectory = Path.GetFullPath(taskDirectory);
        var runsRoot = Path.GetFullPath(Path.Combine(productProjectRoot, ".codex-helper", "runs"));
        if (!PathSafety.IsWithin(taskDirectory, runsRoot))
            throw new InvalidOperationException("失败修复编排只接受属于项目 .codex-helper/runs 的绝对任务目录。");
        var taskId = Path.GetFileName(taskDirectory);

        var existing = HarnessRemediationStore.TryReadRecord(taskDirectory);
        if (existing is not null)
            return AttachToRecord(existing, taskId, taskDirectory);

        // 阶段五串行门禁：任何 failure 分类 / triage / 修复合同启动 / revalidation 前必须证明确认
        // 原 DSH Runner 已退出（监督记录 PID+启动时间不存活 或 无监督记录时传统 lease 不存在），
        // 且 HARNESS_STATUS 非 running；无法证明则只返回继续等待/不适用，绝不启动 GPT/DSH。
        if (!OriginalRunnerExited(taskDirectory, out var gateReason))
            return new(OutcomeRunnerActive,
                "原 DSH Runner 未证明确实退出（" + gateReason + "）；返回继续等待/不适用，不启动 triage/修复（串行门禁）。",
                taskId, taskDirectory, null);

        // 触发源判定：只对明确失败类触发一次 triage。
        var source = ClassifyFailureSource(taskDirectory);
        if (source is null)
            return new(OutcomeNotApplicable, "无失败验收/失败真相记录，不进入修复编排（已通过或未失败）。", taskId, taskDirectory, null);
        if (string.Equals(source.AcceptanceState, HarnessAcceptanceQueue.FailedState, StringComparison.OrdinalIgnoreCase)
            && LooksLikeInfrastructureFailure(source.AcceptanceSafeSummary))
        {
            var claimedInfra = ClaimAndWriteNeedsUser(taskId, productProjectRoot, taskDirectory, source.Fingerprint,
                "验收基础设施不可用（App Server 启动/握手/协议失败，脱敏原因见验收记录），不假装修复自己；请人工检查验收基础设施。");
            return new(claimedInfra.State, "验收失败源于验收基础设施不可用，已保守落 needs-user（不假装修复，不消耗 triage 额度）。",
                taskId, taskDirectory, claimedInfra);
        }

        // 单飞占位（round=1，最多一次）。
        var now = DateTime.UtcNow;
        var claimed = HarnessRemediationStore.TryClaimQueued(new HarnessRemediationRecord(
            HarnessRemediationStore.RecordSchemaVersion, taskId, productProjectRoot, taskDirectory, source.Fingerprint,
            HarnessRemediationStore.TriageQueuedState, HarnessRemediationStore.MaxRoundsPerTask, now, now,
            LauncherPid: Environment.ProcessId, LauncherStartedUtc: CurrentProcessStartTimeUtc()));
        if (claimed is null)
        {
            var raced = HarnessRemediationStore.TryReadRecord(taskDirectory);
            if (raced is not null) return AttachToRecord(raced, taskId, taskDirectory);
            return new(OutcomeNotApplicable, "remediation 占位冲突且记录不可读，保守不开启修复（可诊断需人工检查）。", taskId, taskDirectory, null);
        }

        // 记录初始 round=1 于 LauncherPid 已存于 claimed（claim 中 LauncherPid 由构造占位？占位时含 pid）。
        var ctx = new HarnessRemediationContext(productProjectRoot, taskDirectory, taskId, source.Fingerprint);

        // ---- triage（一次，App Server 独立线程；基础设施不可用 → needs-user） ----
        var triageRunning = HarnessRemediationStore.Update(claimed, HarnessRemediationStore.TriageRunningState);
        HarnessTriageResult triage;
        try
        {
            var runner = TriageRunner ?? ((c, token) => RunDefaultTriageAsync(c, token));
            triage = await runner(ctx, cancellationToken);
        }
        catch (CodexAcceptanceException ex)
        {
            var record = HarnessRemediationStore.Update(triageRunning, HarnessRemediationStore.NeedsUserState,
                safeSummary: "triage 无法执行：" + HarnessAcceptanceQueue.Safe(ex.Message));
            return new(record.State, "triage（App Server）不可用或失败，已保守落 needs-user（不循环、不假成功）。", taskId, taskDirectory, record);
        }

        var verdict = (triage.Verdict ?? string.Empty).ToLowerInvariant();
        var scope = HarnessAcceptanceQueue.Safe(triage.ScopeSummary, 600);
        var summary = HarnessAcceptanceQueue.Safe(triage.SafeSummary, 400);

        if (string.Equals(verdict, GptRemediationStore.VerdictNoRepairNeeded, StringComparison.OrdinalIgnoreCase))
        {
            var record = HarnessRemediationStore.Update(triageRunning, HarnessRemediationStore.NoRepairNeededState,
                verdict: verdict, safeSummary: "triage 判定无需修复（不启动 DSH）。" + summary);
            return new(record.State, "triage 判定 no-repair-needed，不启动 DSH。", taskId, taskDirectory, record);
        }
        if (string.Equals(verdict, GptRemediationStore.VerdictIndeterminate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(verdict, GptRemediationStore.VerdictNeedsUser, StringComparison.OrdinalIgnoreCase))
        {
            var terminal = string.Equals(verdict, GptRemediationStore.VerdictNeedsUser, StringComparison.OrdinalIgnoreCase)
                ? HarnessRemediationStore.NeedsUserState : HarnessRemediationStore.IndeterminateState;
            var record = HarnessRemediationStore.Update(triageRunning, terminal, verdict: verdict,
                safeSummary: "triage 无法明确修复范围/需用户介入，不猜测修复。" + summary);
            return new(record.State, record.State == HarnessRemediationStore.NeedsUserState
                ? "triage 判定 needs-user（不猜测、不自动修复）。" : "triage 判定 remediation-indeterminate（不猜测修复）。",
                taskId, taskDirectory, record);
        }
        if (!string.Equals(verdict, GptRemediationStore.VerdictProductRepair, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(verdict, GptRemediationStore.VerdictHelperRepair, StringComparison.OrdinalIgnoreCase))
        {
            var record = HarnessRemediationStore.Update(triageRunning, HarnessRemediationStore.RemediationFailedState,
                verdict: verdict, safeSummary: "triage 返回未知 verdict（" + HarnessAcceptanceQueue.Safe(verdict) + "），保守失败。");
            return new(record.State, "triage 返回未知 verdict，保守 remediation-failed（不自动开新合同）。", taskId, taskDirectory, record);
        }

        // ---- repair：唯一创建修复合同并串行启动一次 DSH ----
        var helperRepair = string.Equals(verdict, GptRemediationStore.VerdictHelperRepair, StringComparison.OrdinalIgnoreCase);

        // 修复合同启动前复核串行门禁（原 Runner 仍存活 → 不建合同、不启动 DSH）。
        if (!OriginalRunnerExited(taskDirectory, out var contractGateReason))
        {
            var gated = HarnessRemediationStore.Update(triageRunning, HarnessRemediationStore.NeedsUserState,
                verdict: verdict, safeSummary: "修复合同启动前串行门禁未通过（" + contractGateReason + "），不启动修复 DSH。");
            return new(gated.State, "修复合同启动前串行门禁未通过，保守落 needs-user（绝不与运行中的原 Runner 并发）。",
                taskId, taskDirectory, gated);
        }

        string repairProjectRoot;
        if (helperRepair)
        {
            var helperRoot = HelperProjectRootProvider?.Invoke();
            if (string.IsNullOrWhiteSpace(helperRoot))
            {
                var record = HarnessRemediationStore.Update(triageRunning, HarnessRemediationStore.NeedsUserState,
                    verdict: verdict, safeSummary: "helper-repair 需要 Helper 项目根，当前不可解析；不猜测、不自动开合同。");
                return new(record.State, "triage 判定 helper-repair 但 Helper 项目根不可用，已落 needs-user。", taskId, taskDirectory, record);
            }
            repairProjectRoot = Path.GetFullPath(helperRoot);
        }
        else
        {
            repairProjectRoot = productProjectRoot;
        }

        var rootCauseKey = ReadRootCauseKey(taskDirectory) ?? source.Fingerprint ?? taskId;
        string repairTaskDirectory;
        try
        {
            repairTaskDirectory = helperRepair
                ? CreateRepairContract(repairProjectRoot, ctx, "helper-remediation", "codex-helper-remediation-" + taskId, scope)
                : CreateRepairContract(repairProjectRoot, ctx, "product-remediation", rootCauseKey, scope);
        }
        catch (Exception ex)
        {
            var record = HarnessRemediationStore.Update(triageRunning, HarnessRemediationStore.RemediationFailedState,
                verdict: verdict, safeSummary: "修复合同创建失败：" + HarnessAcceptanceQueue.Safe(ex.Message));
            return new(record.State, "修复合同创建失败，保守 remediation-failed。", taskId, taskDirectory, record);
        }
        var repairTaskId = Path.GetFileName(repairTaskDirectory);
        var repairFingerprint = DeepSeekHarnessRunner.ComputeContractFingerprint(repairTaskDirectory);
        var repairQueued = HarnessRemediationStore.Update(triageRunning,
            helperRepair ? HarnessRemediationStore.HelperRepairQueuedState : HarnessRemediationStore.ProductRepairQueuedState,
            verdict: verdict, repairTaskDirectory: repairTaskDirectory, repairFingerprint: repairFingerprint,
            safeSummary: "已创建修复合同（" + (helperRepair ? "helper" : "product") + "）：" + scope);

        // 串行启动修复 DSH：必须在前序 triage 终态之后（此处即顺序）。
        var repairRunning = HarnessRemediationStore.Update(repairQueued, HarnessRemediationStore.RepairRunningState);
        HarnessRepairRunResult repair;
        try
        {
            var runner = RepairRunner ?? ((root, dir, token) => RunDefaultRepairAsync(root, dir, token));
            repair = await runner(repairProjectRoot, repairTaskDirectory, cancellationToken);
        }
        catch (Exception ex)
        {
            var record = HarnessRemediationStore.Update(repairRunning, HarnessRemediationStore.RemediationFailedState,
                repairTaskDirectory: repairTaskDirectory, repairFingerprint: repairFingerprint,
                safeSummary: "修复 DSH 启动/运行异常：" + HarnessAcceptanceQueue.Safe(ex.Message));
            return new(record.State, "修复 DSH 异常，保守 remediation-failed（不自动第二轮）。", taskId, taskDirectory, record);
        }
        if (!repair.CompletedWithGate)
        {
            var record = HarnessRemediationStore.Update(repairRunning, HarnessRemediationStore.RemediationFailedState,
                repairTaskDirectory: repairTaskDirectory, repairFingerprint: repairFingerprint,
                safeSummary: HarnessAcceptanceQueue.Safe(repair.Message));
            return new(record.State, "修复 DSH 未到真实完成态或报告未过门禁，保守 remediation-failed（不自动第二轮）。",
                taskId, taskDirectory, record);
        }

        // ---- revalidation（一次独立复验，accepted 才终态 accepted） ----
        var revalidationRunning = HarnessRemediationStore.Update(repairRunning, HarnessRemediationStore.RevalidationRunningState,
            repairTaskDirectory: repairTaskDirectory, repairFingerprint: repairFingerprint);
        HarnessRevalidationResult revalidated;
        try
        {
            var runner = RevalidateRunner ?? ((root, dir, fp, token) => RunDefaultRevalidateAsync(root, dir, fp, token));
            revalidated = await runner(repairProjectRoot, repairTaskDirectory, repairFingerprint, cancellationToken);
        }
        catch (CodexAcceptanceException ex)
        {
            var record = HarnessRemediationStore.Update(revalidationRunning, HarnessRemediationStore.NeedsUserState,
                repairTaskDirectory: repairTaskDirectory, repairFingerprint: repairFingerprint,
                safeSummary: "复验基础设施不可用：" + HarnessAcceptanceQueue.Safe(ex.Message));
            return new(record.State, "独立复验基础设施不可用，已保守落 needs-user（不循环额度）。", taskId, taskDirectory, record);
        }
        var revalidatedVerdict = (revalidated.Verdict ?? string.Empty).ToLowerInvariant();
        if (string.Equals(revalidatedVerdict, GptAcceptanceStore.VerdictAccepted, StringComparison.OrdinalIgnoreCase))
        {
            var record = HarnessRemediationStore.Update(revalidationRunning, HarnessRemediationStore.AcceptedState,
                repairTaskDirectory: repairTaskDirectory, repairFingerprint: repairFingerprint,
                safeSummary: "修复合同独立复验通过：" + HarnessAcceptanceQueue.Safe(revalidated.SafeSummary));
            return new(record.State, "修复合同独立复验通过，已终态 accepted。", taskId, taskDirectory, record);
        }
        var failedRecord = HarnessRemediationStore.Update(revalidationRunning, HarnessRemediationStore.RemediationFailedState,
            repairTaskDirectory: repairTaskDirectory, repairFingerprint: repairFingerprint,
            safeSummary: "修复合同独立复验未通过（" + HarnessAcceptanceQueue.Safe(revalidatedVerdict) + "）：" + HarnessAcceptanceQueue.Safe(revalidated.SafeSummary));
        return new(failedRecord.State, "修复后复验未通过，保守 remediation-failed（不再自动开新合同）。", taskId, taskDirectory, failedRecord);
    }

    // ---- triage 默认实现（App Server turn + GPT_REMEDIATION.json） ----

    private async Task<HarnessTriageResult> RunDefaultTriageAsync(HarnessRemediationContext ctx, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TurnTimeout);
        var token = linked.Token;
        var transport = await CreateTransportAsync(TriageTransportFactory, token);
        await using (transport)
        {
            await transport.InitializeAsync(token);
            var threadId = await transport.StartThreadAsync(ctx.ProductProjectRoot, token, CodexTurnPermissions.ReadOnlySandbox);
            var outcome = await transport.RunTurnAsync(threadId, BuildTriageInstruction(ctx), token,
                new CodexTurnPermissions(WritableRoot: ctx.TaskDirectory));
            if (outcome is null || !outcome.Completed)
                throw new CodexAcceptanceException("triage turn 未到可确认终态（" + HarnessAcceptanceQueue.Safe(outcome?.FailureReason) + "），未自动批准任何操作。");
        }
        var ledger = GptRemediationStore.TryReadRecord(ctx.TaskDirectory);
        if (ledger is null)
            throw new CodexAcceptanceException("triage 线程已到真实终态，但任务目录缺少 GPT_REMEDIATION.json，无法确认 verdict（保守失败）。");
        if (!string.Equals(ledger.TaskId, ctx.TaskId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(ctx.ContractFingerprint)
            || !string.Equals(ledger.ContractFingerprint, ctx.ContractFingerprint, StringComparison.Ordinal))
            throw new CodexAcceptanceException("GPT_REMEDIATION.json 的 taskId/fingerprint 与本任务不匹配，无法确认 verdict（保守失败）。");
        return new HarnessTriageResult(ledger.Verdict ?? string.Empty, ledger.SafeSummary, ledger.ScopeSummary);
    }

    private string BuildTriageInstruction(HarnessRemediationContext ctx)
    {
        if (TriageInstructionBuilder is not null) return TriageInstructionBuilder(ctx);
        return string.Join(Environment.NewLine, new[]
        {
            "你是失败修复 triage 执行器（Codex App Server 独立线程，不依赖任何既有会话）。只做一次判读，完成后结束 turn。",
            "产品项目根：" + ctx.ProductProjectRoot,
            "任务目录：" + ctx.TaskDirectory,
            "任务 ID：" + ctx.TaskId,
            "合同指纹：" + ctx.ContractFingerprint,
            "规则：先只读本任务目录的 SPEC.md、HANDOFF.md、manifest.json、HARNESS_STATUS.json、EXECUTION_REPORT.md、WORKER_ACCEPTANCE.md 与 HARNESS_ACCEPTANCE.json（绝不读取 ACCEPTANCE.md）；",
            "独立判断失败类别：是验收基础设施错误、DSH 执行/报告错误，还是产品真实未通过；判定修复是否值得进行。",
            "在可确认时于本任务目录以 UTF-8 无 BOM 原子方式写 GPT_REMEDIATION.json（schema=1、taskId=" + ctx.TaskId + "、fingerprint=" + ctx.ContractFingerprint + "、verdict、safeSummary、scopeSummary、verdictUtc=UTC）。",
            "verdict 只能取：product-repair（产品真实缺陷，可在原项目唯一创建同根因键修复合同）、helper-repair（需要修复 Codex Helper 侧基础设施/工具，需独立根因键合同）、no-repair-needed、remediation-indeterminate、needs-user。",
            "scopeSummary 只描述需修复的最小范围（不复制合同正文）。不得提交、推送、安装、打包、发布、删除、重置或修改除本任务目录 GPT_REMEDIATION.json 外的任何内容。写完后结束 turn。"
        });
    }

    // ---- 默认修复 DSH runner 与复验 runner（第八阶段：默认修复必须受监督并生成可信终态证据） ----

    /// <summary>
    /// 默认修复执行：以 <see cref="HarnessSupervisor"/> 受管形态启动/等待修复 DSH ——
    /// 受管记录承载 PID/启动身份，等待子进程真实退出后按 HARNESS_STATUS + 报告门禁给出终态结论；
    /// 完成时监督记录落 completed 并同步生成 HARNESS_TERMINAL_EVIDENCE.json。
    /// 绝不使用无监督的旧同步执行来作为自动复验前置。
    /// </summary>
    private async Task<HarnessRepairRunResult> RunDefaultRepairAsync(string repairProjectRoot, string repairTaskDirectory, CancellationToken cancellationToken)
    {
        var supervisor = RepairSupervisorFactory?.Invoke() ?? new HarnessSupervisor();
        await supervisor.StartAsync(repairProjectRoot, repairTaskDirectory, cancellationToken);
        // AwaitAsync：attach 受管 PID（存活则等待真实退出）；未完成时继续本地等待，绝不并发验收/重投 DSH。
        var awaited = await supervisor.AwaitAsync(repairProjectRoot, repairTaskDirectory, cancellationToken);
        if (string.Equals(awaited.Outcome, HarnessSupervisor.OutcomeCompleted, StringComparison.OrdinalIgnoreCase))
            return new HarnessRepairRunResult(true, "修复 DSH 受管等待完成：监督终态 completed + 报告门禁通过，可信终态证据已生成。");
        return new HarnessRepairRunResult(false, awaited.Message);
    }

    /// <summary>
    /// 默认复验（第八阶段）：复验前本地确认修复任务的监督记录为 completed、HARNESS_STATUS.json
    /// 非 running、报告门禁通过、HARNESS_TERMINAL_EVIDENCE.json 通过严格 Check；证据缺失/错配/
    /// 监督矛盾时抛 <see cref="CodexAcceptanceException"/>（协调器保守落 needs-user，零 GPT 复验）。
    /// </summary>
    private async Task<HarnessRevalidationResult> RunDefaultRevalidateAsync(string repairProjectRoot, string repairTaskDirectory, string repairFingerprint, CancellationToken cancellationToken)
    {
        var repairTaskId = Path.GetFileName(repairTaskDirectory);

        // 复验前置：监督记录 completed + HARNESS_STATUS 非 running + 报告门禁 + 证据严格一致。
        var supervisor = HarnessSupervisor.TryReadRecord(repairTaskDirectory);
        if (supervisor is null || !supervisor.IsTerminal
            || !string.Equals(supervisor.TerminalOutcome, HarnessSupervisor.OutcomeCompleted, StringComparison.OrdinalIgnoreCase))
            throw new CodexAcceptanceException("修复任务监督记录缺失或未到 completed 终态；复验前置不通过，不启动 GPT 复验（保守 needs-user）。");
        var truth = HarnessAcceptanceCoordinator.TryReadTruth(repairTaskDirectory);
        if (truth is null || truth.IsRunning)
            throw new CodexAcceptanceException("修复任务 HARNESS_STATUS.json 缺失或仍为 running；复验前置不通过，不启动 GPT 复验（保守 needs-user）。");
        var reportGate = HarnessExecutionReportValidator.Validate(repairTaskDirectory, truth.TaskId,
            truth.ContractFingerprint ?? repairFingerprint, truth.StartedUtc);
        if (!reportGate.Valid)
            throw new CodexAcceptanceException("修复任务报告未通过完成门禁（" + reportGate.Reason + "）；复验前置不通过，不启动 GPT 复验（保守 needs-user）。");
        var evidence = HarnessTerminalEvidenceStore.Check(repairTaskDirectory, repairTaskId, repairFingerprint);
        if (!evidence.Valid)
            throw new CodexAcceptanceException("修复任务可信终态证据" + evidence.Reason + "；复验前置不通过，不启动 GPT 复验（保守 needs-user）。");

        var launcher = new CodexAcceptanceLauncher
        {
            TurnTimeout = TurnTimeout,
            CodexPathResolver = CodexPathResolver,
            TransportFactory = RevalidateTransportFactory ?? TriageTransportFactory ?? CreateDefaultTransportFactory()
        };
        var result = await launcher.LaunchAsync(new HarnessAcceptanceLaunchContext(
            repairProjectRoot, repairTaskDirectory, repairTaskId,
            HarnessAcceptanceQueue.RecordPath(repairTaskDirectory), repairFingerprint), cancellationToken);
        return new HarnessRevalidationResult(result.State, result.SafeSummary);
    }

    private async Task<ICodexAppServerTransport> CreateTransportAsync(Func<CancellationToken, Task<ICodexAppServerTransport>>? factory, CancellationToken token)
        => factory is not null ? await factory(token) : (await CreateDefaultTransportFactory()(token));

    private Func<CancellationToken, Task<ICodexAppServerTransport>> CreateDefaultTransportFactory()
        => token =>
        {
            var resolver = CodexPathResolver ?? (() => CodexExecutableResolver.Resolve());
            var executable = resolver();
            ICodexAppServerTransport transport = new CodexAppServerProcessTransport(
                executable ?? throw new CodexAcceptanceException("未找到 codex 可执行文件，无法启动独立 triage/复验（未调用任何模型）。"));
            return Task.FromResult(transport);
        };

    // ---- 记录接回/触发分类/合同创建 ----

    private HarnessRemediationDecision AttachToRecord(HarnessRemediationRecord record, string taskId, string taskDirectory)
    {
        if (record.IsTerminal)
        {
            var note = record.State == HarnessRemediationStore.NeedsUserState
                ? "该任务已有修复终态记录（needs-user），等待人工介入，绝不自动第二轮。"
                : record.State == HarnessRemediationStore.RemediationFailedState
                    ? "该任务已有修复终态记录（remediation-failed），绝不自动第二轮（防额度循环）。"
                    : record.State == HarnessRemediationStore.AcceptedState
                        ? "该任务修复后独立复验已通过（accepted）。"
                        : "该任务已有修复终态记录（" + record.State + "），不自动第二轮。";
            return new(record.State, note, taskId, taskDirectory, record);
        }
        // 运行中：启动器进程存活 → 接回；消失 → 保守 needs-user（防额度/防循环）。
        if (record.LauncherPid is { } pid && record.LauncherStartedUtc is { } started)
        {
            var handle = TryAttachProcess(pid, started);
            if (handle is not null)
            {
                if (handle is IDisposable disposable) { try { disposable.Dispose(); } catch { /* 忽略 */ } }
                return new(OutcomeAlreadyInProgress, "修复编排正在运行（PID " + pid + "，状态 " + record.State + "），未开启第二轮。", taskId, taskDirectory, record);
            }
        }
        var healed = HarnessRemediationStore.Update(record, HarnessRemediationStore.NeedsUserState,
            safeSummary: "修复编排运行中进程已消失（PID " + (record.LauncherPid?.ToString() ?? "未知") + "），round 未完成；不自动第二轮（防额度循环）。");
        return new(healed.State, "检测到修复编排运行中但进程已消失，已保守落 needs-user（不自动第二轮）。", taskId, taskDirectory, healed);
    }

    private HarnessRemediationRecord ClaimAndWriteNeedsUser(string taskId, string projectRoot, string taskDirectory, string? fingerprint, string summary)
    {
        var now = DateTime.UtcNow;
        var claimed = HarnessRemediationStore.TryClaimQueued(new HarnessRemediationRecord(
            HarnessRemediationStore.RecordSchemaVersion, taskId, projectRoot, taskDirectory, fingerprint,
            HarnessRemediationStore.TriageQueuedState, HarnessRemediationStore.MaxRoundsPerTask, now, now,
            LauncherPid: Environment.ProcessId, LauncherStartedUtc: CurrentProcessStartTimeUtc()));
        if (claimed is not null)
            return HarnessRemediationStore.Update(claimed, HarnessRemediationStore.NeedsUserState, safeSummary: summary);
        var raced = HarnessRemediationStore.TryReadRecord(taskDirectory);
        if (raced is not null) return raced;
        now = DateTime.UtcNow;
        return new HarnessRemediationRecord(HarnessRemediationStore.RecordSchemaVersion, taskId, projectRoot,
            taskDirectory, fingerprint, HarnessRemediationStore.NeedsUserState, HarnessRemediationStore.MaxRoundsPerTask,
            now, now, SafeSummary: summary);
    }

    private sealed record FailureSource(string AcceptanceState, string? AcceptanceSafeSummary, string? Fingerprint);

    /// <summary>
    /// 串行门禁核心：原 DSH Runner 是否已证明退出（监督记录 PID+启动时间不存活 / 无监督记录时传统 lease
    /// 不存在）且 HARNESS_STATUS 非 running。证伪返回 true；无法证明返回 false + 可定位原因。
    /// </summary>
    private bool OriginalRunnerExited(string taskDirectory, out string reason)
    {
        var truth = HarnessAcceptanceCoordinator.TryReadTruth(taskDirectory);
        if (truth is null) { reason = "任务真相源（HARNESS_STATUS.json）缺失"; return false; }
        if (truth.IsRunning) { reason = "任务真相仍为 " + truth.State + "（非真实终态）"; return false; }
        var supervisor = HarnessSupervisor.TryReadRecord(taskDirectory);
        if (supervisor is not null)
        {
            if (!supervisor.IsTerminal)
            {
                var handle = AttachRunner is not null
                    ? AttachRunner(supervisor.RunnerPid, supervisor.RunnerStartedUtc)
                    : TryAttachProcess(supervisor.RunnerPid, supervisor.RunnerStartedUtc);
                if (handle is not null)
                {
                    if (handle is IDisposable disposable) { try { disposable.Dispose(); } catch { /* 忽略 */ } }
                    reason = "受管 Runner 仍存活（PID " + supervisor.RunnerPid + "）";
                    return false;
                }
            }
            // 监督记录终态或 PID 已不存在 → 已退出。
        }
        else if (File.Exists(Path.Combine(taskDirectory, DeepSeekHarnessRunner.TaskLeaseFileName)))
        {
            reason = "任务目录存在 Runner 租约（传统受管形态仍存活）";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static FailureSource? ClassifyFailureSource(string taskDirectory)
    {
        var acceptance = HarnessAcceptanceQueue.TryReadRecord(taskDirectory);
        var truth = HarnessAcceptanceCoordinator.TryReadTruth(taskDirectory);
        if (acceptance is not null)
        {
            if (string.Equals(acceptance.QueueState, HarnessAcceptanceQueue.AcceptedState, StringComparison.OrdinalIgnoreCase))
                return null; // 已通过，无需修复
            if (string.Equals(acceptance.QueueState, HarnessAcceptanceQueue.NeedsRepairState, StringComparison.OrdinalIgnoreCase)
                || string.Equals(acceptance.QueueState, HarnessAcceptanceQueue.FailedState, StringComparison.OrdinalIgnoreCase))
                return new FailureSource(acceptance.QueueState, acceptance.SafeSummary, acceptance.ContractFingerprint);
            return null; // queued/running：尚不是可 triage 的失败终态
        }
        // 无自动验收记录：只对明确的 DSH 失败类进入 triage（failed/cancelled/报告门禁失败）。
        if (truth is null || truth.IsRunning) return null;
        var state = (truth.State ?? string.Empty).ToLowerInvariant();
        if (state is "failed" or "cancelled")
            return new FailureSource(state, truth.Message, truth.ContractFingerprint);
        if (state is "completed" or "awaiting-gpt")
        {
            var validation = HarnessExecutionReportValidator.Validate(taskDirectory, truth.TaskId,
                truth.ContractFingerprint ?? string.Empty, truth.StartedUtc);
            if (!validation.Valid)
                return new FailureSource("report-gate-failed", validation.Reason, truth.ContractFingerprint);
        }
        return null;
    }

    /// <summary>验收基础设施不可用特征（脱敏原因关键词；命中则不消耗 triage 额度，直接 needs-user）。</summary>
    private static bool LooksLikeInfrastructureFailure(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return false;
        string[] markers =
        [
            "未找到 codex 可执行", "codex app-server", "initialize", "thread/start",
            "result.thread.id", "schema 不匹配", "进程提前退出", "未启动", "握手"
        ];
        return markers.Any(marker => summary.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadRootCauseKey(string taskDirectory)
    {
        var path = Path.Combine(taskDirectory, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
            var key = node?["rootCauseKey"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch { return null; }
    }

    /// <summary>唯一创建符合规范的修复合同（SPEC/ACCEPTANCE/HANDOFF/manifest；同/独立根因键；UTF-8 无 BOM）。</summary>
    private static string CreateRepairContract(string repairProjectRoot, HarnessRemediationContext ctx,
        string phase, string rootCauseKey, string scope)
    {
        var runsRoot = Path.Combine(Path.GetFullPath(repairProjectRoot), ".codex-helper", "runs");
        Directory.CreateDirectory(runsRoot);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var dirName = "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8];
            var repairDir = Path.Combine(runsRoot, dirName);
            if (Directory.Exists(repairDir)) continue;
            Directory.CreateDirectory(repairDir);
            var taskId = Path.GetFileName(repairDir);
            var now = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            AtomicFile.WriteAllText(Path.Combine(repairDir, "SPEC.md"),
                "# 修复合同（自动生成，可审计，阶段四）\n\n- 根因键：" + rootCauseKey + "\n- 原任务：" + ctx.TaskId + "\n- 修复范围（triage 证实，最小）：" + scope + "\n- 要求：仅修复上述范围；按项目现有约定运行 workerChecks；完成后在任务目录写 EXECUTION_REPORT.md（含任务 ID、合同指纹、退出码 0、修改文件、workerChecks、风险/未完成项）。\n- 禁止：提交/推送/安装/打包/发布/删除/重置；不改版本号/安装器。\n");
            AtomicFile.WriteAllText(Path.Combine(repairDir, "HANDOFF.md"),
                "# 交接\n\n仅按 SPEC 修复范围工作，先读本目录 SPEC/HANDOFF/manifest 与 WORKER_ACCEPTANCE.md（如存在）；绝不读取 ACCEPTANCE.md。\n");
            AtomicFile.WriteAllText(Path.Combine(repairDir, "ACCEPTANCE.md"),
                "# 验收\n\n独立验收：先阅读任务目录 SPEC/HANDOFF/manifest 与状态/报告，检查实际改动并运行聚焦 gptChecks；通过后写 GPT_ACCEPTANCE.json。\n");
            var manifest = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["taskId"] = taskId,
                ["projectRoot"] = Path.GetFullPath(repairProjectRoot),
                ["rootCauseKey"] = rootCauseKey,
                ["phase"] = phase,
                ["createdUtc"] = now,
                ["executor"] = "deepseek-harness",
                ["allowNetwork"] = false,
                ["allowInstall"] = false,
                ["allowRelease"] = false
            };
            AtomicFile.WriteAllText(Path.Combine(repairDir, "manifest.json"), manifest.ToJsonString());
            return repairDir;
        }
        throw new InvalidOperationException("无法在 " + runsRoot + " 内创建唯一修复合同目录。");
    }

    private static IHarnessSupervisedProcess? TryAttachProcess(int pid, DateTime startedUtc)
    {
        if (pid <= 0) return null;
        Process? process = null;
        try { process = Process.GetProcessById(pid); }
        catch { return null; }
        DateTime osStartedUtc;
        try { osStartedUtc = process.StartTime.ToUniversalTime(); }
        catch { process.Dispose(); return null; }
        if ((osStartedUtc - startedUtc).Duration() > TimeSpan.FromSeconds(2))
        {
            process.Dispose();
            return null;
        }
        if (process.HasExited)
        {
            process.Dispose();
            return null;
        }
        return new RepairProcessHandle(process);
    }

    private static DateTime CurrentProcessStartTimeUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>修复子进程真实受控句柄。</summary>
    private sealed class RepairProcessHandle : IHarnessSupervisedProcess
    {
        private readonly Process process;

        internal RepairProcessHandle(Process process)
        {
            this.process = process;
            try { StartTimeUtc = process.StartTime.ToUniversalTime(); }
            catch { StartTimeUtc = DateTime.UtcNow; }
        }

        public int Id => process.Id;
        public DateTime StartTimeUtc { get; }
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    }
}
