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
        _ => "中文进度、直接实施、只做 workerChecks、结构化 EXECUTION_REPORT；使用 Helper 托管的 codex-contract 预设（官方 standard 完整副本，只替换 persona 的 prefix/text 标量），预设结构确实不兼容时降级 standard 并显示具体原因。同一开发目录（项目目录归一化相同）续用 Helper 自己登记的持续会话：首份合同独立读取任务合同并实施；同一目录的后续合同在最近一个已停止会话上提交增量回合，只先读 Helper 生成的有界 PROJECT_CONTEXT.md 与当前 HANDOFF.md，禁止递归扫描项目。各合同 TaskId/指纹/组键/报告仍严格独立审计，跨项目绝不复用。"
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
/// 只替换 persona 的 prefix/text 标量为合同模式规则（suffix 与其余原文逐字不变）；
/// 结构不兼容时拒绝生成并诚实降级到 standard，安装失败保留旧预设。
/// 生成完全幂等：重复调用不写盘且产生逐字节相同内容。
/// </summary>
public sealed class HarnessContractProfileService
{
    /// <summary>Helper 专属 preset id（也是目录名）。</summary>
    public const string PresetId = "codex-contract";

    /// <summary>预设目录下的元数据文件名。</summary>
    public const string MetadataFileName = "preset.yml";

    /// <summary>旧版 standard preset 的官方 persona 单行原文（rc.6 及更早；仅用于兼容识别的说明文本）。</summary>
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
    /// 探测当前 DSH 是否支持 Helper 的 profile/preset 覆盖能力。与
    /// <see cref="InstallOrRepair"/> 共用同一识别入口（<see cref="HarnessContractPresetCompatibility.TryRecognize"/>），
    /// 绝不出现"IsSupported true 而 Install 拒绝"或反之的分裂判定。不满足时返回 false，
    /// 调用方必须诚实降级到 standard，UI 不得虚报。
    /// </summary>
    public bool IsSupported(string dshEntryPath) => Probe(dshEntryPath).Supported;

    /// <summary>
    /// 带具体诊断的兼容探测：支持时 Supported=true 并给出后端来源；不支持时 Reason 说明
    /// 是"未定位到官方 standard 预设"、"persona 结构变化"还是"多个 persona/别名/路径越界"。
    /// 诊断文本只含来源描述与结构事实，已脱敏且不含任何凭据。
    /// </summary>
    public (bool Supported, string Reason, string Backend) Probe(string? dshEntryPath)
    {
        if (HarnessContractPresetCompatibility.TryRecognize(dshEntryPath, out var located, out var rewrite, out var diagnostic))
            return (true, diagnostic, located!.BackendText);
        return (false, diagnostic, "未定位到兼容的 standard 预设");
    }

    /// <summary>
    /// 幂等生成/修复 codex-contract preset。基于官方 standard preset 的完整副本，
    /// 仅替换 persona 的 prefix/text 标量为合同模式规则（中文进度、直接实施、只做 workerChecks、
    /// 禁止重新规划/截图/视觉验收/发布结论、结构化 EXECUTION_REPORT）；suffix、工具行、realm 与
    /// <c>!!js</c> 标签、注释与换行风格全部逐字保留。
    /// <para>
    /// 两个文件（组合文件 + 元数据）作为一个事务提交：先备份旧文件，任一步失败回滚到旧内容，
    /// 绝不留下半写状态；内容已一致时不写盘（重复调用零写入）。
    /// 结构不兼容（未定位到来源、persona 不可识别、多个 persona、别名、路径/软链越界）时抛
    /// <see cref="InvalidOperationException"/> 拒绝生成，绝不产出残缺预设；调用方捕获后降级 standard。
    /// </para>
    /// </summary>
    public void InstallOrRepair(string dshEntryPath)
    {
        if (string.IsNullOrWhiteSpace(dshEntryPath))
            throw new InvalidOperationException("未配置 Harness dsh 入口，不能生成 Codex 合同模式预设。");
        // 与 IsSupported 共用的同一识别入口：绝不允许一个 false 一个 true。
        if (!HarnessContractPresetCompatibility.TryRecognize(dshEntryPath, out var located, out var recognition, out var diagnostic))
            throw new InvalidOperationException("standard preset 不兼容，已拒绝生成合同模式预设：" + diagnostic);
        var shipped = located!.CompositionPath;
        if (!File.Exists(shipped))
            throw new FileNotFoundException("当前 Harness 未提供 standard agent preset。", shipped);

        var composition = File.ReadAllText(shipped, Encoding.UTF8);
        var rewrite = HarnessContractPresetCompatibility.RewritePersonaPrefix(composition, ContractPersonaText);
        if (!rewrite.Supported || rewrite.Rewritten is null)
            throw new InvalidOperationException("standard preset 的 persona 结构不兼容，已拒绝生成不兼容配置：" + recognition.Reason);

        // 路径越界与软链拒绝：目标必须位于本 DSH Home 之下，且整条路径不得经过重解析点。
        if (HarnessContractPresetCompatibility.AnyReparsePointOnPath(home, PresetDirectory)
            || HarnessContractPresetCompatibility.AnyReparsePointOnPath(home, CompositionPath)
            || HarnessContractPresetCompatibility.AnyReparsePointOnPath(home, MetadataPath))
            throw new InvalidOperationException("预设写入路径位于符号链接或重解析点上（越界风险），已拒绝写入。");

        var metadata = BuildMetadata();
        Directory.CreateDirectory(PresetDirectory);
        using var writeLease = new FileStream(Path.Combine(PresetDirectory, ".helper-write.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // 写前完整读取：任何旧文件不可读（共享冲突/权限/IO）一律拒绝写入，绝不把"读不到"当成
        // "不存在"而在回滚时删掉一个其实存在且重要的旧文件；读到的原文同时作为可恢复备份。
        var compositionExisting = ReadExisting(CompositionPath);
        var metadataExisting = ReadExisting(MetadataPath);
        // 幂等前置：两个文件内容都已一致时零写入（不触碰时间戳、不产生任何中间文件）。
        if (MatchesContent(compositionExisting, rewrite.Rewritten) && MatchesContent(metadataExisting, metadata)) return;

        var backupSuffix = ".bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        if (compositionExisting is not null) AtomicFile.WriteAllText(CompositionPath + backupSuffix, compositionExisting);
        if (metadataExisting is not null) AtomicFile.WriteAllText(MetadataPath + backupSuffix, metadataExisting);
        // ---- 两文件事务：写前已完整读取（作为备份）→ 写入 → 失败回滚旧内容并如实报告回滚失败 ----
        try
        {
            AtomicFile.WriteAllText(CompositionPath, rewrite.Rewritten);
            AtomicFile.WriteAllText(MetadataPath, metadata);
        }
        catch (Exception writeFailure)
        {
            var compositionRollback = TryRestore(CompositionPath, compositionExisting);
            var metadataRollback = TryRestore(MetadataPath, metadataExisting);
            var rollbackFact = compositionRollback is null && metadataRollback is null
                ? "已回滚旧预设。"
                : "回滚未完全成功：" + string.Join("；", new[] { compositionRollback, metadataRollback }.Where(item => item is not null)) + "。";
            throw new InvalidOperationException("写入 codex-contract 预设失败，" + rollbackFact
                + " 原始原因：" + HarnessContractPresetCompatibility.Sanitize(writeFailure.Message), writeFailure);
        }
    }

    private static bool MatchesContent(string? existing, string expected)
        => existing is not null && string.Equals(existing, expected, StringComparison.Ordinal);

    /// <summary>
    /// 写入前读取旧文件原文：文件不存在返回 null；存在但不可读（权限/共享冲突/IO/编码失败）
    /// 一律抛出可读中文异常拒绝写入——绝不把不可读误认成不存在。
    /// </summary>
    private static string? ReadExisting(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("已有预设文件不可读，已拒绝写入以免误删可恢复内容（"
                + HarnessContractPresetCompatibility.Sanitize(ex.Message) + "）：" + path, ex);
        }
    }

    /// <summary>
    /// 回滚到旧内容；回滚本身失败时返回可读原因（绝不吞掉），成功返回 null。
    /// 备份为 null 表示写入前该文件确实不存在，回滚即删除本次写入的文件。
    /// </summary>
    private static string? TryRestore(string path, string? backup)
    {
        try
        {
            if (backup is null)
            {
                if (File.Exists(path)) File.Delete(path);
                return null;
            }
            AtomicFile.WriteAllText(path, backup);
            return null;
        }
        catch (Exception ex)
        {
            // 回滚失败不掩盖原始写入失败原因，但必须如实回报（旧文件可能已被半写替换）。
            return HarnessContractPresetCompatibility.Sanitize(ex.Message);
        }
    }

    /// <summary>合同模式 persona：五阶段协议、冻结决策优先、集中读取与批量编辑、仅 workerChecks、结束即报告。</summary>
    public const string ContractPersonaText =
        "你是 Codex Helper 的合同实现执行器，模型为 {{model}}，工作目录为 {{cwd}}。\n" +
        "只执行任务目录内的 SPEC.md 与 HANDOFF.md。方案已冻结：最多一次性列出不超过 5 项简短实施动作，然后直接实施，禁止重新设计或重新规划。\n" +
        "本任务按五阶段协议执行：合同校验、一次性冻结计划、批量实现、workerChecks 单次执行、写报告并停止；每阶段只能前进，禁止回到规划阶段，不因发现可以顺便优化而扩大范围。快速连续：首次基线合同独立读取任务合同后直接实施；可信同组键（rootCauseKey）前序回合通过报告门禁后，后续回合只读 Helper 生成的有界 PROJECT_CONTEXT.md（来源标识与门禁事实）与当前 HANDOFF.md 直接依赖范围，禁止递归扫描：禁止为理解旧合同递归扫描项目，禁止递归扫描仓库或无关配置。\n" +
        "集中读取授权文件后批量编辑，同一未变化文件不得重复读取；相同只读工具调用不得连续重复。所有用户可见自然语言（计划、分析、中间说明、工具前后说明、进度、测试解释和最终报告）必须使用简体中文；仅代码标识符、命令、路径和原始错误可保留英文。\n" +
        "同一失败原因达到当前强度阈值立即停止；禁止重复构建、重复测试、重复打包；未在 manifest 列出的安装/打包/发布不得执行。禁止截图、禁止查看图片、禁止做视觉结论、禁止发布结论；完成后只运行 workerChecks：有显式 workerChecks 时只能逐项运行该列表且每项最多一次，严禁追加任何未列出的构建、测试、检查或自查。无显式 workerChecks 时只对受影响项目做一次 Release build（无法确定项目时在报告中交给 GPT），不自动运行完整测试套件，不递归扫描仓库寻找测试入口。\n" +
        "凭据与任务正文绝不进入命令行；完成即报告：写入 EXECUTION_REPORT.md，成功退出码必须单独写成一行“- 退出码：0”，该行不得附加括号、命令或解释（解释可写在 workerChecks 条目中），随后停止，等待 GPT 验收。";

    private static string BuildMetadata()
        => "name: Codex 合同模式\ndescription: GPT 规划验收，Harness 仅实现与 workerChecks；同一开发目录续用同一 DSH 会话，Helper 生成有界 PROJECT_CONTEXT.md。\norder: 0\n";
}
