using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// helper-setup.json v1 的严格解析器、校验器与受信侧车。
/// 校验规则：必填字段、合法枚举、setup 与字段一致性、重复 field id、secret 无默认值且 export=false、
/// 拒绝命令/脚本/request body、拒绝密钥明文、path 字段禁止绝对路径、超大声明、插件侧车包绑定。
/// 静态扫描只产生“可能需要设置”的提示，绝不自动生成 secret 字段或 required 分类（见 DshComponentScanner）。
/// 任何校验失败抛 InvalidDataException（消息为中文），调用方不得应用声明字段。
/// </summary>
public sealed class HelperSetupManifestService
{
    /// <summary>声明文件大小上限：超大声明一律拒绝。</summary>
    public const long MaxDeclarationBytes = 256 * 1024;

    private const string FingerprintPrefix = "sha256:";

    // 拒绝命令/脚本/request body 关键词（大小写不敏感，命中即拒绝声明）。
    private static readonly string[] ExecutableKeywords =
    [
        "powershell", "pwsh", "cmd.exe", "bash -c", "sh -c", "exec(", "eval(",
        "child_process", "spawn(", "subprocess", "request body", "requestBody", "curl ", "wget "
    ];

    // 拒绝明文密钥模式：声明不得携带任何真实密钥。
    private static readonly System.Text.RegularExpressions.Regex SecretPattern = new(
        "\\b(sk-[A-Za-z0-9]{16,}|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})\\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex VersionPattern = new(
        @"^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // ---- 解析与校验 ----

    /// <summary>解析并严格校验 helper-setup.json 文本；失败抛 InvalidDataException。</summary>
    public HelperSetupDeclaration Parse(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        if (json.Length > MaxDeclarationBytes)
            throw new InvalidDataException($"声明文件超过安全上限（{MaxDeclarationBytes / 1024} KB）。");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        return ParseDocument(document.RootElement);
    }

    /// <summary>从文件读取并校验声明；文件不存在抛 FileNotFoundException，超限/损坏抛 InvalidDataException。</summary>
    public HelperSetupDeclaration ParseFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("声明文件不存在。", path);
        if (info.Length > MaxDeclarationBytes)
            throw new InvalidDataException($"声明文件超过安全上限（{MaxDeclarationBytes / 1024} KB）：{path}");
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    private static HelperSetupDeclaration ParseDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("声明必须是 JSON 对象。");

        var declaration = new HelperSetupDeclaration();
        var fieldIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "schemaVersion":
                    if (property.Value.ValueKind != JsonValueKind.Number || property.Value.GetInt32() != 1)
                        throw new InvalidDataException("声明 schemaVersion 必须为 1。");
                    break;
                case "componentId":
                    declaration.ComponentId = RequireNonEmpty(property, "componentId");
                    break;
                case "displayName":
                    declaration.DisplayName = RequireNonEmpty(property, "displayName");
                    break;
                case "componentType":
                    declaration.ComponentType = RequireNonEmpty(property, "componentType");
                    break;
                case "setup":
                    declaration.Setup = RequireNonEmpty(property, "setup");
                    break;
                case "package":
                    declaration.Package = ParsePackage(property.Value);
                    break;
                case "evidence":
                    declaration.Evidence = ParseStringList(property, "evidence", maxItems: 32);
                    break;
                case "fields":
                    declaration.Fields = ParseFields(property.Value, fieldIds);
                    break;
                case "validation":
                    declaration.Validation = ParseValidation(property.Value);
                    break;
                default:
                    throw new InvalidDataException($"声明包含未知字段：{property.Name}");
            }
        }

        ValidateDeclaration(declaration, fieldIds);
        return declaration;
    }

    private static HelperSetupPackageBinding ParsePackage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("package 必须是对象。");
        var binding = new HelperSetupPackageBinding();
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "name":
                    binding.Name = RequireNonEmpty(property, "package.name");
                    break;
                case "versionRange":
                    binding.VersionRange = RequireNonEmpty(property, "package.versionRange");
                    break;
                case "fingerprint":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("package.fingerprint 必须是字符串。");
                    binding.Fingerprint = property.Value.GetString();
                    break;
                default:
                    throw new InvalidDataException($"package 包含未知字段：{property.Name}");
            }
        }
        if (string.IsNullOrWhiteSpace(binding.Name)) throw new InvalidDataException("package.name 必填。");
        return binding;
    }

    private static List<string> ParseStringList(JsonProperty property, string name, int maxItems)
    {
        if (property.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{name} 必须是字符串数组。");
        var result = new List<string>();
        foreach (var item in property.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} 只能包含字符串。");
            result.Add(item.GetString()!);
            if (result.Count > maxItems) throw new InvalidDataException($"{name} 条目数量超过上限。");
        }
        return result;
    }

    private static List<HelperSetupField> ParseFields(JsonElement value, HashSet<string> fieldIds)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("fields 必须是数组。");
        var fields = new List<HelperSetupField>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("fields 条目必须是对象。");
            var field = new HelperSetupField();
            foreach (var property in item.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "id":
                        field.Id = RequireNonEmpty(property, "fields[].id");
                        break;
                    case "kind":
                        field.Kind = RequireNonEmpty(property, "fields[].kind");
                        break;
                    case "label":
                        field.Label = RequireNonEmpty(property, "fields[].label");
                        break;
                    case "required":
                        if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                            throw new InvalidDataException($"字段 {field.Id} 的 required 必须是布尔值。");
                        field.Required = property.Value.GetBoolean();
                        break;
                    case "default":
                        field.Default = ReadDefault(property);
                        break;
                    case "export":
                        if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                            throw new InvalidDataException($"字段 {field.Id} 的 export 必须是布尔值。");
                        field.Export = property.Value.GetBoolean();
                        break;
                    case "credentialRef":
                        if (property.Value.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException($"字段 {field.Id} 的 credentialRef 必须是字符串。");
                        field.CredentialRef = property.Value.GetString();
                        break;
                    case "dependsOn":
                        if (property.Value.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException($"字段 {field.Id} 的 dependsOn 必须是字符串。");
                        field.DependsOn = property.Value.GetString();
                        break;
                    case "choices":
                        if (property.Value.ValueKind != JsonValueKind.Array)
                            throw new InvalidDataException($"字段 {field.Id} 的 choices 必须是字符串数组。");
                        foreach (var choice in property.Value.EnumerateArray())
                        {
                            if (choice.ValueKind != JsonValueKind.String) throw new InvalidDataException($"字段 {field.Id} 的 choices 只能包含字符串。");
                            field.Choices.Add(choice.GetString()!);
                        }
                        break;
                    default:
                        throw new InvalidDataException($"fields 条目包含未知字段：{property.Name}");
                }
            }
            if (string.IsNullOrWhiteSpace(field.Id)) throw new InvalidDataException("fields 条目缺少 id。");
            if (!fieldIds.Add(field.Id)) throw new InvalidDataException($"声明包含重复字段 id：{field.Id}");
            fields.Add(field);
        }
        return fields;
    }

    private static string? ReadDefault(JsonProperty property)
    {
        var value = property.Value;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) return value.GetBoolean() ? "true" : "false";
        if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        throw new InvalidDataException("default 只能是字符串、布尔值或数字。");
    }

    private static HelperSetupValidation ParseValidation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("validation 必须是对象。");
        var validation = new HelperSetupValidation();
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "protocol":
                    validation.Protocol = RequireNonEmpty(property, "validation.protocol");
                    break;
                case "capabilities":
                    validation.Capabilities = ParseStringList(property, "validation.capabilities", maxItems: 32);
                    break;
                default:
                    throw new InvalidDataException($"validation 包含未知字段：{property.Name}");
            }
        }
        return validation;
    }

    private static void ValidateDeclaration(HelperSetupDeclaration declaration, HashSet<string> fieldIds)
    {
        // 枚举合法性。
        if (!Enum.TryParse<DshComponentType>(declaration.ComponentType, ignoreCase: true, out _))
            throw new InvalidDataException($"非法组件类型：{declaration.ComponentType}（允许 skill/plugin/preset/bridge）。");
        if (!Enum.TryParse<DshSetupState>(declaration.Setup, ignoreCase: true, out _))
            throw new InvalidDataException($"非法的 setup 值：{declaration.Setup}（允许 none/optional/required/unknown）。");
        foreach (var field in declaration.Fields)
        {
            if (!Enum.TryParse<HelperSetupFieldKind>(field.Kind, ignoreCase: true, out _))
                throw new InvalidDataException($"字段 {field.Id} 非法类型：{field.Kind}");
            if (string.IsNullOrWhiteSpace(field.Label))
                throw new InvalidDataException($"字段 {field.Id} 缺少 label。");
            if (field.DependsOn is not null && !fieldIds.Contains(field.DependsOn))
                throw new InvalidDataException($"字段 {field.Id} 的 dependsOn 引用了不存在的字段：{field.DependsOn}");
            if (field.Kind.Equals("choice", StringComparison.OrdinalIgnoreCase) && field.Choices.Count == 0)
                throw new InvalidDataException($"字段 {field.Id} 类型为 choice 时必须提供 choices。");
        }

        // setup 与字段一致性。
        if (declaration.Setup.Equals("none", StringComparison.OrdinalIgnoreCase) && declaration.Fields.Count > 0)
            throw new InvalidDataException("setup 为 none 的组件不得声明配置字段。");
        if (declaration.Setup.Equals("required", StringComparison.OrdinalIgnoreCase)
            && !declaration.Fields.Any(field => field.Required))
            throw new InvalidDataException("setup 为 required 的组件必须至少包含一个必填字段。");

        // secret 字段：无默认值、export=false、可带 credentialRef。
        foreach (var field in declaration.Fields.Where(field => field.Kind.Equals("secret", StringComparison.OrdinalIgnoreCase)))
        {
            if (field.Default is not null)
                throw new InvalidDataException($"secret 字段 {field.Id} 不允许默认值。");
            if (field.Export)
                throw new InvalidDataException($"secret 字段 {field.Id} 必须设置 export=false。");
        }

        // 路径字段禁止绝对路径（不得硬编码他人的绝对路径）。
        foreach (var field in declaration.Fields.Where(field => field.Kind.Equals("path", StringComparison.OrdinalIgnoreCase)))
        {
            if (field.Default is not null && IsAbsolutePath(field.Default))
                throw new InvalidDataException($"path 字段 {field.Id} 的默认值不得是绝对路径。");
        }

        // 拒绝可执行内容与密钥明文。
        foreach (var field in declaration.Fields)
        {
            if (field.Default is not null) RejectExecutableContent(field.Default, $"字段 {field.Id}");
            if (field.CredentialRef is not null) RejectSecretPattern(field.CredentialRef, $"字段 {field.Id}");
        }
        foreach (var evidence in declaration.Evidence) RejectSecretPattern(evidence, "evidence");

        // 声明内不得出现命令关键词。
        foreach (var field in declaration.Fields.Where(field => field.Default is not null))
        {
            foreach (var keyword in ExecutableKeywords)
            {
                if (field.Default!.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"字段 {field.Id} 的默认值包含疑似可执行内容：{keyword}");
            }
        }

        // 插件侧车包绑定：name 必填、fingerprint 格式校验。
        if (declaration.Package is not null)
        {
            if (string.IsNullOrWhiteSpace(declaration.Package.VersionRange))
                throw new InvalidDataException("package.versionRange 必填。");
            if (declaration.Package.Fingerprint is not null)
            {
                var fingerprint = declaration.Package.Fingerprint;
                if (!fingerprint.StartsWith(FingerprintPrefix, StringComparison.OrdinalIgnoreCase)
                    || fingerprint.Length != FingerprintPrefix.Length + 64
                    || fingerprint[FingerprintPrefix.Length..].Any(character => !Uri.IsHexDigit(character)))
                    throw new InvalidDataException("package.fingerprint 必须是 sha256: 后跟 64 位小写十六进制。");
            }
        }
    }

    private static void RejectExecutableContent(string value, string context)
    {
        foreach (var keyword in ExecutableKeywords)
        {
            if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{context} 包含疑似可执行内容：{keyword}");
        }
    }

    private static void RejectSecretPattern(string value, string context)
    {
        if (SecretPattern.IsMatch(value))
            throw new InvalidDataException($"{context} 包含疑似密钥明文，声明拒绝携带任何密钥。");
    }

    private static bool IsAbsolutePath(string value)
    {
        try { return Path.IsPathRooted(value); }
        catch { return false; }
    }

    private static string RequireNonEmpty(JsonProperty property, string name)
    {
        if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
            throw new InvalidDataException($"{name} 必填且不能为空。");
        return property.Value.GetString()!;
    }

    // ---- 受信侧车 ----

    /// <summary>
    /// Helper 内置受信侧车：针对不可修改/会被 npm 更新覆盖的旧插件，作为随程序只读资产，
    /// 运行时仍需 package name（和版本范围）匹配。绝不放第三方 node_modules 包本体。
    /// </summary>
    public static IReadOnlyList<HelperSetupDeclaration> TrustedSidecars() =>
    [
        new HelperSetupDeclaration
        {
            SchemaVersion = 1,
            ComponentId = "trusted.dsh-web-search-opencode-go",
            DisplayName = "dsh-web-search-opencode-go",
            ComponentType = "plugin",
            Setup = "required",
            Package = new HelperSetupPackageBinding { Name = "dsh-web-search-opencode-go", VersionRange = "*" },
            Evidence = ["Helper 内置受信声明：旧版闭源插件，需要 API Key、Base URL 与模型。"],
            Fields =
            [
                new HelperSetupField { Id = "apiKey", Kind = "secret", Label = "API Key", Required = true, Export = false },
                new HelperSetupField { Id = "baseUrl", Kind = "url", Label = "Base URL", Required = true },
                new HelperSetupField { Id = "model", Kind = "model", Label = "模型", Required = true, DependsOn = "baseUrl" }
            ],
            Validation = new HelperSetupValidation { Protocol = "openai-responses", Capabilities = ["models", "text"] }
        },
        new HelperSetupDeclaration
        {
            SchemaVersion = 1,
            ComponentId = "trusted.dsh-vision-bridge",
            DisplayName = "dsh-vision-bridge",
            ComponentType = "plugin",
            Setup = "optional",
            Package = new HelperSetupPackageBinding { Name = "dsh-vision-bridge", VersionRange = "*" },
            Evidence = ["Helper 内置受信声明：视觉桥接复用已配置的 DSH provider/model，不重复索要 API Key。"],
            Fields =
            [
                new HelperSetupField { Id = "provider", Kind = "providerRef", Label = "复用 Provider", Required = true },
                new HelperSetupField { Id = "visionModel", Kind = "modelRef", Label = "视觉模型", Required = true }
            ],
            Validation = new HelperSetupValidation { Protocol = "provider-reference", Capabilities = ["vision"] }
        },
        new HelperSetupDeclaration
        {
            SchemaVersion = 1,
            ComponentId = "trusted.dsh-index-polyfill",
            DisplayName = "dsh-index-polyfill",
            ComponentType = "plugin",
            Setup = "none",
            Package = new HelperSetupPackageBinding { Name = "dsh-index-polyfill", VersionRange = "*" },
            Evidence = ["Helper 内置受信声明：本地索引补丁，无需机器配置。"]
        }
    ];

    // ---- 侧车绑定校验 ----

    /// <summary>
    /// 校验插件侧车绑定：package name 必须匹配；带版本范围时核对语义版本；
    /// 用户侧车还必须匹配 sha256 指纹（规范化 package.json + 入口文件）。
    /// 不匹配返回中文原因（“声明已过期/待人工确认”），绝不应用字段。
    /// </summary>
    public static string? ValidateSidecarBinding(
        HelperSetupDeclaration declaration,
        string packageName,
        string? packageVersion,
        string packageJsonPath,
        string entryFilePath,
        bool requireFingerprint)
    {
        if (declaration.Package is null) return "侧车声明缺少 package 绑定，无法核对。";
        if (!string.Equals(declaration.Package.Name, packageName, StringComparison.Ordinal))
            return $"侧车绑定包名不匹配：声明 {declaration.Package.Name}，实际 {packageName}。";
        if (!string.IsNullOrWhiteSpace(declaration.Package.VersionRange)
            && !string.Equals(declaration.Package.VersionRange, "*", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(packageVersion) || !MatchesVersionRange(packageVersion, declaration.Package.VersionRange))
                return $"侧车版本范围不匹配：声明 {declaration.Package.VersionRange}，实际 {(string.IsNullOrWhiteSpace(packageVersion) ? "未知" : packageVersion)}。";
        }
        if (requireFingerprint)
        {
            if (string.IsNullOrWhiteSpace(declaration.Package.Fingerprint)) return "用户侧车必须包含 sha256 指纹。";
            string computed;
            try { computed = ComputeSidecarFingerprint(packageJsonPath, entryFilePath); }
            catch (Exception ex) { return "侧车指纹无法计算：" + ex.Message; }
            if (!string.Equals(computed, declaration.Package.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return "侧车指纹不匹配（package.json 或入口文件已变化），声明已过期/待人工确认。";
        }
        return null;
    }

    /// <summary>计算侧车指纹：sha256(规范化 package.json + "\n" + 入口文件内容)。</summary>
    public static string ComputeSidecarFingerprint(string packageJsonPath, string entryFilePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath, Encoding.UTF8));
        var normalized = NormalizeJson(document.RootElement);
        var entry = File.ReadAllText(entryFilePath, Encoding.UTF8);
        var bytes = Encoding.UTF8.GetBytes(normalized + "\n" + entry);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 解析 package.json 的入口文件：main/module 优先，其次 index.js / &lt;name&gt;.js；
    /// 相对包根解析；无法确定时抛 InvalidDataException。
    /// </summary>
    public static string ResolvePackageEntry(string packageRoot, string packageName)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        string? entry = null;
        if (File.Exists(packageJsonPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath, Encoding.UTF8));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "main", "module" })
                {
                    if (document.RootElement.TryGetProperty(key, out var candidate) && candidate.ValueKind == JsonValueKind.String)
                    {
                        entry = candidate.GetString();
                        if (!string.IsNullOrWhiteSpace(entry)) break;
                    }
                }
            }
        }
        if (string.IsNullOrWhiteSpace(entry)) entry = "index.js";
        var candidates = new[]
        {
            Path.Combine(packageRoot, entry.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)),
            Path.Combine(packageRoot, "index.js"),
            Path.Combine(packageRoot, packageName.Replace("/", string.Empty) + ".js")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidDataException($"无法确定插件入口文件：{packageName}");
    }

    /// <summary>规范化 JSON：对象键按序排序、紧凑输出（用于指纹计算，保证跨机一致）。</summary>
    private static string NormalizeJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteNormalized(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalized(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteNormalized(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("无法规范化 JSON 值。");
        }
    }

    // ---- 语义版本范围匹配（npm 常用子集） ----

    /// <summary>
    /// 语义版本范围匹配：支持精确版本、^、~、&gt;=/&lt;=/&gt;/&lt;、空格分隔 AND、* / x 通配。
    /// 无法解析的范围按不匹配处理（保守，交由“待人工确认”）。
    /// </summary>
    public static bool MatchesVersionRange(string version, string range)
    {
        if (!TryParseVersion(version, out var actual)) return false;
        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*") return true;
        var constraints = range.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var constraint in constraints)
        {
            if (!MatchesConstraint(actual, constraint)) return false;
        }
        return true;
    }

    private static bool MatchesConstraint(SemanticVersion actual, string constraint)
    {
        if (constraint is "*" or "x" or "X") return true;
        if (constraint.StartsWith(">=", StringComparison.Ordinal))
            return TryParseVersion(constraint[2..], out var bound) && actual.CompareTo(bound) >= 0;
        if (constraint.StartsWith("<=", StringComparison.Ordinal))
            return TryParseVersion(constraint[2..], out var bound) && actual.CompareTo(bound) <= 0;
        if (constraint.StartsWith('>'))
            return TryParseVersion(constraint[1..], out var bound) && actual.CompareTo(bound) > 0;
        if (constraint.StartsWith('<'))
            return TryParseVersion(constraint[1..], out var bound) && actual.CompareTo(bound) < 0;
        if (constraint.StartsWith('^'))
        {
            if (!TryParseVersion(constraint[1..], out var bound)) return false;
            var upper = new SemanticVersion(bound.Major + (bound.Major > 0 ? 1 : 0),
                bound.Major == 0 ? bound.Minor + (bound.Minor > 0 ? 1 : 0) : 0,
                bound.Major == 0 && bound.Minor == 0 ? bound.Patch : 0, string.Empty);
            return actual.CompareTo(bound) >= 0 && actual.CompareTo(upper) < 0;
        }
        if (constraint.StartsWith('~'))
        {
            if (!TryParseVersion(constraint[1..], out var bound)) return false;
            var upper = new SemanticVersion(bound.Major, bound.Minor + 1, 0, string.Empty);
            return actual.CompareTo(bound) >= 0 && actual.CompareTo(upper) < 0;
        }
        // 精确或带通配（1.2.x / 1.x / 1.2）。
        return MatchesExactOrWildcard(actual, constraint);
    }

    private static bool MatchesExactOrWildcard(SemanticVersion actual, string constraint)
    {
        var clean = constraint.TrimStart('=', 'v');
        // 拆出约束的预发布部分（SemVer 中 '-' 之后的标识符）；构建元数据不参与比较。
        var constraintPrerelease = string.Empty;
        var dashIndex = clean.IndexOf('-');
        if (dashIndex >= 0)
        {
            constraintPrerelease = clean[(dashIndex + 1)..];
            clean = clean[..dashIndex];
        }
        var parts = clean.Split('.');
        var parsed = new int?[3];
        for (var index = 0; index < parts.Length && index < 3; index++)
        {
            if (parts[index] is "x" or "X" or "*") { parsed[index] = null; continue; }
            if (!int.TryParse(parts[index], out var number)) return false;
            parsed[index] = number;
        }
        if (parsed[0] is int major && major != actual.Major) return false;
        if (parsed[1] is int minor && minor != actual.Minor) return false;
        if (parsed[2] is int patch && patch != actual.Patch) return false;
        // 约束带预发布时，实版本预发布必须逐字一致；无预发布的完整约束（如 1.2.3）不匹配带预发布的实版本；
        // 通配约束（如 1.2.x）保持只看核心版本（既有行为不回退）。
        if (constraintPrerelease.Length > 0) return string.Equals(constraintPrerelease, actual.Prerelease, StringComparison.Ordinal);
        if (parsed[0] is not null && parsed[1] is not null && parsed[2] is not null) return actual.Prerelease.Length == 0;
        return true;
    }

    private static bool TryParseVersion(string text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = VersionPattern.Match(text.Trim());
        if (!match.Success) return false;
        var major = int.Parse(match.Groups[1].Value);
        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value : string.Empty;
        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease)
    {
        public int CompareTo(SemanticVersion other)
        {
            var result = Major.CompareTo(other.Major);
            if (result != 0) return result;
            result = Minor.CompareTo(other.Minor);
            if (result != 0) return result;
            result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;
            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        /// <summary>SemVer 预发布比较：无预发布高于有预发布；标识符逐个比较，数字按数值且低于非数字，
        /// 非数字按 ASCII 序；前缀完全相同者短者低。构建元数据不参与比较。</summary>
        private static int ComparePrerelease(string left, string right)
        {
            if (left.Length == 0 && right.Length == 0) return 0;
            if (left.Length == 0) return 1;
            if (right.Length == 0) return -1;
            var leftIdentifiers = left.Split('.');
            var rightIdentifiers = right.Split('.');
            var count = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);
            for (var index = 0; index < count; index++)
            {
                var result = CompareIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
                if (result != 0) return result;
            }
            return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
        }

        private static int CompareIdentifier(string left, string right)
        {
            var leftIsNumeric = long.TryParse(left, out var leftNumber);
            var rightIsNumeric = long.TryParse(right, out var rightNumber);
            if (leftIsNumeric && rightIsNumeric) return leftNumber.CompareTo(rightNumber);
            if (leftIsNumeric) return -1; // 数字标识符低于非数字标识符。
            if (rightIsNumeric) return 1;
            return string.CompareOrdinal(left, right);
        }
    }
}
