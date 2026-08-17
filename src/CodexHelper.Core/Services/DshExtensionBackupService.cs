using System.Text;
using System.Text.RegularExpressions;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// DSH（DeepSeek Harness）用户 Skills、Agent 预设、Profile 配置与用户插件的发现与安全恢复。
/// DSH Home 解析与官方一致：优先 <c>$DSH_HOME</c>，未设置时使用 <c>%USERPROFILE%\.dsh</c>，
/// 路径一律规范化。
/// 备份发现：<c>skills/</c>、<c>.agent-presets/</c>、<c>profiles/</c>（排除 node_modules、临时文件与缓存），
/// 以及从 Profile 配置明确注册字段（YAML/JSON 的 name/plugin/package）与配置目录内 JS 静态
/// import/require 引用、<c>profiles/node_modules</c> 顶层 dsh-* 惯例识别的实际插件包——
/// 每个插件作为独立数据源，不备份整棵依赖树；兼容普通包与 scoped package，必须有 package.json。
/// 恢复规划：把快照中的 <c>dsh-*</c> 数据源安全映射到当前设备 DSH Home，拒绝路径越界、
/// 符号链接/重解析点和非法插件名；快照中的旧用户名绝对路径仅作展示，绝不作为恢复目标。
/// 绝对禁止备份：.credentials.yaml、API Key、会话 sessions/、附件 attachments/、storages/、缓存、日志与依赖树。
/// </summary>
public sealed class DshExtensionBackupService
{
    /// <summary>DSH 数据源的统一 id 前缀（快照内据此识别 DSH 数据）。</summary>
    public const string SourceIdPrefix = "dsh-";

    private const string SkillsSourceId = "dsh-skills";
    private const string AgentPresetsSourceId = "dsh-agent-presets";
    private const string ProfilesSourceId = "dsh-profiles";
    private const string PluginSourceIdPrefix = "dsh-plugin-";
    private static readonly string PluginsRelativeDirectory = Path.Combine("profiles", "node_modules");

    private static readonly string[] SkillsExcludedDirectories = [".system"];
    private static readonly string[] AgentPresetsExcludedDirectories = ["node_modules", ".cache", ".git"];
    private static readonly string[] PluginExcludedDirectories = ["node_modules", ".cache", ".git"];

    /// <summary>
    /// profiles 遍历（插件发现与配置探测）统一排除的非配置数据目录名：依赖树、会话、附件、
    /// storages、缓存、日志、临时目录、任务运行目录与工作目录等，大小写不敏感。
    /// 重解析点（符号链接/junction）在遍历时按目录属性额外排除。profiles 备份数据源同样使用该规则。
    /// </summary>
    internal static readonly string[] NonConfigDirectoryNames =
    [
        "node_modules", "sessions", "attachments", "storages",
        "cache", ".cache", "logs", "log",
        "temp", "tmp", ".temp", ".tmp",
        "runs", ".runs",
        "workdir", "workspace",
        ".git", "__pycache__", ".helper-setup", ".system"
    ];

    private static readonly string[] ProfilesExcludedDirectories = NonConfigDirectoryNames;

    /// <summary>临时文件排除：'.' 开头按后缀、'~' 开头按前缀匹配（见 BackupRepository.IsExcludedFileName）。</summary>
    private static readonly string[] TemporaryFileExclusions = [".tmp", ".temp", ".swp", ".part", ".crdownload", ".DS_Store", "~$"];

    /// <summary>Profile 配置文本扫描的单文件上限，防止异常超大文件拖垮发现。</summary>
    private const long MaxConfigScanBytes = 4L * 1024 * 1024;

    /// <summary>Profile 配置文本扫描的候选文件数上限：达到后停止配置发现，返回当前安全结果。</summary>
    private const int MaxConfigScanFiles = 2000;

    /// <summary>Profile 配置文本扫描的累计读取字节上限：达到后停止配置发现，返回当前安全结果。</summary>
    private const long MaxConfigScanTotalBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Profile 配置中的插件注册字段：YAML/JSON 中 name/plugin/package 键（可选引号，键引号必须成对）
    /// 的合法 npm 包名值（可选引号，值引号必须成对）。普通引号字符串、注释、字段值一律不作为包名。
    /// </summary>
    private static readonly Regex RegisteredPackagePattern = new(
        @"(?:^|[\s{,-])([""']?)(?:name|plugin|package)\1\s*:\s*([""']?)((?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*)\2",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// YAML 明确插件列表块键：顶层或任意嵌套的 plugins/packages 字段（键后仅空白）。
    /// plugin-list、dependencies、bundles 等其余键名天然不匹配，不会被当作块键。
    /// </summary>
    private static readonly Regex YamlPackageListKeyPattern = new(
        @"^(\s*)(?:plugins|packages)(\s*):\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>YAML 列表项：以 "-" 开头且其后有空白（"-foo" 是普通标量，不算列表项）。</summary>
    private static readonly Regex YamlPackageListItemPattern = new(
        @"^\s*-\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>YAML 行注释剥离（# 前必须是行首或空白，避免误伤 URL fragment）。</summary>
    private static readonly Regex YamlCommentPattern = new(
        @"(?:^|\s)#[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>JS/MJS/CJS 明确静态 import/export-from 的模块标识符（不匹配动态 import() 与 require.resolve）。</summary>
    private static readonly Regex JsImportPattern = new(
        @"\b(?:import|export)\s+(?:(?:[^;""']*?\s+)?from\s+)?[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>JS/MJS/CJS 明确静态 require 调用的模块标识符。</summary>
    private static readonly Regex JsRequirePattern = new(
        @"\brequire\s*\(\s*[""']([^""']+)[""']\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>JS 注释剥离（行注释与块注释），防止从注释中提取包名。</summary>
    private static readonly Regex JsCommentPattern = new(
        @"//[^\r\n]*|/\*.*?\*/",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly string home;

    public DshExtensionBackupService(string? configuredHome = null)
    {
        var resolved = configuredHome;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            var envHome = Environment.GetEnvironmentVariable("DSH_HOME");
            resolved = string.IsNullOrWhiteSpace(envHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
                : envHome;
        }
        home = Path.GetFullPath(resolved.Trim());
    }

    /// <summary>当前设备 DSH Home 根目录（规范化后的绝对路径）。</summary>
    public string DshHome => home;

    /// <summary>DSH 是否已安装（Home 目录存在）。未安装时一键备份不添加任何 DSH 数据源。</summary>
    public bool IsInstalled => Directory.Exists(home);

    /// <summary>
    /// 发现当前 DSH Home 的可备份数据源。DSH 未安装或目录不存在时返回空列表，
    /// 不抛异常；单个插件损坏/无 package.json 时跳过，不拖垮整个发现。
    /// </summary>
    public IReadOnlyList<BackupSource> DiscoverSources()
    {
        var result = new List<BackupSource>();
        if (!IsInstalled) return result;
        AddDirectorySource(result, SkillsSourceId, "DSH 个人 Skills", Path.Combine(home, "skills"), SkillsExcludedDirectories);
        AddDirectorySource(result, AgentPresetsSourceId, "DSH Agent 预设", Path.Combine(home, ".agent-presets"), AgentPresetsExcludedDirectories);
        AddDirectorySource(result, ProfilesSourceId, "DSH 用户配置", Path.Combine(home, "profiles"), ProfilesExcludedDirectories);
        foreach (var plugin in DiscoverPlugins())
        {
            result.Add(new BackupSource(
                PluginSourceIdPrefix + EncodePackageName(plugin.Name),
                "DSH 插件 " + plugin.Name,
                plugin.Directory,
                AdditionalExcludedDirectoryNames: PluginExcludedDirectories,
                AdditionalExcludedFileNames: TemporaryFileExclusions));
        }
        return result;
    }

    /// <summary>
    /// 从快照 manifest 生成恢复计划：识别 dsh-* 数据源、校验插件名合法性、生成
    /// 数据源→DSH Home 相对路径映射。没有 DSH 数据时抛 InvalidOperationException
    /// （UI 据此给出清晰提示）；非法插件名或映射冲突抛 InvalidDataException 拒绝恢复。
    /// </summary>
    public DshRestorePlan BuildRestorePlan(SnapshotManifest manifest)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        var sources = manifest.Sources
            .Where(source => source.Id.StartsWith(SourceIdPrefix, StringComparison.Ordinal))
            .OrderBy(source => source.Id, StringComparer.Ordinal)
            .ToList();
        if (sources.Count == 0)
            throw new InvalidOperationException("该快照不包含 DSH Skills、Agent 预设、配置或插件数据。请先创建包含 DSH 数据的新快照。");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var plugins = new List<string>();
        foreach (var source in sources)
        {
            var target = ResolveTarget(source.Id, source.DisplayName, plugins);
            if (!map.TryAdd(source.Id, target)) throw new InvalidDataException($"DSH 快照包含重复数据源：{source.Id}");
        }
        if (map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Count)
            throw new InvalidDataException("DSH 数据源映射目标冲突，已拒绝恢复。");

        var kinds = new List<string>();
        if (map.ContainsKey(SkillsSourceId)) kinds.Add("DSH 个人 Skills（skills/）");
        if (map.ContainsKey(AgentPresetsSourceId)) kinds.Add("DSH Agent 预设（.agent-presets/）");
        if (map.ContainsKey(ProfilesSourceId)) kinds.Add("DSH 用户配置（profiles/，不含账号密钥、会话和附件）");
        if (plugins.Count > 0) kinds.Add($"DSH 用户插件（{plugins.Count} 个）：" + string.Join("、", plugins));
        return new DshRestorePlan(map.Keys.ToList(), map, kinds, plugins, home);
    }

    /// <summary>
    /// 恢复目标（当前设备 DSH Home）不能是备份仓库本身、仓库的子目录或仓库的父目录，
    /// 防止恢复写穿备份仓库。备份时 CreateSnapshotAsync 已通过
    /// <see cref="PathSafety.EnsureRepositoryOutsideSources"/> 保证仓库不在 DSH Home 内，
    /// 此处双向校验兜底。
    /// </summary>
    public static void EnsureTargetOutsideRepository(string repositoryRoot, string dshHome)
    {
        var repository = Path.GetFullPath(repositoryRoot);
        var target = Path.GetFullPath(dshHome);
        if (string.Equals(repository, target, StringComparison.OrdinalIgnoreCase)
            || PathSafety.IsWithin(repository, target)
            || PathSafety.IsWithin(target, repository))
            throw new InvalidOperationException("DSH Home 不能是备份仓库本身或其子目录/父目录，已拒绝恢复。");
    }

    /// <summary>恢复计划：源 id 列表、源→DSH Home 相对路径映射、恢复种类文案、插件名与目标 Home。</summary>
    public sealed record DshRestorePlan(
        IReadOnlyList<string> SourceIds,
        IReadOnlyDictionary<string, string> SourceTargetMap,
        IReadOnlyList<string> Kinds,
        IReadOnlyList<string> PluginNames,
        string TargetHome);

    private sealed record PluginCandidate(string Name, string Directory);

    // ---- 插件发现 ----

    /// <summary>
    /// 供其他 DSH 服务（组件扫描等）复用的插件发现：只读的 (名称, 目录) 列表，
    /// 与备份发现共用同一套规则，避免第二套漂移逻辑。
    /// </summary>
    internal IReadOnlyList<PluginPackage> DiscoverPluginPackages() =>
        DiscoverPlugins().Select(candidate => new PluginPackage(candidate.Name, candidate.Directory)).ToList();

    /// <summary>插件包信息（名称 + 包根目录）。</summary>
    internal sealed record PluginPackage(string Name, string Directory);

    /// <summary>
    /// 识别实际插件包：<c>profiles/node_modules</c> 直接一级包（含 scoped 二级）中带 package.json 的
    /// 目录，加上 Profile 配置文本引用的包（必须同样位于该目录内）。每个插件独立成源。
    /// 无 package.json 的目录直接跳过，不影响其他插件。
    /// </summary>
    private IReadOnlyList<PluginCandidate> DiscoverPlugins()
    {
        var pluginsRoot = Path.Combine(home, "profiles", "node_modules");
        if (!Directory.Exists(pluginsRoot)) return Array.Empty<PluginCandidate>();
        var candidates = new Dictionary<string, PluginCandidate>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Profile references are authoritative. Additionally keep unscoped dsh-*
            // packages as a compatibility convention for locally installed plugins.
            // Never treat every top-level dependency as a plugin: doing so effectively
            // backs up the entire node_modules tree package-by-package.
            foreach (var referenced in ScanConfigReferences(pluginsRoot))
            {
                var path = Path.Combine(pluginsRoot, referenced.Replace('/', Path.DirectorySeparatorChar));
                AddPluginCandidate(candidates, referenced, path);
            }
            foreach (var directory in Directory.EnumerateDirectories(pluginsRoot))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith('@') && name.StartsWith("dsh-", StringComparison.OrdinalIgnoreCase))
                    AddPluginCandidate(candidates, name, directory);
            }
        }
        catch
        {
            // 单个插件或目录损坏（权限/锁定/命名异常）只跳过，绝不拖垮整个发现。
        }
        return candidates.Values.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddPluginCandidate(Dictionary<string, PluginCandidate> candidates, string name, string directory)
    {
        if (!IsValidPackageName(name)) return;
        if (!File.Exists(Path.Combine(directory, "package.json"))) return;
        candidates[name] = new PluginCandidate(name, directory);
    }

    /// <summary>
    /// 扫描 profiles/ 下（不含 node_modules）的 YAML/JSON/JS 配置文本，提取插件注册：
    /// YAML/JSON 仅接受 name/plugin/package 键的合法 npm 包名（键/值引号必须成对，YAML 注释剥离），
    /// JS/MJS/CJS 仅接受 Profile 配置目录（所在目录链含 cordis.yml/cordis.patch.yml/package.json 特征）
    /// 内文件的明确静态 import/export-from/require（注释剥离）；任何普通引号字符串、注释、字段值
    /// 均不作为包名。引用的包必须已存在于 profiles/node_modules 直接一级包中（来源路径约束），
    /// 否则忽略。单个文件损坏/锁定/超大时跳过该文件。
    /// </summary>
    private IReadOnlyList<string> ScanConfigReferences(string pluginsRoot)
    {
        var profilesRoot = Path.Combine(home, "profiles");
        if (!Directory.Exists(profilesRoot)) return Array.Empty<string>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".yaml", ".yml", ".json", ".js", ".mjs", ".cjs" };
        var stack = new Stack<string>();
        stack.Push(profilesRoot);
        // 硬预算：候选文件数与累计读取字节任一达到上限即停止配置文本发现，返回当前安全结果，
        // 绝不无限遍历（单文件 4 MB 上限单独保留）。
        var scannedFiles = 0;
        long scannedBytes = 0;
        var budgetExceeded = false;
        while (stack.Count > 0)
        {
            if (scannedFiles >= MaxConfigScanFiles || scannedBytes >= MaxConfigScanTotalBytes)
            {
                budgetExceeded = true;
                break;
            }
            var current = stack.Pop();
            string[] directories;
            string[] files;
            try
            {
                directories = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch { continue; }
            foreach (var directory in directories)
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (info.Name.StartsWith('.')) continue;
                if (NonConfigDirectoryNames.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(directory);
            }
            foreach (var file in files)
            {
                if (scannedFiles >= MaxConfigScanFiles || scannedBytes >= MaxConfigScanTotalBytes)
                {
                    budgetExceeded = true;
                    break;
                }
                if (!extensions.Contains(Path.GetExtension(file))) continue;
                scannedFiles++;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length <= 0 || info.Length > MaxConfigScanBytes) continue;
                    scannedBytes += info.Length;
                    var text = File.ReadAllText(file, Encoding.UTF8);
                    var extension = Path.GetExtension(file);
                    if (extension is ".js" or ".mjs" or ".cjs")
                    {
                        // JS 仅作为 Profile 配置（配置目录内）时扫描，且只认明确静态 import/require。
                        if (!IsProfileConfigFile(file, profilesRoot)) continue;
                        text = JsCommentPattern.Replace(text, " ");
                        foreach (Match match in JsImportPattern.Matches(text))
                            AddReferenceIfInstalled(referenced, match.Groups[1].Value, pluginsRoot);
                        foreach (Match match in JsRequirePattern.Matches(text))
                            AddReferenceIfInstalled(referenced, match.Groups[1].Value, pluginsRoot);
                    }
                    else
                    {
                        // YAML/JSON：注册字段（name/plugin/package）保持；YAML 再识别明确的
                        // plugins:/packages: 列表块（plugin-list/dependencies/bundles/普通
                        // 数组、映射项与任意引号字段值一律不识别）。
                        if (extension is ".yaml" or ".yml")
                        {
                            text = YamlCommentPattern.Replace(text, " ");
                            foreach (var candidate in ScanYamlPackageListBlocks(text))
                                AddReferenceIfInstalled(referenced, candidate, pluginsRoot);
                        }
                        foreach (Match match in RegisteredPackagePattern.Matches(text))
                            AddReferenceIfInstalled(referenced, match.Groups[3].Value, pluginsRoot);
                    }
                }
                catch { }
            }
            if (budgetExceeded) break;
        }
        return referenced.ToList();
    }

    /// <summary>
    /// 扫描 YAML 明确的 plugins:/packages: 列表块：块键可为顶层或任意嵌套的 plugins/packages
    /// 字段（键后仅空白；plugin-list、dependencies、bundles 等其余键名不算块键）；块内只接受
    /// 缩进比键行更深、以 "- " 开头且整个标量（可整体加单/双引号）为合法 npm 包名的项；
    /// 遇到同级或更浅缩进的行即结束当前块。普通数组、映射项（如 "- plugin: x"）、字段值中的
    /// 任意引号字符串、"-foo" 普通标量均不作为列表项。
    /// </summary>
    private static IEnumerable<string> ScanYamlPackageListBlocks(string yamlText)
    {
        var found = new List<string>();
        var blockIndent = 0;
        var inBlock = false;
        foreach (var rawLine in yamlText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.Trim().Length == 0) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (!inBlock)
            {
                if (YamlPackageListKeyPattern.IsMatch(line))
                {
                    blockIndent = indent;
                    inBlock = true;
                }
                continue;
            }
            if (indent <= blockIndent)
            {
                // 同级/更浅缩进的新字段：结束当前块；该行可能是下一个块键。
                inBlock = false;
                if (YamlPackageListKeyPattern.IsMatch(line))
                {
                    blockIndent = indent;
                    inBlock = true;
                }
                continue;
            }
            var item = YamlPackageListItemPattern.Match(line);
            if (!item.Success) continue;
            var scalar = item.Groups[1].Value.Trim();
            if (scalar.Length >= 2 && scalar[0] == '\'' && scalar[^1] == '\'')
                scalar = scalar[1..^1];
            else if (scalar.Length >= 2 && scalar[0] == '"' && scalar[^1] == '"')
                scalar = scalar[1..^1];
            if (IsValidPackageName(scalar)) found.Add(scalar);
        }
        return found;
    }

    /// <summary>引用的包必须已实际安装在 profiles/node_modules（一级包）中，否则忽略。</summary>
    private static void AddReferenceIfInstalled(HashSet<string> referenced, string candidate, string pluginsRoot)
    {
        if (!IsValidPackageName(candidate)) return;
        var path = Path.Combine(pluginsRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(Path.Combine(path, "package.json"))) referenced.Add(candidate);
    }

    /// <summary>
    /// 判断 JS 文件是否属于 Profile 配置：所在目录链（向上至 profiles 根）存在
    /// cordis.yml、cordis.patch.yml 或 package.json 特征文件。散落的测试/工具脚本不算配置。
    /// </summary>
    private static bool IsProfileConfigFile(string filePath, string profilesRoot)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        while (true)
        {
            if (File.Exists(Path.Combine(directory, "cordis.yml"))
                || File.Exists(Path.Combine(directory, "cordis.patch.yml"))
                || File.Exists(Path.Combine(directory, "package.json")))
                return true;
            if (string.Equals(directory, profilesRoot, StringComparison.OrdinalIgnoreCase)) return false;
            var parent = Path.GetDirectoryName(directory);
            if (parent is null) return false;
            directory = parent;
        }
    }

    // ---- 数据源→目标映射 ----

    private static string ResolveTarget(string sourceId, string displayName, List<string> plugins)
    {
        if (sourceId == SkillsSourceId) return "skills";
        if (sourceId == AgentPresetsSourceId) return ".agent-presets";
        if (sourceId == ProfilesSourceId) return "profiles";
        if (sourceId.StartsWith(PluginSourceIdPrefix, StringComparison.Ordinal))
        {
            var packageName = DecodePackageName(sourceId[PluginSourceIdPrefix.Length..]);
            if (!IsValidPackageName(packageName))
                throw new InvalidDataException($"DSH 插件名非法，已拒绝恢复：{displayName}");
            plugins.Add(packageName);
            return Path.Combine(PluginsRelativeDirectory, packageName.Replace('/', Path.DirectorySeparatorChar));
        }
        throw new InvalidDataException($"未知的 DSH 数据源：{sourceId}");
    }

    // ---- 包名编解码与校验（npm 命名规则近似） ----

    private static string EncodePackageName(string name) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(name)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodePackageName(string encoded)
    {
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("DSH 插件标识损坏，已拒绝恢复。", ex);
        }
    }

    internal static bool IsValidPackageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 214) return false;
        if (name.StartsWith('@'))
        {
            var parts = name.Split('/');
            return parts.Length == 2 && IsValidPackageSegment(parts[0][1..]) && IsValidPackageSegment(parts[1]);
        }
        return IsValidPackageSegment(name);
    }

    private static bool IsValidPackageSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > 214) return false;
        if (segment[0] is '.' or '_') return false;
        return segment.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '-' or '_');
    }

    private static void AddDirectorySource(List<BackupSource> result, string id, string displayName, string path, IReadOnlyList<string>? excludedDirectories)
    {
        if (!Directory.Exists(path)) return;
        result.Add(new BackupSource(
            id,
            displayName,
            path,
            AdditionalExcludedDirectoryNames: excludedDirectories,
            AdditionalExcludedFileNames: TemporaryFileExclusions));
    }
}
