using System.Text;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// DSH 组件扫描与状态判定：扫描 <c>skills/*</c>、<c>.agent-presets/*</c> 与
/// <c>profiles/node_modules</c> 直接一级插件（复用 DshExtensionBackupService 的发现规则），
/// 解析内嵌声明、用户侧车与 Helper 受信侧车，判定配置状态。
/// 检查现有配置时只返回布尔/缺失字段 id，绝不把密钥值传到结果、日志或异常。
/// 静态关键词扫描只产生“可能需要设置”的提示，绝不自动生成 secret 字段或 required 分类。
/// </summary>
public sealed class DshComponentScanner
{
    private const string EmbeddedDeclarationFileName = "helper-setup.json";
    private const long MaxHintScanBytes = 4L * 1024 * 1024;

    /// <summary>单侧车目录直接文件数量上限：超出视为目录异常，保守不关联任何侧车。</summary>
    private const int MaxSidecarFiles = 256;

    /// <summary>静态提示关键词：命中只产生“可能需要设置”的提示，绝不提升为 required。</summary>
    private static readonly string[] HintKeywords =
    [
        "api key", "apikey", "api_key", "access token", "access_token", "bearer", "credential", "secret"
    ];

    private readonly string home;
    private readonly DshExtensionBackupService discovery;
    private readonly HelperSetupManifestService manifestService = new();

    public DshComponentScanner(string? configuredHome = null)
    {
        discovery = new DshExtensionBackupService(configuredHome);
        home = discovery.DshHome;
    }

    /// <summary>当前设备 DSH Home（与备份服务同一解析规则）。</summary>
    public string DshHome => home;

    /// <summary>DSH 是否已安装（Home 目录存在）。未安装返回空列表，不抛异常。</summary>
    public bool IsInstalled => discovery.IsInstalled;

    /// <summary>
    /// 扫描全部组件。单个组件损坏/无声明只影响该组件，绝不拖垮整个扫描；
    /// 结果按类型与名称排序，稳定输出。任何结果都不含密钥值。
    /// 一次扫描内只建立一次只读快照：侧车目录只枚举/解析一次、provider/model 配置只扫描
    /// 一次，全部组件循环复用，避免每个组件重复遍历 profiles 与侧车目录。
    /// </summary>
    public IReadOnlyList<DshComponentInfo> Scan(CancellationToken cancellationToken = default)
    {
        var result = new List<DshComponentInfo>();
        if (!IsInstalled) return result;

        var snapshot = BuildScanSnapshot(cancellationToken);

        foreach (var skill in ScanDirectories(Path.Combine(home, "skills"), ".system"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ScanSkillOrPreset(skill, DshComponentType.Skill, "skills", snapshot));
        }
        foreach (var preset in ScanDirectories(Path.Combine(home, ".agent-presets")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ScanSkillOrPreset(preset, DshComponentType.Preset, ".agent-presets", snapshot));
        }
        foreach (var plugin in discovery.DiscoverPluginPackages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ScanPlugin(plugin.Name, plugin.Directory, snapshot));
        }
        return result
            .OrderBy(component => component.Kind)
            .ThenBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- 目录扫描 ----

    private static IEnumerable<string> ScanDirectories(string root, params string[] excludedNames)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (info.Name.StartsWith('.')) continue;
            if (excludedNames.Any(name => string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            yield return directory;
        }
    }

    // ---- skill / preset ----

    private DshComponentInfo ScanSkillOrPreset(string directory, DshComponentType kind, string relativeRoot, ScanSnapshot snapshot)
    {
        var name = Path.GetFileName(directory);
        var info = new DshComponentInfo { Name = name, Kind = kind, RootPath = directory, TargetRelativeRoot = relativeRoot + "/" + name };

        // 用户 preset 侧车：声明自身是身份事实源，按 componentId 精确匹配预设目录名。
        SidecarLookup? presetSidecar = null;
        if (kind == DshComponentType.Preset)
        {
            presetSidecar = FindSidecar(snapshot, pluginSidecar: false, packageName: null, presetComponentId: name);
        }

        var embeddedPath = Path.Combine(directory, EmbeddedDeclarationFileName);
        if (File.Exists(embeddedPath))
        {
            ApplyDeclaration(info, TryParse(embeddedPath), source: DshDeclarationSource.Embedded, snapshot.ConfiguredProviderIds);
            if (info.Status == DshComponentStatus.SetupNone) return info;
            return info;
        }
        if (presetSidecar is not null)
        {
            if (presetSidecar.Ambiguous) { MarkSidecarAmbiguous(info, presetSidecar, "预设"); return info; }
            if (presetSidecar.Broken) { MarkSidecarBroken(info, presetSidecar); return info; }
            if (presetSidecar.Path is not null)
            {
                ApplyDeclaration(info, TryParse(presetSidecar.Path), source: DshDeclarationSource.UserSidecar, snapshot.ConfiguredProviderIds);
                return info;
            }
        }

        // 无声明：静态提示（只产生“可能需要设置”提示，绝不自动生成 secret/required）。
        var hint = FindStaticHint(directory);
        if (hint is not null)
        {
            info.Setup = DshSetupState.Unknown;
            info.Status = DshComponentStatus.ManualReview;
            info.ReviewReason = "未找到配置声明，静态扫描提示可能需要设置：请人工确认。";
            info.Evidence.Add(hint);
            info.SetupSteps = "请人工确认该组件是否需要机器配置；需要时在组件根目录放置 helper-setup.json 声明。";
        }
        else
        {
            info.Setup = DshSetupState.None;
            info.Status = DshComponentStatus.SetupNone;
            info.Evidence.Add("无配置声明且无静态提示，视为无需配置。");
        }
        return info;
    }

    // ---- 插件 ----

    private DshComponentInfo ScanPlugin(string packageName, string packageRoot, ScanSnapshot snapshot)
    {
        var info = new DshComponentInfo
        {
            Name = packageName,
            Kind = DshComponentType.Plugin,
            RootPath = packageRoot,
            TargetRelativeRoot = "profiles/node_modules/" + packageName,
            Version = ReadPackageVersion(packageRoot)
        };

        var embeddedPath = Path.Combine(packageRoot, EmbeddedDeclarationFileName);
        // 用户插件侧车：声明自身是身份事实源，按 package.name 精确匹配实际包名。
        var userSidecar = FindSidecar(snapshot, pluginSidecar: true, packageName: packageName, presetComponentId: null);

        if (File.Exists(embeddedPath))
        {
            // 内嵌声明优先；带包绑定时校验名称（第一方内嵌可省略指纹）。
            var declaration = TryParse(embeddedPath);
            if (declaration is not null && declaration.Package is not null
                && !string.Equals(declaration.Package.Name, packageName, StringComparison.Ordinal))
            {
                info.ReviewReason = $"内嵌声明绑定包名不匹配：声明 {declaration.Package.Name}，实际 {packageName}。";
                info.Status = DshComponentStatus.ManualReview;
                info.Evidence.Add("内嵌声明绑定校验失败，声明已过期/待人工确认。");
                return info;
            }
            ApplyDeclaration(info, declaration, source: DshDeclarationSource.Embedded, snapshot.ConfiguredProviderIds);
            return info;
        }
        if (userSidecar.Ambiguous)
        {
            MarkSidecarAmbiguous(info, userSidecar, "插件");
            return info;
        }
        if (userSidecar.Broken)
        {
            MarkSidecarBroken(info, userSidecar);
            return info;
        }
        if (userSidecar.Path is not null)
        {
            var declaration = TryParse(userSidecar.Path);
            if (declaration is not null)
            {
                var mismatch = HelperSetupManifestService.ValidateSidecarBinding(
                    declaration, packageName, info.Version, Path.Combine(packageRoot, "package.json"),
                    ResolveEntryOrDefault(packageRoot, packageName), requireFingerprint: true);
                if (mismatch is not null)
                {
                    info.ReviewReason = mismatch;
                    info.Status = DshComponentStatus.ManualReview;
                    info.DeclarationSource = DshDeclarationSource.UserSidecar;
                    info.Evidence.Add("用户侧车绑定校验失败：" + mismatch);
                    info.SetupSteps = "请人工确认侧车声明是否仍然有效；过期声明不会被应用。";
                    return info;
                }
            }
            ApplyDeclaration(info, declaration, source: DshDeclarationSource.UserSidecar, snapshot.ConfiguredProviderIds);
            return info;
        }

        // Helper 受信侧车：按包名匹配，运行时核对版本范围（* 通配任意版本）。
        var trusted = HelperSetupManifestService.TrustedSidecars()
            .FirstOrDefault(declaration => declaration.Package is not null
                && string.Equals(declaration.Package.Name, packageName, StringComparison.Ordinal));
        if (trusted is not null)
        {
            if (!string.IsNullOrWhiteSpace(info.Version)
                && !HelperSetupManifestService.MatchesVersionRange(info.Version, trusted.Package!.VersionRange))
            {
                info.ReviewReason = $"受信侧车版本范围不匹配：声明 {trusted.Package.VersionRange}，实际 {info.Version}。";
                info.Status = DshComponentStatus.ManualReview;
                info.DeclarationSource = DshDeclarationSource.TrustedSidecar;
                info.Evidence.Add("受信侧车版本核对失败，声明已过期/待人工确认。");
                return info;
            }
            ApplyDeclaration(info, trusted, source: DshDeclarationSource.TrustedSidecar, snapshot.ConfiguredProviderIds);
            return info;
        }

        // 无声明：静态提示。
        var hint = FindStaticHint(packageRoot);
        if (hint is not null)
        {
            info.Setup = DshSetupState.Unknown;
            info.Status = DshComponentStatus.ManualReview;
            info.ReviewReason = "未找到配置声明，静态扫描提示可能需要设置：请人工确认。";
            info.Evidence.Add(hint);
            info.SetupSteps = "请人工确认该插件是否需要机器配置；需要时放置 helper-setup.json 或侧车声明。";
        }
        else
        {
            info.Setup = DshSetupState.None;
            info.Status = DshComponentStatus.SetupNone;
            info.Evidence.Add("无配置声明且无静态提示，视为无需配置。");
        }
        return info;
    }

    // ---- 声明应用与状态判定 ----

    private HelperSetupDeclaration? TryParse(string path)
    {
        try { return manifestService.ParseFile(path); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void ApplyDeclaration(DshComponentInfo info, HelperSetupDeclaration? declaration, DshDeclarationSource source, IReadOnlySet<string> configuredProviderIds)
    {
        info.DeclarationSource = source;
        if (declaration is null)
        {
            info.Setup = DshSetupState.Unknown;
            info.Status = DshComponentStatus.InvalidDeclaration;
            info.ReviewReason = "配置声明无法解析或校验失败（声明无效），未应用任何字段。";
            info.Evidence.Add("声明解析失败，拒绝应用字段；请检查声明格式与校验规则。");
            return;
        }
        info.Declaration = declaration;
        info.Setup = Enum.Parse<DshSetupState>(declaration.Setup, ignoreCase: true);
        info.Evidence.AddRange(declaration.Evidence);
        if (declaration.DisplayName.Length > 0 && !string.Equals(declaration.DisplayName, info.Name, StringComparison.OrdinalIgnoreCase))
            info.Evidence.Add("声明显示名称：" + declaration.DisplayName);

        switch (info.Setup)
        {
            case DshSetupState.None:
                info.Status = DshComponentStatus.SetupNone;
                info.SetupSteps = "该组件无需机器配置，可直接使用。";
                break;
            case DshSetupState.Optional:
                info.Status = DshComponentStatus.OptionalConfig;
                info.SetupSteps = BuildSetupSteps(declaration, info);
                break;
            case DshSetupState.Required:
                info.Status = BuildRequiredStatus(declaration, info, configuredProviderIds);
                info.SetupSteps = BuildSetupSteps(declaration, info);
                break;
            default:
                info.Status = DshComponentStatus.ManualReview;
                info.ReviewReason = "声明标记为 unknown：请人工确认配置需求。";
                info.SetupSteps = "请人工确认该组件是否需要机器配置。";
                break;
        }
    }

    /// <summary>
    /// required 组件的配置探测：providerRef/modelRef 字段通过扫描 DSH 非凭据配置文本
    /// （profiles 下 YAML/JSON/JS，排除 .credentials.yaml、node_modules、会话与附件）确认
    /// 是否已引用匹配的 provider/model；secret/url/model 字段无法安全探测（不解析 DSH 私有
    /// 凭据），一律列为缺失。只返回布尔/缺失字段 id，绝不返回密钥值。
    /// </summary>
    private DshComponentStatus BuildRequiredStatus(HelperSetupDeclaration declaration, DshComponentInfo info, IReadOnlySet<string> configuredProviderIds)
    {
        var missing = new List<string>();
        foreach (var field in declaration.Fields.Where(field => field.Required))
        {
            var satisfied = field.Kind switch
            {
                "providerRef" => configuredProviderIds.Contains(NormalizeReference(field.Id)),
                "modelRef" => configuredProviderIds.Contains(NormalizeReference(field.Id)),
                _ => false
            };
            if (!satisfied) missing.Add(field.Id);
        }
        info.MissingFieldIds = missing;
        return missing.Count == 0 ? DshComponentStatus.Ready : DshComponentStatus.RequiredMissing;
    }

    /// <summary>
    /// 扫描 DSH 非凭据配置文本中的 provider/model 标识（规范化小写集合）。一次扫描只调用一次，
    /// 结果在组件循环中复用；遍历与备份服务的插件发现共用同一套非配置目录排除规则。
    /// </summary>
    private HashSet<string> FindConfiguredProviderIds(CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var profilesRoot = Path.Combine(home, "profiles");
        if (!Directory.Exists(profilesRoot)) return found;
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".yaml", ".yml", ".json", ".js", ".mjs", ".cjs" };
        var stack = new Stack<string>();
        stack.Push(profilesRoot);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            string[] directories;
            string[] files;
            try { directories = Directory.GetDirectories(current); files = Directory.GetFiles(current); }
            catch { continue; }
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (info.Name.StartsWith('.')) continue;
                if (DshExtensionBackupService.NonConfigDirectoryNames.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(directory);
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extensions.Contains(Path.GetExtension(file))) continue;
                if (IsCredentialFileName(file)) continue;
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.Length <= 0 || fileInfo.Length > MaxHintScanBytes) continue;
                    var text = File.ReadAllText(file, Encoding.UTF8);
                    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                        text, @"(?:provider|model)\s*[:=]\s*[""']?([A-Za-z0-9._-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        if (match.Groups[1].Value.Length > 0)
                            found.Add(NormalizeReference(match.Groups[1].Value));
                    }
                }
                catch { }
            }
        }
        return found;
    }

    private static string NormalizeReference(string value) => value.Trim().ToLowerInvariant();

    private string BuildSetupSteps(HelperSetupDeclaration declaration, DshComponentInfo info)
    {
        var steps = new List<string>();
        foreach (var field in declaration.Fields)
        {
            switch (field.Kind)
            {
                case "secret":
                    steps.Add($"· {field.Label}（{field.Id}）：在 Harness Web 设置中配置；Helper 不读写 DSH 私有凭据。");
                    break;
                case "providerRef":
                case "modelRef":
                    steps.Add($"· {field.Label}（{field.Id}）：选择已存在的 DSH Provider/Model，不重复索要 API Key。");
                    break;
                default:
                    steps.Add($"· {field.Label}（{field.Id}）：{(field.Required ? "必填" : "可选")}。");
                    break;
            }
        }
        if (steps.Count == 0) return "该组件无需机器配置。";
        steps.Add("完成后点“重新扫描”确认配置状态。");
        return string.Join("\n", steps);
    }

    // ---- 静态提示 ----

    /// <summary>扫描组件根目录文本（SKILL.md、README、*.js 等，≤4MB）中的提示关键词；返回第一条命中原文。</summary>
    private static string? FindStaticHint(string root)
    {
        try
        {
            var candidates = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(file => Path.GetExtension(file) is ".md" or ".txt" or ".js" or ".mjs" or ".cjs" or ".json" or ".yaml" or ".yml")
                .ToList();
            foreach (var file in candidates)
            {
                var info = new FileInfo(file);
                if (info.Length <= 0 || info.Length > MaxHintScanBytes) continue;
                if (IsCredentialFileName(file)) continue;
                var text = File.ReadAllText(file, Encoding.UTF8);
                foreach (var keyword in HintKeywords)
                {
                    if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        return $"静态提示：{Path.GetFileName(file)} 中提到 “{keyword}”。";
                }
            }
        }
        catch { }
        return null;
    }

    // ---- 小工具 ----

    private static string? ReadPackageVersion(string packageRoot)
    {
        try
        {
            var path = Path.Combine(packageRoot, "package.json");
            if (!File.Exists(path)) return null;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == System.Text.Json.JsonValueKind.String)
                return version.GetString();
        }
        catch { }
        return null;
    }

    private static string ResolveEntryOrDefault(string packageRoot, string packageName)
    {
        try { return HelperSetupManifestService.ResolvePackageEntry(packageRoot, packageName); }
        catch { return Path.Combine(packageRoot, "index.js"); }
    }

    // ---- 侧车匹配 ----

    /// <summary>侧车匹配结果：唯一可解析匹配、无匹配、多个匹配（歧义）或唯一归属但声明损坏。</summary>
    private sealed record SidecarLookup(string? Path, bool Ambiguous, bool Broken, string? Reason);

    // ---- 扫描快照（一次扫描只建立一次只读缓存） ----

    /// <summary>一次扫描内的只读快照：侧车目录条目（枚举/解析一次）与 provider/model 配置集合（扫描一次）。</summary>
    private sealed record ScanSnapshot(
        IReadOnlyList<SidecarEntry> PluginSidecars,
        IReadOnlyList<SidecarEntry> PresetSidecars,
        string? PluginSidecarsOverLimitReason,
        string? PresetSidecarsOverLimitReason,
        IReadOnlySet<string> ConfiguredProviderIds);

    /// <summary>侧车文件条目：解析成功时保留声明；解析失败时保留安全浅层归属（仅用于归属判定，不应用字段）。</summary>
    private sealed record SidecarEntry(string Path, HelperSetupDeclaration? Declaration, string? PeekedOwner);

    private ScanSnapshot BuildScanSnapshot(CancellationToken cancellationToken)
    {
        var (pluginSidecars, pluginOverLimit) = IndexSidecars(Path.Combine(home, ".helper-setup", "plugins"), plugin: true, cancellationToken);
        var (presetSidecars, presetOverLimit) = IndexSidecars(Path.Combine(home, ".helper-setup", "presets"), plugin: false, cancellationToken);
        return new ScanSnapshot(pluginSidecars, presetSidecars, pluginOverLimit, presetOverLimit, FindConfiguredProviderIds(cancellationToken));
    }

    /// <summary>枚举并解析侧车目录顶层文件一次（不递归、有文件数量上限）；返回条目列表与超限原因。</summary>
    private (IReadOnlyList<SidecarEntry> Entries, string? OverLimitReason) IndexSidecars(string sidecarRoot, bool plugin, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sidecarRoot)) return (Array.Empty<SidecarEntry>(), null);
        var files = Directory.EnumerateFiles(sidecarRoot, "*.helper-setup.json", SearchOption.TopDirectoryOnly)
            .Take(MaxSidecarFiles + 1)
            .ToList();
        if (files.Count > MaxSidecarFiles)
            return (Array.Empty<SidecarEntry>(), "侧车目录文件数量超过安全上限，未关联任何侧车。");
        var entries = new List<SidecarEntry>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var declaration = TryParse(file);
            var peekedOwner = declaration is null ? PeekSidecarOwner(file, plugin) : null;
            entries.Add(new SidecarEntry(file, declaration, peekedOwner));
        }
        return (entries, null);
    }

    /// <summary>
    /// 在扫描快照中查找属于当前组件的侧车。声明自身是身份事实源：
    /// 插件按 package.name 精确匹配实际包名，预设按 componentId 精确匹配目录名；
    /// 解析失败的文件仅当能从安全浅层元数据（package.name/componentId 浅读）或旧 Base64 文件名
    /// 确定归属时才关联（显示声明无效），否则作为全局诊断忽略，单个损坏或不相关侧车绝不拖垮
    /// 其他组件。多个匹配侧车视为歧义，不任意选择。侧车文件在快照中已枚举/解析一次，此处只做内存过滤。
    /// </summary>
    private SidecarLookup FindSidecar(ScanSnapshot snapshot, bool pluginSidecar, string? packageName, string? presetComponentId)
    {
        var entries = pluginSidecar ? snapshot.PluginSidecars : snapshot.PresetSidecars;
        var overLimitReason = pluginSidecar ? snapshot.PluginSidecarsOverLimitReason : snapshot.PresetSidecarsOverLimitReason;
        if (overLimitReason is not null) return new(null, false, false, overLimitReason);
        var matches = new List<string>();
        var broken = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Declaration is not null)
            {
                var matched = packageName is not null
                    ? entry.Declaration.Package is not null && string.Equals(entry.Declaration.Package.Name, packageName, StringComparison.Ordinal)
                    : string.Equals(entry.Declaration.ComponentId, presetComponentId, StringComparison.Ordinal);
                if (matched) matches.Add(entry.Path);
                continue;
            }
            // 损坏声明：仅当安全浅层元数据或旧 Base64 文件名能确定归属时才关联。
            var owned = packageName is not null
                ? string.Equals(entry.PeekedOwner, packageName, StringComparison.Ordinal)
                : string.Equals(entry.PeekedOwner, presetComponentId, StringComparison.Ordinal);
            if (owned)
            {
                broken.Add(entry.Path);
                continue;
            }
            if (IsLegacySidecarFileName(Path.GetFileName(entry.Path), packageName ?? presetComponentId!))
                broken.Add(entry.Path);
        }
        if (matches.Count > 1)
            return new(null, true, false, $"找到 {matches.Count} 个声明属于该组件的侧车，请人工确认后只保留唯一有效声明。");
        if (matches.Count == 1)
            return new(matches[0], false, false, null);
        if (broken.Count > 0)
            return new(null, false, true, $"侧车声明无法解析或校验失败：{Path.GetFileName(broken[0])}。");
        return new(null, false, false, null);
    }

    /// <summary>
    /// 从损坏声明安全浅读身份字段（插件 package.name / 预设 componentId），仅用于归属判定，
    /// 不应用任何字段。超限、非 JSON 或缺少身份字段返回 null（无法归属）。
    /// </summary>
    private static string? PeekSidecarOwner(string path, bool plugin)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > HelperSetupManifestService.MaxDeclarationBytes) return null;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (plugin)
            {
                if (root.TryGetProperty("package", out var package) && package.ValueKind == System.Text.Json.JsonValueKind.Object
                    && package.TryGetProperty("name", out var packageName) && packageName.ValueKind == System.Text.Json.JsonValueKind.String)
                    return packageName.GetString();
                return null;
            }
            if (root.TryGetProperty("componentId", out var componentId) && componentId.ValueKind == System.Text.Json.JsonValueKind.String)
                return componentId.GetString();
            return null;
        }
        catch { return null; }
    }

    /// <summary>旧 Base64 文件名是否为当前组件的编码（内容损坏时的兼容回退归属）。</summary>
    private static bool IsLegacySidecarFileName(string fileName, string componentName) =>
        string.Equals(fileName, EncodeSidecarId(componentName) + ".helper-setup.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>旧文件名 id 编码：URL 安全 base64（与 DshExtensionBackupService 的包名编码一致）；仅用于旧文件名兼容回退，侧车身份以声明内容为准。</summary>
    private static string EncodeSidecarId(string name) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(name)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void MarkSidecarAmbiguous(DshComponentInfo info, SidecarLookup lookup, string componentLabel)
    {
        info.ReviewReason = lookup.Reason;
        info.Status = DshComponentStatus.ManualReview;
        info.DeclarationSource = DshDeclarationSource.UserSidecar;
        info.Evidence.Add("多个侧车声明匹配同一" + componentLabel + "，未应用任何字段，请人工确认。");
        info.SetupSteps = "请人工确认并只保留唯一有效侧车声明后重新扫描。";
    }

    private static void MarkSidecarBroken(DshComponentInfo info, SidecarLookup lookup)
    {
        info.ReviewReason = lookup.Reason;
        info.Status = DshComponentStatus.InvalidDeclaration;
        info.DeclarationSource = DshDeclarationSource.UserSidecar;
        info.Evidence.Add("匹配组件的侧车声明损坏，拒绝应用字段；请检查声明格式与校验规则。");
        info.SetupSteps = "请修复或删除损坏的侧车声明后重新扫描。";
    }

    /// <summary>凭据类文件名硬排除（导出与提示扫描共用）。</summary>
    internal static bool IsCredentialFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (name.StartsWith('~')) return false;
        if (name.Contains("credential", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("token", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("session", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("cookie", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("api_key", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("apikey", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(".credentials.yaml", StringComparison.OrdinalIgnoreCase) || name.Equals("credentials.yaml", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
