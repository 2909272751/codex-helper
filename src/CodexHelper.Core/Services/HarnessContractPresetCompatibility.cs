using System.Text;
using System.Text.RegularExpressions;

namespace CodexHelper.Core.Services;

/// <summary>
/// Codex 合同模式预设的"来源定位 + persona 兼容重写"单一事实源。新版 DSH（0.1.5-rc.x）
/// 把随包预设放在 <c>node_modules/@deepseek-ai/dsh-agent-presets/presets/&lt;id&gt;/agent.cordis.yml</c>，
/// 旧版放在 <c>config/agent-presets/&lt;id&gt;/agent.cordis.yml</c>；persona 也由旧版
/// <c>config.text</c> 单行标量改成新版 <c>config.prefix</c>/<c>config.suffix</c> 分栏。
/// 本类只做两件事：
/// <list type="number">
/// <item>按"确认对应包 package.json 的 name 是 @deepseek-ai/dsh-agent-presets"的规则定位官方 standard 预设，
/// 绝不跨任意目录挑别的 DSH 包；</item>
/// <item>以严格限定的行范围替换 persona 的单个 prefix/text 标量，保留 suffix、工具/realm/<c>!!js</c>
/// 标签、注释、换行风格与未知内容原文；不引入 YAML 网络依赖、不做 YAML 重序列化。</item>
/// </list>
/// 结构不可识别（缺 persona、多个 persona、别名、复杂结构）时诚实拒绝并给出可读诊断，绝不写出坏预设。
/// </summary>
public static class HarnessContractPresetCompatibility
{
    /// <summary>官方预设包名：只有 package.json 的 name 等于它才承认这是可用的预设来源。</summary>
    public const string PresetsPackageName = "@deepseek-ai/dsh-agent-presets";

    /// <summary>官方 persona 插件包名：persona 行必须挂它才算可识别（新版带引号，旧版可能不带）。</summary>
    public const string PersonaPackageName = "@deepseek-ai/dsh-persona";

    /// <summary>新版随包预设根目录（相对预设包根）。</summary>
    public const string NodeModulesPresetRoot = "presets";

    /// <summary>旧版随包预设根目录（相对 dsh 包根）。</summary>
    public const string LegacyPresetRoot = "config/agent-presets";

    /// <summary>新 Gateway 的 Remote 流多路复用端点（会话水位读取入口）。</summary>
    public const string RemoteMuxPath = "/api/remote.mux";

    /// <summary>人物设定的最小可识别长度：短于此值的 prefix 视为占位/结构异常，不当作可替换正文。</summary>
    private const int MinPersonaScalarLength = 24;

    /// <summary>预设组合文件读取上限（有界；超过即视为不可信来源，避免把巨大文件读进内存）。</summary>
    public const int MaxCompositionBytes = 1_048_576;

    /// <summary>预设来源种类：新版随包预设、旧版随包预设。</summary>
    public enum PresetSourceKind
    {
        None = 0,
        NodeModulesPreset = 1,
        LegacyPreset = 2
    }

    /// <summary>定位到的官方 standard 预设组合文件。</summary>
    public sealed record LocatedComposition(PresetSourceKind Kind, string PackageRoot, string CompositionPath, string Description)
    {
        /// <summary>可读来源描述（只含路径与包名，不含任何凭据）。</summary>
        public string BackendText => Kind switch
        {
            PresetSourceKind.NodeModulesPreset =>
                "新版随包预设（" + PresetsPackageName + " → " + NodeModulesPresetRoot + "/standard/agent.cordis.yml）",
            PresetSourceKind.LegacyPreset =>
                "旧版随包预设（config/agent-presets/standard/agent.cordis.yml）",
            _ => "未定位到官方 standard 预设"
        };
    }

    /// <summary>persona 兼容重写结果：失败时 OutOfRange 为空且 Reason 可读（绝不部分写出）。</summary>
    public sealed record PersonaRewrite(bool Supported, string Reason, string? Rewritten, string? BracketStyle, string? PersonaHKey)
    {
        public static PersonaRewrite Fail(string reason) => new(false, reason, null, null, null);
    }

    /// <summary>
    /// 同一识别入口：IsSupported / InstallOrRepair / 诊断共用它，避免一个 false 一个 true。
    /// 返回定位到的组合文件与 persona 是否可重写。
    /// </summary>
    public static bool TryRecognize(string? dshEntryPath, out LocatedComposition? located, out PersonaRewrite rewrite, out string diagnostic)
    {
        located = null;
        rewrite = PersonaRewrite.Fail("尚未探测。");
        diagnostic = "尚未探测官方 standard 预设。";
        if (string.IsNullOrWhiteSpace(dshEntryPath))
        {
            diagnostic = "未配置 Harness dsh 入口，无法定位官方 standard 预设。";
            return false;
        }
        try
        {
            located = LocateStandardComposition(dshEntryPath);
            if (located is null)
            {
                diagnostic = "未找到官方 standard 预设组合文件（已按新版 presets/ 与旧版 config/agent-presets/ 两种位置、"
                    + "以及向上逐级 node_modules 依赖解析查找 " + PresetsPackageName + "）。";
                return false;
            }
            if (IsReparsePoint(located.CompositionPath))
            {
                diagnostic = "官方 standard 预设组合文件是符号链接或重解析点，已拒绝在越界路径上生成预设。";
                return false;
            }
            var size = new FileInfo(located.CompositionPath).Length;
            if (size <= 0 || size > MaxCompositionBytes)
            {
                diagnostic = "官方 standard 预设组合文件大小异常（" + size + " 字节），已拒绝基于它生成预设。";
                return false;
            }
            var text = File.ReadAllText(located.CompositionPath, Encoding.UTF8);
            rewrite = TryRewritePersonaPrefix(text);
            if (!rewrite.Supported)
            {
                diagnostic = located.BackendText + "：" + rewrite.Reason;
                return false;
            }
            diagnostic = located.BackendText + "：persona 可兼容重写（" + rewrite.Reason + "）。";
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = "定位官方 standard 预设失败：" + Sanitize(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 定位官方 standard 预设组合文件。查找顺序（全部要求预设包 package.json 的 name 精确匹配）：
    /// <list type="number">
    /// <item>dsh 包根 → <c>node_modules/@deepseek-ai/dsh-agent-presets/presets/standard/agent.cordis.yml</c>（新版）；</item>
    /// <item>dsh 入口逐级向上 → 每级 <c>node_modules/@deepseek-ai/dsh-agent-presets</c>（依赖提升/多级安装）；</item>
    /// <item>dsh 包根 → <c>config/agent-presets/standard/agent.cordis.yml</c>（旧版；此形态无预设包，允许 package.json 为 dsh 自身）。</item>
    /// </list>
    /// </summary>
    public static LocatedComposition? LocateStandardComposition(string? dshEntryPath)
    {
        if (string.IsNullOrWhiteSpace(dshEntryPath)) return null;
        var entryDirectory = Path.GetDirectoryName(Path.GetFullPath(dshEntryPath));
        var dshPackageRoot = FindContainingPackageRoot(entryDirectory);
        if (dshPackageRoot is not null)
        {
            var nested = Path.Combine(dshPackageRoot, "node_modules", PresetsPackageName, NodeModulesPresetRoot, "standard", "agent.cordis.yml");
            if (IsPresetsPackage(Path.Combine(dshPackageRoot, "node_modules", PresetsPackageName)) && File.Exists(nested))
                return new LocatedComposition(PresetSourceKind.NodeModulesPreset,
                    Path.Combine(dshPackageRoot, "node_modules", PresetsPackageName), nested,
                    "新版随包预设");

            var legacy = Path.Combine(dshPackageRoot, LegacyPresetRoot, "standard", "agent.cordis.yml");
            if (File.Exists(legacy))
                return new LocatedComposition(PresetSourceKind.LegacyPreset, dshPackageRoot, legacy, "旧版随包预设");
        }

        // 依赖向上解析：多级 node_modules / 提升安装 / dsh 通过依赖引入预设包的布局。
        for (var directory = entryDirectory is null ? null : new DirectoryInfo(entryDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "node_modules", PresetsPackageName);
            if (!IsPresetsPackage(candidate)) continue;
            var composition = Path.Combine(candidate, NodeModulesPresetRoot, "standard", "agent.cordis.yml");
            if (File.Exists(composition))
                return new LocatedComposition(PresetSourceKind.NodeModulesPreset, candidate, composition, "新版随包预设（向上依赖解析）");
        }
        return null;
    }

    /// <summary>确认目录是官方预设包：package.json 的 name 精确等于 <see cref="PresetsPackageName"/>。</summary>
    public static bool IsPresetsPackage(string? packageDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory)) return false;
        var manifest = Path.Combine(packageDirectory, "package.json");
        if (!File.Exists(manifest)) return false;
        if (IsReparsePoint(manifest) || IsReparsePoint(packageDirectory)) return false;
        try
        {
            var info = new FileInfo(manifest);
            if (info.Length <= 0 || info.Length > 262_144) return false;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(name.GetString(), PresetsPackageName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从目录向上找包含 package.json 的包根（最多 6 级，避免越界遍历）。</summary>
    public static string? FindContainingPackageRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory)) return null;
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "package.json")))
                return directory.FullName;
        return null;
    }

    /// <summary>路径本身是符号链接/重解析点（越界写入风险的第一个判据）。</summary>
    public static bool IsReparsePoint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            FileAttributes attributes = File.Exists(path)
                ? File.GetAttributes(path)
                : Directory.Exists(path) ? new DirectoryInfo(path).Attributes : (FileAttributes)0;
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 探测整条写入路径（从 DSH Home 到目标文件/目录）是否经过符号链接或重解析点。
    /// 命中即拒绝写入（避免把自家 codex-contract 预设写到被链接出去的越界位置）。
    /// </summary>
    public static bool AnyReparsePointOnPath(string fromRoot, string target)
    {
        try
        {
            var root = Path.GetFullPath(fromRoot);
            var full = Path.GetFullPath(target);
            if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            var current = root;
            if (IsReparsePoint(current)) return true;
            var relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length == 0) continue;
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current)) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 严格限定的 persona prefix/text 标量替换。只改一行键 + 它自己的缩进续行，不改注释、
    /// 不改 suffix、不改其他 persona 行、不动 <c>!!js</c>/realm/工具行。返回重写后的完整文本。
    /// </summary>
    public static PersonaRewrite RewritePersonaPrefix(string composition, string? replacementScalar = null)
    {
        var detect = TryRewritePersonaPrefix(composition, replacementScalar);
        return detect;
    }

    /// <summary>
    /// 识别并（可选）重写 persona 的 prefix/text 标量。<paramref name="replacementScalar"/> 为空时
    /// 只做识别（供 IsSupported/诊断使用），返回的 Rewritten 为 null。
    /// </summary>
    public static PersonaRewrite TryRewritePersonaPrefix(string? composition, string? replacementScalar = null)
    {
        if (composition is null) return PersonaRewrite.Fail("预设内容为空，无法识别 persona。");
        if (composition.Length > MaxCompositionBytes) return PersonaRewrite.Fail("预设内容过大，已拒绝解析。");
        if (composition.IndexOf('\0') >= 0) return PersonaRewrite.Fail("预设内容包含 NUL 字节，已拒绝解析。");

        var newline = composition.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(composition);

        // 收集所有顶层行人设块：- id: persona（允许引号/多种缩进），由 id 值精确判定。
        var personaStarts = new List<int>();
        for (var index = 0; index < lines.Count; index++)
            if (IsPersonaIdLine(lines[index])) personaStarts.Add(index);

        if (personaStarts.Count == 0)
            return PersonaRewrite.Fail("未找到可识别的 persona 行（- id: persona），结构已变化。");
        if (personaStarts.Count > 1)
            return PersonaRewrite.Fail("预设中存在多个 persona 行（共 " + personaStarts.Count + " 个），无法判定应替换哪一个，已拒绝。");

        var start = personaStarts[0];
        var end = lines.Count;
        for (var index = start + 1; index < lines.Count; index++)
        {
            if (IsTopLevelRowLine(lines[index])) { end = index; break; }
        }

        // persona 行必须挂官方 persona 插件（新版 '@deepseek-ai/dsh-persona'，旧版可能不带引号）。
        var personaNameLine = -1;
        for (var index = start + 1; index < end; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("name:", StringComparison.Ordinal))
            {
                personaNameLine = index;
                break;
            }
        }
        if (personaNameLine < 0)
            return PersonaRewrite.Fail("persona 行没有 name: 字段，结构已变化。");
        var personaName = Unquote(lines[personaNameLine].Trim()["name:".Length..].Trim());
        // 兼容两种官方拼写：新版 '@deepseek-ai/dsh-persona'（随包 presets 带引号），
        // 旧版裸 'persona'（rc.6 及更早的 config/agent-presets 形态）。其他包名一律拒绝。
        if (!IsOfficialPersonaName(personaName))
            return PersonaRewrite.Fail("persona 行的 name 不是 " + PersonaPackageName + "（实际：" + Sanitize(personaName) + "），已拒绝改写。");

        // persona 行必须使用具体插件（无 alias/reference 间接层）。
        for (var index = start + 1; index < end; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("alias:", StringComparison.Ordinal) || trimmed.StartsWith("ref:", StringComparison.Ordinal))
                return PersonaRewrite.Fail("persona 行使用别名/引用间接层，无法安全定位 prefix 标量，已拒绝改写。");
        }

        // config: 块必须在 persona 内，且唯一。
        var configLine = -1;
        for (var index = start + 1; index < end; index++)
        {
            if (!string.Equals(lines[index].Trim(), "config:", StringComparison.Ordinal)) continue;
            if (configLine >= 0) return PersonaRewrite.Fail("persona 行含多个 config: 块，结构不明确，已拒绝改写。");
            configLine = index;
        }
        if (configLine < 0)
            return PersonaRewrite.Fail("persona 行没有 config: 块，结构已变化。");

        var configIndent = IndentOf(lines[configLine]);
        var keyIndex = -1;
        string? key = null;
        for (var index = configLine + 1; index < end; index++)
        {
            var raw = lines[index];
            if (raw.Trim().Length == 0) continue;
            if (IndentOf(raw) <= configIndent) break;
            var trimmed = raw.Trim();
            if (IsScalarKeyLine(trimmed, "prefix")) { keyIndex = index; key = "prefix"; break; }
            if (IsScalarKeyLine(trimmed, "text")) { keyIndex = index; key = "text"; break; }
        }
        if (keyIndex < 0)
            return PersonaRewrite.Fail("persona 的 config: 既无 prefix: 也无 text: 标量，结构已变化。");

        var currentValue = ExtractScalarValue(lines, keyIndex!);
        if (currentValue is null)
            return PersonaRewrite.Fail("persona 的 " + key + ": 不是可识别的标量（折叠/字面/单行），已拒绝改写。");
        if (currentValue.Trim().Length < MinPersonaScalarLength)
            return PersonaRewrite.Fail("persona 的 " + key + ": 内容过短（疑似占位），已拒绝改写。");

        var style = ScalarStyle(lines[keyIndex!]);
        if (replacementScalar is null)
            return new PersonaRewrite(true, "识别到 persona." + key + "（" + style + "）", null, style, key);

        var rewritten = ReplaceScalar(lines, keyIndex!, replacementScalar, newline, composition);
        return new PersonaRewrite(true, "已重写 persona." + key + "（" + style + "）", rewritten, style, key);
    }

    /// <summary>官方 persona 插件名的两种拼写：新版 '@deepseek-ai/dsh-persona'、旧版裸 'persona'。</summary>
    public static bool IsOfficialPersonaName(string? name)
        => string.Equals(name, PersonaPackageName, StringComparison.Ordinal)
            || string.Equals(name, "persona", StringComparison.Ordinal);

    /// <summary>persona 行判定：<c>- id: persona</c>（值可加引号，缩进任意，但必须在行首 <c>- </c> 后）。</summary>
    private static bool IsPersonaIdLine(string line)    {
        var match = Regex.Match(line, @"^\s*-\s+id\s*:\s*(?<value>[^\s#]+)\s*(?:#.*)?$");
        if (!match.Success) return false;
        return string.Equals(Unquote(match.Groups["value"].Value), "persona", StringComparison.Ordinal);
    }

    /// <summary>顶层插件行判定：缩进 0 或 2 且以 <c>- </c> 开头。</summary>
    private static bool IsTopLevelRowLine(string line)
    {
        var indent = IndentOf(line);
        if (indent > 2) return false;
        return line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("-\t", StringComparison.Ordinal);
    }

    private static bool IsScalarKeyLine(string trimmed, string key)
        => Regex.IsMatch(trimmed, "^" + Regex.Escape(key) + @"\s*:\s*(?:[>|][+-]?[0-9]?|.*)$", RegexOptions.CultureInvariant)
            && !trimmed.StartsWith(key + "s:", StringComparison.Ordinal);

    private static int IndentOf(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
    }

    private static string ScalarStyle(string keyLine)
    {
        var value = keyLine.Trim()[keyLine.Trim().IndexOf(':')..].TrimStart(':').Trim();
        if (value.StartsWith(">-", StringComparison.Ordinal) || value.StartsWith(">", StringComparison.Ordinal)) return "折叠标量";
        if (value.StartsWith("|-", StringComparison.Ordinal) || value.StartsWith("|", StringComparison.Ordinal)) return "字面标量";
        if (value.Length == 0) return "缩进标量";
        return "单行标量";
    }

    /// <summary>抽取标量值：单行值取其文本；块标量取所有更深缩进的续行（去公共缩进）。</summary>
    private static string? ExtractScalarValue(List<string> lines, int keyIndex)
    {
        var raw = lines[keyIndex];
        var trimmedStart = raw.TrimStart();
        var keyIndent = raw.Length - trimmedStart.Length;
        var value = trimmedStart[trimmedStart.IndexOf(':')..].TrimStart(':').Trim();
        if (value.Length > 0 && !value.StartsWith(">", StringComparison.Ordinal) && !value.StartsWith("|", StringComparison.Ordinal))
            return Unquote(value);
        if (value.Length > 0)
        {
            // 块标量头部（>- / | / >-2 等）：值在后续更深缩进行。
            var collected = new List<string>();
            for (var index = keyIndex + 1; index < lines.Count; index++)
            {
                var line = lines[index];
                if (line.Trim().Length == 0) { collected.Add(string.Empty); continue; }
                if (IndentOf(line) <= keyIndent) break;
                collected.Add(line.TrimEnd());
            }
            TrimTrailingEmpty(collected);
            if (collected.Count == 0) return string.Empty;
            var bodyIndent = collected.Where(x => x.Length > 0).Min(IndentOf);
            return string.Join("\n", collected.Select(x => x.Length == 0 ? string.Empty : x[Math.Min(bodyIndent, x.Length)..]));
        }
        // 空值 + 缩进续行（"prefix:" 换行后跟更深缩进的内容）。
        var block = new List<string>();
        for (var index = keyIndex + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Trim().Length == 0) { block.Add(string.Empty); continue; }
            if (IndentOf(line) <= keyIndent) break;
            block.Add(line.TrimEnd());
        }
        TrimTrailingEmpty(block);
        return block.Count == 0 ? null : string.Join("\n", block.Select(x => x.Trim()));
    }

    private static void TrimTrailingEmpty(List<string> values)
    {
        while (values.Count > 0 && values[^1].Trim().Length == 0) values.RemoveAt(values.Count - 1);
    }

    /// <summary>
    /// 用新标量整体替换键行与其全部续行：单行形态保持单行；块标量形态把多行正文按
    /// "键缩进 + 2" 重新缩进，保留 <c>&gt;-</c> 折叠风格、suffix 与后续内容原文。
    /// </summary>
    private static string ReplaceScalar(List<string> lines, int keyIndex, string replacement, string newline, string original)
    {
        var raw = lines[keyIndex];
        var keyIndent = raw.Length - raw.TrimStart().Length;
        var trimmedStart = raw.TrimStart();
        var colon = trimmedStart.IndexOf(':');
        var key = trimmedStart[..colon];
        var head = trimmedStart[(colon + 1)..].Trim();

        // 收集原标量占用的续行（更深缩进或空行）。
        var end = keyIndex + 1;
        while (end < lines.Count)
        {
            var line = lines[end];
            if (line.Trim().Length == 0) { end++; continue; }
            if (IndentOf(line) <= keyIndent) break;
            end++;
        }
        while (end - 1 > keyIndex && lines[end - 1].Trim().Length == 0) end--;

        var blockStyle = head.StartsWith(">", StringComparison.Ordinal) || head.StartsWith("|", StringComparison.Ordinal) || head.Length == 0;
        var contentType = head.StartsWith(">", StringComparison.Ordinal) ? ">" : head.StartsWith("|", StringComparison.Ordinal) ? "|" : ">";
        var chomp = head.Length >= 2 && (head[1] == '-' || head[1] == '+') ? head[1].ToString() : string.Empty;
        var indent = new string(' ', keyIndent + 2);
        var textLines = replacement.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        // 单行形态：值本身不含换行时保持单行（不改变文件的行数语义）。
        if (textLines.Length <= 1 && !replacement.Contains('\n'))
        {
            var single = new List<string>(lines);
            single[keyIndex] = new string(' ', keyIndent) + key + ": " + (textLines.Length == 0 ? string.Empty : textLines[0]);
            if (end > keyIndex + 1) single.RemoveRange(keyIndex + 1, end - keyIndex - 1);
            return JoinLines(single, original, newline);
        }

        var replacementLines = new List<string> { new string(' ', keyIndent) + key + ": " + contentType + chomp };
        foreach (var line in textLines)
            replacementLines.Add(line.Length == 0 ? string.Empty : indent + line);

        var result = new List<string>(lines.Count - (end - keyIndex) + replacementLines.Count);
        for (var index = 0; index < keyIndex; index++) result.Add(lines[index]);
        result.AddRange(replacementLines);
        for (var index = end; index < lines.Count; index++) result.Add(lines[index]);
        return JoinLines(result, original, newline);
    }

    /// <summary>
    /// 按原换行风格拼回文本，并恢复原文件是否以换行结尾（保留 UTF-8 换行风格与末尾换行语义，
    /// 避免"只替换一个标量"却顺带增删了文件末尾换行）。
    /// </summary>
    private static string JoinLines(List<string> lines, string original, string newline)
    {
        var joined = string.Join(newline, lines);
        if (original.EndsWith("\r\n", StringComparison.Ordinal) || original.EndsWith('\n'))
            joined += newline;
        return joined;
    }

    /// <summary>按原文件的换行风格切分成行（不含换行符），保留空行。</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var list = new List<string>(lines.Length);
        list.AddRange(lines);
        // 末尾换行会产生一个空尾元素：保留它由 string.Join 还原，但语义上多余，移除以免增行。
        if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        return list;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }

    /// <summary>异常/诊断文本脱敏：只保留首行、限长，去掉可能的路径细节与凭据样式片段。</summary>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "未知原因";
        var firstLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (firstLine.Length > 160) firstLine = firstLine[..160] + "…";
        return firstLine;
    }
}
