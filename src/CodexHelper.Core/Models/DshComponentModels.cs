namespace CodexHelper.Core.Models;

/// <summary>DSH 组件类型：skill/plugin/preset/bridge（helper-setup.json componentType）。</summary>
public enum DshComponentType
{
    Skill,
    Plugin,
    Preset,
    Bridge
}

/// <summary>组件配置状态：none/optional/required/unknown（helper-setup.json setup）。</summary>
public enum DshSetupState
{
    None,
    Optional,
    Required,
    Unknown
}

/// <summary>声明字段类型（helper-setup.json fields[].kind）。</summary>
public enum HelperSetupFieldKind
{
    Secret,
    Url,
    Model,
    Path,
    Text,
    Choice,
    Boolean,
    ProviderRef,
    ModelRef
}

/// <summary>声明验证协议（helper-setup.json validation.protocol）。</summary>
public enum HelperSetupValidationProtocol
{
    None,
    OpenAiResponses,
    OpenAiCompatible,
    ProviderReference,
    PathExists
}

/// <summary>组件声明来源。</summary>
public enum DshDeclarationSource
{
    /// <summary>没有任何声明，仅依据静态提示或默认无需配置。</summary>
    None,

    /// <summary>组件根目录内嵌 helper-setup.json。</summary>
    Embedded,

    /// <summary>用户侧车（$DSH_HOME/.helper-setup/plugins|presets/*.helper-setup.json）。</summary>
    UserSidecar,

    /// <summary>Helper 内置受信侧车（随程序只读资产，仍需运行时包名/版本匹配）。</summary>
    TrustedSidecar
}

/// <summary>组件扫描后的配置状态（至少包括：无需配置、已就绪、待配置、可选配置、待人工确认、声明无效）。</summary>
public enum DshComponentStatus
{
    /// <summary>无需配置（setup none 或无声明且无提示）。</summary>
    SetupNone,

    /// <summary>已就绪（required 组件依赖的 provider/model 已存在，或非 required 组件）。</summary>
    Ready,

    /// <summary>待配置（required 组件存在缺失字段，只暴露字段 id，不返回密钥值）。</summary>
    RequiredMissing,

    /// <summary>可选配置（setup optional）。</summary>
    OptionalConfig,

    /// <summary>待人工确认（setup unknown、侧车绑定过期或不匹配、静态提示可能需要设置）。</summary>
    ManualReview,

    /// <summary>声明无效（解析或校验失败，绝不应用字段）。</summary>
    InvalidDeclaration
}

/// <summary>helper-setup.json v1 声明模型。字符串字段保留原文，解析器负责严格枚举校验。</summary>
public sealed class HelperSetupDeclaration
{
    public int SchemaVersion { get; set; } = 1;
    public string ComponentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ComponentType { get; set; } = string.Empty;
    public string Setup { get; set; } = string.Empty;
    public HelperSetupPackageBinding? Package { get; set; }
    public List<string> Evidence { get; set; } = new();
    public List<HelperSetupField> Fields { get; set; } = new();
    public HelperSetupValidation? Validation { get; set; }
}

/// <summary>插件侧车包绑定：package name、语义版本范围、可选 sha256 指纹。</summary>
public sealed class HelperSetupPackageBinding
{
    public string Name { get; set; } = string.Empty;
    public string VersionRange { get; set; } = string.Empty;
    public string? Fingerprint { get; set; }
}

/// <summary>声明字段。Default 统一为字符串（boolean 字段用 "true"/"false"）；secret 字段不允许默认值。</summary>
public sealed class HelperSetupField
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? Default { get; set; }
    public bool Export { get; set; } = true;
    public string? CredentialRef { get; set; }
    public string? DependsOn { get; set; }
    public List<string> Choices { get; set; } = new();
}

/// <summary>声明验证规格：协议与能力（capabilities 仅声明用途，Helper 决定如何做受信检查）。</summary>
public sealed class HelperSetupValidation
{
    public string Protocol { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = new();
}

/// <summary>组件扫描结果（名称、类型、版本、声明来源、配置状态、缺失字段 id、证据；绝不包含密钥值）。</summary>
public sealed class DshComponentInfo
{
    /// <summary>组件名：skill/preset 用目录名，插件用 package name。</summary>
    public string Name { get; set; } = string.Empty;

    public DshComponentType Kind { get; set; }

    /// <summary>插件版本（package.json version）；skill/preset 为 null。</summary>
    public string? Version { get; set; }

    public DshDeclarationSource DeclarationSource { get; set; }

    public DshSetupState Setup { get; set; }

    public DshComponentStatus Status { get; set; }

    /// <summary>缺失配置字段 id（只含 id，绝不包含任何密钥值）。</summary>
    public List<string> MissingFieldIds { get; set; } = new();

    /// <summary>判定依据（声明证据或静态提示原文）。</summary>
    public List<string> Evidence { get; set; } = new();

    /// <summary>待人工确认/声明无效的原因；正常状态为 null。</summary>
    public string? ReviewReason { get; set; }

    /// <summary>组件根目录（绝对路径，仅本地使用，不进入迁移包）。</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>有效声明（无声明或声明无效时为 null）。</summary>
    public HelperSetupDeclaration? Declaration { get; set; }

    /// <summary>配置步骤中文说明（UI 展开显示）。</summary>
    public string? SetupSteps { get; set; }

    /// <summary>导入时相对 DSH Home 的目标根（如 skills/&lt;name&gt;、.agent-presets/&lt;name&gt;、profiles/node_modules/&lt;pkg&gt;）。</summary>
    public string TargetRelativeRoot { get; set; } = string.Empty;

    /// <summary>Kind 中文显示。</summary>
    public string KindDisplay => Kind switch
    {
        DshComponentType.Skill => "Skill",
        DshComponentType.Plugin => "插件",
        DshComponentType.Preset => "Agent 预设",
        DshComponentType.Bridge => "桥接",
        _ => Kind.ToString()
    };

    /// <summary>Status 中文显示。</summary>
    public string StatusDisplay => Status switch
    {
        DshComponentStatus.SetupNone => "无需配置",
        DshComponentStatus.Ready => "已就绪",
        DshComponentStatus.RequiredMissing => "待配置",
        DshComponentStatus.OptionalConfig => "可选配置",
        DshComponentStatus.ManualReview => "待人工确认",
        DshComponentStatus.InvalidDeclaration => "声明无效",
        _ => Status.ToString()
    };

    /// <summary>声明来源中文显示。</summary>
    public string DeclarationSourceDisplay => DeclarationSource switch
    {
        DshDeclarationSource.Embedded => "内嵌声明",
        DshDeclarationSource.UserSidecar => "用户侧车",
        DshDeclarationSource.TrustedSidecar => "Helper 受信侧车",
        DshDeclarationSource.None => "无声明",
        _ => DeclarationSource.ToString()
    };
}

/// <summary>DSH 迁移包（ZIP）固定版本 manifest。</summary>
public sealed class DshTransferManifest
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>迁移包格式版本（固定 "1.0"，与 Helper 版本无关）。</summary>
    public string ManifestVersion { get; set; } = "1.0";

    public string TransferId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string CodexHelperVersion { get; set; } = string.Empty;
    public List<DshTransferComponent> Components { get; set; } = new();
    public List<DshTransferFile> Files { get; set; } = new();
    public List<OperationIssue> Issues { get; set; } = new();
}

/// <summary>迁移包组件清单项：TargetRelativeRoot 是导入时相对目标 DSH Home 的安全相对根。</summary>
public sealed record DshTransferComponent(
    string Id,
    string Kind,
    string DisplayName,
    string? Version,
    string TargetRelativeRoot,
    string? DeclarationRelativePath);

/// <summary>迁移包文件条目：相对组件根路径 + SHA-256。</summary>
public sealed record DshTransferFile(
    string ComponentId,
    string RelativePath,
    string ContentHash,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record DshTransferExportRequest(
    string DestinationZipPath,
    IReadOnlyList<DshComponentInfo> Components);

public sealed record DshTransferPreview(
    DshTransferManifest Manifest,
    long ZipSize,
    bool StructureVerified);

public sealed record DshTransferImportRequest(
    string BundlePath,
    string DestinationHome,
    bool OnlyNewFiles = true);

public sealed record DshTransferImportResult(
    OperationOutcome Outcome,
    int ImportedFiles,
    int SkippedConflicts,
    int PendingSetupComponents,
    int InvalidComponents,
    IReadOnlyList<OperationIssue> Issues);
