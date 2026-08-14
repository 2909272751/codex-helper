namespace CodexHelper.Core.Services;

/// <summary>
/// Harness 协作一致性诊断结果：设置模式、AGENTS 指导块、Harness skill/脚本、Runner 可找到性
/// 与 Reasonix 状态互斥的只读快照。Consistent 表示当前文件状态与设置模式一致：
/// Harness 模式要求指导块、SKILL.md、invoke-harness.ps1、Runner 四项齐全且 Reasonix 未启用；
/// 其他模式要求无任何 Harness 规则残留。
/// </summary>
public sealed record HarnessConsistencyDiagnostic(
    string SettingsMode,
    bool GuidancePresent,
    bool SkillPresent,
    bool ScriptPresent,
    bool RunnerFound,
    bool ReasonixEnabled,
    bool Consistent);
