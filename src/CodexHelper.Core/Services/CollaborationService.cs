using System.Text;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// 协作执行器路由：根据当前执行器选择（Off/Reasonix/Harness）生成或移除 Helper 管理的
/// Codex 全局指导与 executor skill。关闭协作时移除 Helper 管理的协作规则；
/// Reasonix 模式维持现有三档策略；Harness 模式使用同样的 GPT 规划/验收边界，但调用独立 Harness runner。
/// 同一时刻至多一个执行器的协作规则生效（互斥）。
/// Harness 启用时同时生成托管调用脚本 invoke-harness.ps1（只接收两个绝对路径，自动寻找
/// 已安装或开发输出中的 Runner，任务正文绝不进入命令行）。
/// </summary>
public sealed class CollaborationService
{
    public const string HarnessGuidanceStart = "<!-- CODEX-HELPER-HARNESS-EXECUTOR-START -->";
    public const string HarnessGuidanceEnd = "<!-- CODEX-HELPER-HARNESS-EXECUTOR-END -->";

    /// <summary>
    /// 视觉验收职责边界（Harness 版）：实现执行器/Harness 禁止截图、禁止查看图片、禁止做视觉结论，
    /// 视觉验收归 GPT。修正旧文案把边界错误归属给 Reasonix 的问题。
    /// </summary>
    public const string HarnessVisualBoundaryRule =
        "实现执行器/Harness 禁止截图、禁止查看图片、禁止做视觉结论（不得桌面截图、PrintWindow、BitBlt、RenderTargetBitmap、离屏渲染捕获或像素分析，也不得换用其他截屏方式）。所有截图、DPI、布局、颜色、遮挡与视觉验收都归 GPT；若 GPT 缺少图像工具，必须如实标记“视觉未验证”，而不是把任务退回执行器反复尝试。GUI 烟测至多运行一次；环境不允许就记录事实并继续，绝不做图形环境诊断。若 workerChecks 错误地包含截图或视觉项，跳过并交给 GPT。";

    private readonly string codexRoot;
    private readonly AppPaths paths;
    private readonly string skillDirectory;
    private readonly ReasonixIntegrationService reasonix;
    private readonly HarnessRunnerLocator runnerLocator;

    public CollaborationService(string codexRoot, AppPaths paths, HarnessRunnerLocator? runnerLocator = null)
    {
        this.codexRoot = Path.GetFullPath(codexRoot);
        this.paths = paths;
        skillDirectory = Path.Combine(this.codexRoot, "skills", "harness-executor");
        reasonix = new ReasonixIntegrationService(this.codexRoot, paths);
        this.runnerLocator = runnerLocator ?? new HarnessRunnerLocator();
    }

    /// <summary>
    /// 是否已启用 Harness 协作：指导块、SKILL.md、invoke-harness.ps1 与可找到的 Runner 四项齐全才算启用，
    /// 缺任何一项都返回未启用，绝不假成功。
    /// </summary>
    public bool IsHarnessEnabled()
        => File.Exists(Path.Combine(skillDirectory, "SKILL.md"))
        && File.Exists(Path.Combine(skillDirectory, HarnessInvokeScriptName))
        && HasHarnessGuidance()
        && runnerLocator.FindRunner() is not null;

    /// <summary>
    /// 按持久化的执行器选择同步协作规则，保证互斥：
    /// Off → 移除 Harness 规则并关闭 Reasonix；Reasonix → 移除 Harness 规则（Reasonix 规则由既有按钮管理）；
    /// Harness → 关闭 Reasonix 并写入 Harness 规则。绝不因未知/异常值开启任何执行器。
    /// 同步是幂等的：重复调用产生相同内容、无副作用累积。
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

    /// <summary>一致性诊断：设置模式、AGENTS 指导块、Harness skill/脚本、Runner 可找到性与 Reasonix 状态互斥。</summary>
    public HarnessConsistencyDiagnostic Diagnose(AppSettings settings)
    {
        var mode = CollaborationModeExtensions.ParseCollaborationMode(settings.CollaborationMode);
        var guidancePresent = HasHarnessGuidance();
        var skillPresent = File.Exists(Path.Combine(skillDirectory, "SKILL.md"));
        var scriptPresent = File.Exists(Path.Combine(skillDirectory, HarnessInvokeScriptName));
        var runnerFound = runnerLocator.FindRunner() is not null;
        var reasonixEnabled = reasonix.IsEnabled();
        // 一致性：Harness 模式要求四项齐全且 Reasonix 关闭（互斥）；其他模式要求无 Harness 残留。
        var consistent = mode == CollaborationMode.Harness
            ? guidancePresent && skillPresent && scriptPresent && runnerFound && !reasonixEnabled
            : !guidancePresent && !skillPresent && !scriptPresent;
        return new HarnessConsistencyDiagnostic(mode.ToPersisted(), guidancePresent, skillPresent, scriptPresent, runnerFound, reasonixEnabled, consistent);
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
        WriteManagedPowerShell(Path.Combine(skillDirectory, HarnessInvokeScriptName), BuildHarnessInvokeScript());
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
{{HarnessVisualBoundaryRule}}
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

    /// <summary>托管脚本文件名（与 invoke-reasonix.ps1 同模式）。</summary>
    private const string HarnessInvokeScriptName = "invoke-harness.ps1";

    /// <summary>
    /// 托管 PowerShell 脚本以 UTF-8 BOM 写入，确保 Windows PowerShell 5.1 正确按 UTF-8 解码
    /// 其中的中文（无 BOM 会按系统代码页误读并破坏语法）。
    /// </summary>
    private static void WriteManagedPowerShell(string path, string content)
    {
        var preamble = new UTF8Encoding(true).GetPreamble();
        var body = Encoding.UTF8.GetBytes(content);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// invoke-harness.ps1：只接收绝对 ProjectRoot 与 TaskDirectory 两个参数，校验后自动寻找
    /// 已安装或开发输出中的 CodexHelper.HarnessRunner.exe 并调用，透传退出码（0=完成/1=失败/2=取消/3=参数错误）。
    /// 任务正文绝不进入命令行（脚本只转发两个路径；任务正文由 Runner 从任务目录文件读取）。
    /// </summary>
    private static string BuildHarnessInvokeScript() => """
param(
    [Parameter(Mandatory=$true)][string]$ProjectRoot,
    [Parameter(Mandatory=$true)][string]$TaskDirectory
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
$task = [IO.Path]::GetFullPath($TaskDirectory).TrimEnd('\')
if (-not ($task.StartsWith($project + '\', [StringComparison]::OrdinalIgnoreCase))) { throw '任务目录必须位于项目根目录内。' }
if (-not [IO.File]::Exists((Join-Path $task 'SPEC.md'))) { throw '任务目录缺少 SPEC.md，任务正文只从文件读取。' }

function Find-HarnessRunner {
    $candidates = @()
    # 1) 与脚本同目录（脚本被放入安装目录时）。
    $candidates += Join-Path $PSScriptRoot 'CodexHelper.HarnessRunner.exe'
    # 2) Helper 标准安装目录。
    $candidates += Join-Path $env:LOCALAPPDATA 'Programs\Codex Helper\CodexHelper.HarnessRunner.exe'
    # 3) 开发输出：从脚本位置向上找仓库根（CodexHelper.sln），检查 Release/Debug 输出。
    $dir = $PSScriptRoot
    for ($i = 0; $i -lt 8; $i++) {
        if ([IO.File]::Exists((Join-Path $dir 'CodexHelper.sln'))) {
            foreach ($config in @('Release', 'Debug')) {
                $candidates += Join-Path $dir ("src\CodexHelper.HarnessRunner\bin\" + $config + "\net8.0-windows\CodexHelper.HarnessRunner.exe")
            }
            break
        }
        $parent = Split-Path $dir -Parent
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    foreach ($candidate in $candidates) {
        if ([IO.File]::Exists($candidate)) { return $candidate }
    }
    throw '未找到 CodexHelper.HarnessRunner.exe（已安装目录或开发输出）。请先构建项目或重新安装 Codex Helper。'
}

$runner = Find-HarnessRunner
# 只转发两个绝对路径；任务正文由 Runner 从任务目录文件读取，绝不进入命令行。
& $runner -ProjectRoot $project -TaskDirectory $task
exit $LASTEXITCODE
""";

    /// <summary>
    /// SKILL.md：任务合同与进度说明优先简体中文，给出明确调用命令（只传两个绝对路径、
    /// 等待真实终态与退出码语义），并把视觉边界改为“实现执行器/Harness 禁止截图”。
    /// </summary>
    private static string BuildHarnessSkill() => $$"""
---
name: harness-executor
description: 通过 Helper 托管的 DeepSeek Harness Runner 执行已规划的合同任务。仅在 GPT 已把 SPEC.md、ACCEPTANCE.md、HANDOFF.md 与 manifest.json 写入项目内唯一运行目录后使用。
---

# DeepSeek Harness 执行器

GPT 负责规划与验收，本 skill 只负责把合同任务交给 Helper 托管的 Harness Runner 执行，并在同一调用中等待真实终态。

## 调用命令

powershell.exe -NoProfile -ExecutionPolicy Bypass -File invoke-harness.ps1 -ProjectRoot <绝对项目根目录> -TaskDirectory <绝对任务目录>

只传两个绝对路径参数；任务正文只从任务目录内的合同文件读取（SPEC.md、HANDOFF.md、manifest.json），绝不进入命令行。运行后等待命令返回真实终态：0=完成、1=失败、2=取消、3=参数错误；不得轮询日志、不得重复提交、不得绕过托管 Runner 用其他方式提交。执行期间可在 Codex Helper 的 Harness 任务中心查看进度。

## 任务合同与进度

- 任务合同（SPEC.md）与交接说明（HANDOFF.md）位于任务目录内；合同与进度说明优先使用简体中文。
- 只读取 SPEC.md、HANDOFF.md、manifest.json（绝不读取 ACCEPTANCE.md；视觉验收由 GPT 独立进行）。
- 完成后检查实际改动文件，并只重跑受影响的聚焦验收检查（增量验收）；高风险、发布、安全或合同强制项必须跑完整回归。
- GPT 负责视觉验收与 gptChecks/releaseChecks；执行器只做实现与 workerChecks。
- 不提交、不推送、不重置、不清理、不安装依赖、不打包发布，除非合同明确授权。
- 凭据、API Key 与任务正文绝不进入命令行；日志脱敏。
- Harness 版本 0.1.0-rc.5 仅为已知兼容基线：通过运行时能力探测的新语义版本可用，绝不静默使用 latest。
{{HarnessVisualBoundaryRule}}
""";
}
