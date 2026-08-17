using System.Text;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// Harness 合同模式、权限和执行强度的单一事实源：持久化字符串归一化、显示文案、
/// 模式→agentPreset 映射与强度策略。所有解析都容错：空/非法值回退安全默认
/// （模式 codex-contract、权限 danger-full-access、强度 standard），绝不因旧设置或
/// 损坏值误开启其他行为。执行强度是 Helper 的合同/检查预算，不伪装成模型思考强度。
/// </summary>
public static class HarnessExecutionOptions
{
    public const string DefaultMode = "codex-contract";
    public const string DefaultPermission = "danger-full-access";
    public const string DefaultStrength = "standard";

    // ---- 执行模式 ----
    public static string NormalizeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "standard" => "standard",
        "minimal" => "minimal",
        "plan" => "plan",
        "codex-contract" or "codex_contract" or "codexcontract" => "codex-contract",
        _ => DefaultMode
    };

    public static string DescribeMode(string? mode) => NormalizeMode(mode) switch
    {
        "standard" => "使用 DSH 原生 standard 预设，合同提示与默认一致。",
        "minimal" => "使用 DSH 原生 minimal 预设，仅提供持久 bash 与 str_replace_editor 双工具。",
        "plan" => "只输出实施计划，不修改项目文件；合同提示明确禁止实施与写入。",
        _ => "中文进度、直接实施、只做 workerChecks、结构化 EXECUTION_REPORT；使用 Helper 托管的 codex-contract 预设，不支持时降级 standard。"
    };

    /// <summary>
    /// 模式 → DSH agentPreset 映射。codex-contract 返回 standard 作为默认请求值：
    /// 只有 Helper 托管的 codex-contract 预设确认安装后，调用方才覆盖为
    /// <see cref="HarnessContractProfileService.PresetId"/>；否则诚实使用 standard，
    /// 绝不向 Host 请求不存在的预设。
    /// </summary>
    public static string AgentPreset(string? mode) => NormalizeMode(mode) switch
    {
        "minimal" => "minimal",
        "plan" => "standard",
        _ => "standard"
    };

    // ---- 权限模式 ----
    public static string NormalizePermission(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "read-only" or "readonly" => "read-only",
        "workspace-write" or "workspacewrite" => "workspace-write",
        "danger-full-access" or "danger_full_access" => "danger-full-access",
        _ => DefaultPermission
    };

    public static string DescribePermission(string? permission) => NormalizePermission(permission) switch
    {
        "read-only" => "执行器只能读取，不得写入或执行修改命令。",
        "workspace-write" => "执行器只允许在项目工作区内写入。",
        _ => "执行器可执行任意命令并写入任意路径；仅在你信任任务合同时使用。"
    };

    /// <summary>权限 → Helper 托管 Web Host 进程环境变量值（DSH_PERMISSION_MODE）。</summary>
    public static string PermissionEnvironmentValue(string? permission) => NormalizePermission(permission);

    /// <summary>权限 → 审批策略环境变量值（DSH_APPROVAL_POLICY）：danger-full-access 配合 never，其余保持 ask。</summary>
    public static string ApprovalEnvironmentValue(string? permission)
        => NormalizePermission(permission) == "danger-full-access" ? "never" : "ask";

    // ---- 执行强度 ----
    public static string NormalizeStrength(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "quick" => "quick",
        "deep" => "deep",
        "standard" => "standard",
        _ => DefaultStrength
    };

    /// <summary>强度 → 合同提示的检查预算与收敛策略段落。绝不伪装成模型思考强度。</summary>
    public static string StrengthInstruction(string? strength) => NormalizeStrength(strength) switch
    {
        "quick" => "检查预算为 quick：只重跑受本次改动影响的聚焦检查，不扩大范围；同一失败原因连续出现 2 次时立即停止，写入明确失败原因，等待 GPT 接管，禁止反复重试覆盖文件。",
        "deep" => "检查预算为 deep：重跑受影响的聚焦检查，并对合同强制项（高风险、发布、安全）执行完整回归；同一失败原因连续出现 4 次时立即停止，写入明确失败原因，等待 GPT 接管，禁止反复重试覆盖文件。",
        _ => "检查预算为 standard：按 manifest.json 的 workerChecks 逐项执行，受影响范围不明时做增量验收；同一失败原因连续出现 3 次时立即停止，写入明确失败原因，等待 GPT 接管，禁止反复重试覆盖文件。"
    };

    /// <summary>模型思考强度：DSH 当前没有公开参数，诚实显示"自动，由模型决定"。</summary>
    public const string ModelReasoningText = "自动，由模型决定";

    /// <summary>把设置恢复为推荐值（权限必须回到 danger-full-access，用户明确要求）。</summary>
    public static void RestoreRecommended(AppSettings settings)
    {
        settings.HarnessExecutionMode = DefaultMode;
        settings.HarnessPermissionMode = DefaultPermission;
        settings.HarnessExecutionStrength = DefaultStrength;
        settings.HarnessReuseSession = true;
        settings.HarnessAutoStartHost = true;
        settings.HarnessReturnToGptOnFailure = true;
    }

    /// <summary>
    /// manifest.json 没有显式 workerChecks 时的检查预算规则：只对受影响项目做一次 Release build，
    /// 不默认完整测试套件；无法确定受影响项目时在报告中交给 GPT，绝不递归扫描整个仓库寻找测试入口。
    /// 与 quick/standard/deep 强度无关，是 workerChecks 缺失时的统一默认行为。
    /// </summary>
    public static string DefaultWorkerChecksInstruction()
        => "manifest.json 有显式 workerChecks 时只能逐项执行该列表，每项最多一次；严禁追加任何未列出的构建、测试、检查或自查。manifest.json 没有显式 workerChecks 时：只对受影响项目做一次 Release build，不自动运行完整测试套件；无法确定受影响项目时在报告中明确交给 GPT 验收，绝不递归扫描整个仓库寻找测试入口。";
}

/// <summary>
/// Codex 合同模式 agent preset 管理器：在用户 DSH Home（$DSH_HOME，默认 ~/.dsh）的
/// <c>.agent-presets/codex-contract/</c> 下幂等生成 Helper 专属 preset，不修改用户已有
/// profile、不覆盖凭据或默认模型。生成内容基于 DSH 官方 standard preset 的完整副本，
/// 只替换 persona 段落为合同模式规则；结构不兼容时拒绝生成并诚实降级到 standard。
/// 生成完全幂等：重复调用产生逐字节相同内容。
/// </summary>
public sealed class HarnessContractProfileService
{
    /// <summary>Helper 专属 preset id（也是目录名）。</summary>
    public const string PresetId = "codex-contract";

    /// <summary>预设目录下的元数据文件名。</summary>
    public const string MetadataFileName = "preset.yml";

    /// <summary>standard preset 中需要替换的官方 persona 原文（与 rc.6 发布的 agent.cordis.yml 一致）。</summary>
    public const string StandardPersonaLine =
        "You are a coding agent powered by the {{model}} model. Your working directory is {{cwd}}.";

    private readonly string home;

    public HarnessContractProfileService(string? userProfile = null)
    {
        // DSH Home 解析与官方一致：显式路径 > $DSH_HOME > ~/.dsh。
        var configured = userProfile;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var envHome = Environment.GetEnvironmentVariable("DSH_HOME");
            configured = string.IsNullOrWhiteSpace(envHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
                : Path.GetFullPath(envHome);
        }
        home = Path.GetFullPath(configured);
    }

    /// <summary>DSH Home 根目录（~/.dsh 或 $DSH_HOME）。</summary>
    public string DshHome => home;

    public string PresetDirectory => Path.Combine(home, ".agent-presets", PresetId);
    public string CompositionPath => Path.Combine(PresetDirectory, "agent.cordis.yml");
    public string MetadataPath => Path.Combine(PresetDirectory, MetadataFileName);

    /// <summary>preset 是否已安装（组合文件与元数据文件齐全）。</summary>
    public bool IsInstalled => File.Exists(CompositionPath) && File.Exists(MetadataPath);

    /// <summary>
    /// 探测当前 DSH 是否支持 Helper 的 profile/preset 覆盖能力：
    /// 1) dsh 包内存在官方 standard preset 组合文件；2) 该组合包含可识别的 persona 原文。
    /// 不满足时返回 false，调用方必须诚实降级到 standard，UI 不得虚报。
    /// </summary>
    public bool IsSupported(string dshEntryPath)
    {
        try
        {
            var packageRoot = FindPackageRoot(dshEntryPath);
            if (packageRoot is null) return false;
            var shipped = Path.Combine(packageRoot, "config", "agent-presets", "standard", "agent.cordis.yml");
            return File.Exists(shipped) && File.ReadAllText(shipped, Encoding.UTF8).Contains(StandardPersonaLine, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 幂等生成/修复 codex-contract preset。基于官方 standard preset 的完整副本，
    /// 仅替换 persona 为合同模式规则（中文进度、直接实施、只做 workerChecks、
    /// 禁止重新规划/截图/视觉验收/发布结论、结构化 EXECUTION_REPORT）。
    /// 结构不兼容（persona 原文缺失）时抛 InvalidOperationException 拒绝生成，
    /// 绝不产出残缺预设；调用方捕获后降级 standard。
    /// </summary>
    public void InstallOrRepair(string dshEntryPath)
    {
        var packageRoot = FindPackageRoot(dshEntryPath)
            ?? throw new InvalidOperationException("无法定位 Harness 包根目录，不能生成 Codex 合同模式预设。");
        var shipped = Path.Combine(packageRoot, "config", "agent-presets", "standard", "agent.cordis.yml");
        if (!File.Exists(shipped))
            throw new FileNotFoundException("当前 Harness 未提供 standard agent preset。", shipped);
        var composition = File.ReadAllText(shipped, Encoding.UTF8);
        if (!composition.Contains(StandardPersonaLine, StringComparison.Ordinal))
            throw new InvalidOperationException("standard preset 的 persona 结构已变化，已拒绝生成不兼容配置。");
        // The persona lives inside a YAML folded scalar (text: >-). Every continuation
        // line must keep the same six-space indentation; raw newlines make the preset
        // syntactically invalid and prevent Harness from creating any session.
        var yamlPersona = ContractPersonaText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\n      ", StringComparison.Ordinal);
        composition = composition.Replace(StandardPersonaLine, yamlPersona, StringComparison.Ordinal);

        Directory.CreateDirectory(PresetDirectory);
        AtomicFile.WriteAllText(CompositionPath, composition);
        AtomicFile.WriteAllText(MetadataPath, BuildMetadata());
    }

    /// <summary>合同模式 persona：五阶段协议、冻结决策优先、集中读取与批量编辑、仅 workerChecks、结束即报告。</summary>
    public const string ContractPersonaText =
        "你是 Codex Helper 的合同实现执行器，模型为 {{model}}，工作目录为 {{cwd}}。\n" +
        "只执行任务目录内的 SPEC.md 与 HANDOFF.md。方案已冻结：最多一次性列出不超过 5 项简短实施动作，然后直接实施，禁止重新设计或重新规划。\n" +
        "本任务按五阶段协议执行：合同校验、一次性冻结计划、批量实现、workerChecks 单次执行、写报告并停止；每阶段只能前进，禁止回到规划阶段，不因发现可以顺便优化而扩大范围。\n" +
        "集中读取授权文件后批量编辑，同一未变化文件不得重复读取；相同只读工具调用不得连续重复。所有用户可见自然语言（计划、分析、中间说明、工具前后说明、进度、测试解释和最终报告）必须使用简体中文；仅代码标识符、命令、路径和原始错误可保留英文。\n" +
        "同一失败原因达到当前强度阈值立即停止；禁止重复构建、重复测试、重复打包；未在 manifest 列出的安装/打包/发布不得执行。禁止截图、禁止查看图片、禁止做视觉结论、禁止发布结论；完成后只运行 workerChecks：有显式 workerChecks 时只能逐项运行该列表且每项最多一次，严禁追加任何未列出的构建、测试、检查或自查。无显式 workerChecks 时只对受影响项目做一次 Release build（无法确定项目时在报告中交给 GPT），不自动运行完整测试套件，不递归扫描仓库寻找测试入口。\n" +
        "凭据与任务正文绝不进入命令行；完成即报告：写入 EXECUTION_REPORT.md 并停止，等待 GPT 验收。";

    private static string BuildMetadata()
        => "name: Codex 合同模式\ndescription: GPT 规划验收，Harness 仅实现与 workerChecks。\norder: 0\n";

    /// <summary>从 dsh 入口向上找包含 package.json 且带 config/agent-presets 的包根目录。</summary>
    private static string? FindPackageRoot(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var dir = new FileInfo(entry).Directory;
        for (var i = 0; dir is not null && i < 5; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "package.json")) && Directory.Exists(Path.Combine(dir.FullName, "config", "agent-presets")))
                return dir.FullName;
        return null;
    }
}
