using System.Text;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// 协作执行器路由：根据当前执行器选择（Off/Reasonix/Harness）生成或移除 Helper 管理的
/// Codex 全局指导与 executor skill。关闭协作时移除 Helper 管理的协作规则；
/// Reasonix 模式维持现有三档策略；Harness 模式使用同样的 GPT 规划/验收边界，但调用独立 Harness runner。
/// 同一时刻至多一个执行器的协作规则生效（互斥）。
/// </summary>
public sealed class CollaborationService
{
    public const string HarnessGuidanceStart = "<!-- CODEX-HELPER-HARNESS-EXECUTOR-START -->";
    public const string HarnessGuidanceEnd = "<!-- CODEX-HELPER-HARNESS-EXECUTOR-END -->";

    private readonly string codexRoot;
    private readonly AppPaths paths;
    private readonly string skillDirectory;
    private readonly ReasonixIntegrationService reasonix;

    public CollaborationService(string codexRoot, AppPaths paths)
    {
        this.codexRoot = Path.GetFullPath(codexRoot);
        this.paths = paths;
        skillDirectory = Path.Combine(this.codexRoot, "skills", "harness-executor");
        reasonix = new ReasonixIntegrationService(this.codexRoot, paths);
    }

    /// <summary>是否已启用 Harness 协作（skill 与全局指导都在）。</summary>
    public bool IsHarnessEnabled()
        => File.Exists(Path.Combine(skillDirectory, "SKILL.md")) && HasHarnessGuidance();

    /// <summary>
    /// 按持久化的执行器选择同步协作规则，保证互斥：
    /// Off → 移除 Harness 规则并关闭 Reasonix；Reasonix → 移除 Harness 规则（Reasonix 规则由既有按钮管理）；
    /// Harness → 关闭 Reasonix 并写入 Harness 规则。绝不因未知/异常值开启任何执行器。
    /// </summary>
    public void Synchronize(AppSettings settings)
    {
        var mode = CollaborationModeExtensions.ParseCollaborationMode(settings.CollaborationMode);
        switch (mode)
        {
            case CollaborationMode.Off:
                if (reasonix.IsEnabled()) reasonix.Disable();
                RemoveHarness();
                break;
            case CollaborationMode.Reasonix:
                RemoveHarness();
                break;
            case CollaborationMode.Harness:
                if (reasonix.IsEnabled()) reasonix.Disable();
                WriteHarness();
                break;
            default:
                RemoveHarness();
                break;
        }
    }

    public void RemoveHarness()
    {
        RemoveHarnessGuidance();
        if (Directory.Exists(skillDirectory)) Directory.Delete(skillDirectory, recursive: true);
    }

    private void WriteHarness()
    {
        Directory.CreateDirectory(skillDirectory);
        AtomicFile.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), BuildHarnessSkill());
        WriteHarnessGuidance();
    }

    private bool HasHarnessGuidance()
    {
        var path = Path.Combine(codexRoot, "AGENTS.md");
        return File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Contains(HarnessGuidanceStart, StringComparison.Ordinal);
    }

    private void WriteHarnessGuidance()
    {
        Directory.CreateDirectory(codexRoot);
        var path = Path.Combine(codexRoot, "AGENTS.md");
        var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        existing = RemoveMarkedBlock(existing, HarnessGuidanceStart, HarnessGuidanceEnd).TrimEnd();
        var block = $$"""
{{HarnessGuidanceStart}}
For implementation tasks that change project files, GPT is the planner and judge. This device uses the DeepSeek Harness (developer preview) as the implementation executor. GPT keeps the same planning/acceptance boundary as Reasonix: GPT plans, Harness implements, GPT independently re-runs focused acceptance. Route tasks through the managed `harness-executor` skill runner with only the absolute project root and the unique task directory; the task body is read only from files (SPEC.md/HANDOFF.md/manifest.json), never passed on the command line. Do not automate task submission by clicking the Harness Web UI. If runtime capability probing cannot confirm task submission, event stream and cancellation, honestly report "Web available but automatic relay unavailable" instead of claiming real-time collaboration or falling back to invisible headless. Only listen on 127.0.0.1; closing the browser does not stop the task. Credentials, API keys and task bodies must never appear on the command line; pass credentials only via controlled environment variables / existing secure storage; redact logs. GPT owns visual acceptance and gptChecks/releaseChecks; the Harness runner performs implementation and workerChecks only. Harness version 0.1.0-rc.5 is only the known compatibility baseline: newer valid semantic versions may be used when runtime capability probing passes; never silently use the literal `latest`. Node requirement is >=22.19.0 (LTS) or >=24.0.0.
{{ReasonixIntegrationService.VisualBoundaryRule}}
{{HarnessGuidanceEnd}}
""";
        AtomicFile.WriteAllText(path, string.IsNullOrWhiteSpace(existing) ? block + Environment.NewLine : existing + Environment.NewLine + Environment.NewLine + block + Environment.NewLine);
    }

    private void RemoveHarnessGuidance()
    {
        var path = Path.Combine(codexRoot, "AGENTS.md");
        if (!File.Exists(path)) return;
        var existing = RemoveMarkedBlock(File.ReadAllText(path, Encoding.UTF8), HarnessGuidanceStart, HarnessGuidanceEnd).TrimEnd();
        if (string.IsNullOrWhiteSpace(existing)) File.Delete(path);
        else AtomicFile.WriteAllText(path, existing + Environment.NewLine);
    }

    private static string RemoveMarkedBlock(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0) return text;
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        if (endIndex < 0) return text[..startIndex];
        return text.Remove(startIndex, endIndex + end.Length - startIndex);
    }

    private static string BuildHarnessSkill() => $$"""
---
name: harness-executor
description: Execute an already planned implementation task through the managed DeepSeek Harness runner. Use only after GPT has written SPEC.md, ACCEPTANCE.md, HANDOFF.md and manifest.json in a unique project-local run directory.
---

# DeepSeek Harness Executor

GPT remains planner and judge. This skill only launches the DeepSeek Harness (developer preview) as the implementation hand.

Use the Helper-managed Harness runner only after runtime capability probing has confirmed task submission, event streaming, and cancellation for the active Web Host. If those capabilities are not confirmed, stop and report "Web available but automatic relay unavailable"; do not claim that a task or session was created, do not fall back to invisible headless, and do not resubmit by clicking the Web UI. The task body is read only from the task directory files — never pass it on the command line. After a confirmed managed runner completes, inspect the actual changed files and independently re-run only the focused acceptance checks affected by the changes (incremental acceptance; GPT owns visual acceptance and gptChecks/releaseChecks). Do not commit, push, reset, clean, install dependencies, package, or release unless explicitly authorized. Credentials/API keys/task bodies must never appear on the command line; redact logs. Version 0.1.0-rc.5 is a known compatibility baseline, not a hard pin; newer semantic versions are allowed only when capability probing passes, and the literal `latest` is never accepted as an installed version.
{{ReasonixIntegrationService.VisualBoundaryRule}}
""";
}
