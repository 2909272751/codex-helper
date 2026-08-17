using System.Text;
using System.Text.Json;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>合同启动前体检结果（Harness 版）：是否阻止、中文原因、诊断、是否归一化、去重+职责过滤后的 workerChecks 与移交 GPT 项。</summary>
public sealed record HarnessContractHealthResult(
    bool Blocked,
    string? BlockReason,
    IReadOnlyList<string> Diagnostics,
    bool Normalized,
    IReadOnlyList<string> WorkerChecks,
    IReadOnlyList<string> DelegatedToGpt);

/// <summary>
/// Harness 合同启动前体检与安全归一化（与 Reasonix 同等级，纯文件系统+纯函数，供 Runner 与实际执行共用）：
/// <list type="bullet">
/// <item>校验四份合同文件：SPEC.md（缺失即阻止）、HANDOFF.md / manifest.json（缺失诊断，不阻断）；</item>
/// <item>复用 <see cref="ReasonixContractHealth.Inspect"/> 的既有规则：HANDOFF 要求读 ACCEPTANCE / 写 REVIEW_PACKET 归一化、
/// 要求截图视觉交付则阻止（视觉验收归 GPT，无法安全修正）、workerChecks 去重（保留首个）、HANDOFF 缺允许读取/修改/直接依赖诊断；</item>
/// <item>workerChecks 中视觉/GUI/release 打包/发布职责经 <see cref="ReasonixAcceptanceFilter"/> 移出 worker 范围交给 GPT；</item>
/// <item>把归一化后的 WORKER_ACCEPTANCE.md 原子写入任务目录（UTF-8 无 BOM），供实现执行器只读该派生合同；</item>
/// <item>无法安全归一化时返回 Blocked=true 与中文原因，Runner 写 failed 终态并阻止提交/建会话。</item>
/// </list>
/// </summary>
public static class HarnessContractHealth
{
    /// <summary>任务目录内派生的执行器验收合同文件名。</summary>
    public const string WorkerAcceptanceFileName = "WORKER_ACCEPTANCE.md";

    /// <summary>执行四份合同文件的体检与安全归一化（纯读，不写文件）。</summary>
    public static HarnessContractHealthResult Inspect(string taskDirectory)
    {
        var diagnostics = new List<string>();

        var specPath = Path.Combine(taskDirectory, "SPEC.md");
        if (!File.Exists(specPath))
            return new(true, "任务目录缺少 SPEC.md，无法校验合同与实施范围。", diagnostics, false, [], []);

        var handoffPath = Path.Combine(taskDirectory, "HANDOFF.md");
        var manifestPath = Path.Combine(taskDirectory, "manifest.json");
        var hasHandoff = File.Exists(handoffPath);
        var hasManifest = File.Exists(manifestPath);
        if (!hasHandoff)
            diagnostics.Add("任务目录缺少 HANDOFF.md；已保留原合同，执行时以 SPEC/manifest 为准，但不建议在缺少允许读取/允许修改/直接依赖范围时提交。");
        if (!hasManifest)
            diagnostics.Add("任务目录缺少 manifest.json；workerChecks 视为空（按默认检查规则：无显式 workerChecks 时只做受影响项目 Release build 一次）。");

        var handoffText = hasHandoff ? SafeRead(handoffPath) : string.Empty;
        ReasonixManifestPolicy? policy = null;
        IReadOnlyList<string>? rawChecks = null;
        if (hasManifest)
        {
            try
            {
                using var doc = JsonDocument.Parse(SafeRead(manifestPath));
                policy = ReasonixManifestPolicy.FromManifest(doc.RootElement);
                rawChecks = policy.WorkerChecks;
            }
            catch
            {
                diagnostics.Add("manifest.json 解析失败；workerChecks 视为空（按默认检查规则执行）。");
            }
        }

        // 复用 Reasonix 体检规则（读 ACCEPTANCE/写 REVIEW_PACKET 归一化、截图视觉交付阻止、去重、profile、HANDOFF 缺范围）。
        var baseHealth = ReasonixContractHealth.Inspect(policy, handoffText, rawChecks, deepSeek: false);
        diagnostics.AddRange(baseHealth.Diagnostics);
        if (baseHealth.Blocked)
            return new(true, baseHealth.BlockReason, diagnostics, baseHealth.Normalized, [], []);

        // workerChecks 职责过滤：视觉/GUI 或 release 打包/发布项移交 GPT，普通 build/test/source inspection 保留。
        var (worker, delegated) = ReasonixAcceptanceFilter.Partition(baseHealth.DeduplicatedChecks);
        var normalized = baseHealth.Normalized || delegated.Count > 0;
        return new(false, null, diagnostics, normalized, worker, delegated);
    }

    /// <summary>
    /// 派生/更新任务目录内 WORKER_ACCEPTANCE.md（UTF-8 无 BOM 原子写）。内容只含归一化后的 workerChecks 与执行规则，
    /// 视觉/GPT 项不在此披露（实现执行器不截图、不看图、不作视觉结论，也不打包/发布）。
    /// </summary>
    public static void WriteWorkerAcceptance(string taskDirectory, HarnessContractHealthResult health)
    {
        var lines = new List<string>
        {
            "# WORKER_ACCEPTANCE.md",
            string.Empty,
            "此文件由 Codex Helper 执行器从 manifest.json 的 workerChecks 运行时派生生成，不是原始完整验收账本；完整 GPT 与发布验收仍归 GPT 独立完成。",
            string.Empty,
            "## workerChecks（实现执行器需完成）"
        };
        if (health.WorkerChecks.Count > 0)
        {
            foreach (var check in health.WorkerChecks)
                lines.Add("- " + check);
        }
        else
        {
            lines.Add("- 无显式 workerChecks。按通用自动可验证 worker 规则完成：运行项目现有完整测试套件一次并执行 Release 配置构建一次，均须成功；不得尝试任何视觉/GUI/发布工作。");
        }
        if (health.DelegatedToGpt.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## 已移交 GPT（不属于实现执行器）");
            lines.Add($"- 共 {health.DelegatedToGpt.Count} 项检查被判定为 GPT 视觉/GUI 或 release 打包/发布职责，已整体移交给 GPT 独立验收。其正文不在此披露；实现执行器不得尝试执行其中任何一项。");
        }
        lines.Add(string.Empty);
        lines.Add("## 执行规则");
        lines.Add("- 每个 workerCheck 最多运行一次；已通过的不重跑；不迭代测试/Release 构建来回。");
        lines.Add("- 方案已冻结：最多一次性列出不超过 5 项简短实施动作，然后直接实施；禁止重新规划，禁止读取 ACCEPTANCE.md。");
        lines.Add("- 每个 workerCheck 前后以 UTF-8 无 BOM 原子更新任务目录内 PROGRESS.json（stage/summary/updatedUtc/completedChecks/totalChecks/currentCheck/checks，检查状态 pending/running/passed/failed）；不得把推理 token 数当作完成进度。");
        lines.Add("- 不提交、不推送、不打包、不发布、不安装；不得创建实验项目；不运行 publish/package/build-release。");
        lines.Add("- 视觉/GUI/发布验收全部归 GPT；实现执行器不截图、不看图、不作视觉结论。");
        lines.Add(string.Empty);
        lines.Add("## 报告要求");
        lines.Add("- 将实施结果写入 EXECUTION_REPORT.md；不写 REVIEW_PACKET.md（由 Helper 自动生成）。");

        var body = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        AtomicFile.WriteAllText(Path.Combine(taskDirectory, WorkerAcceptanceFileName), body);
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch { return string.Empty; }
    }
}