namespace CodexHelper.Core.Services;

using System.Xml.Linq;

/// <summary>
/// 第七阶段 helper-repair 项目根识别：从受管 Helper 可执行/运行目录向上安全解析"真实 Codex Helper
/// 项目根"（即 Helper 自身源码/构建仓库），用于 helper-repair 在该根下唯一创建独立根因键修复合同。
/// 解析不依赖硬编码用户名/盘符（只做祖先目录遍历），并且只认 Helper 源码布局标记
/// （Directory.Build.props、CodexHelper.sln、src\CodexHelper.Core\CodexHelper.Core.csproj、
/// scripts\build.ps1），其中 Directory.Build.props 必须能实际读取并解析出 XML Product 值
/// "Codex Helper"（不凭任意字符串包含、不做猜测）；props 读取失败/不可解析一律拒绝。
/// 任意产品项目根（只有 .codex-helper/AGENTS.md 等 Harness 指导，无 Helper 源码布局）
/// 一律拒绝（返回 null → 修复侧 needs-user，绝不把任意项目误认成 Helper）。
/// </summary>
public static class HarnessHelperRootResolver
{
    /// <summary>Helper 源码根的必要标记：根下存在这些文件/目录才视为真实 Helper 项目根。</summary>
    private static readonly string[] RequiredMarkers =
    [
        Path.Combine("Directory.Build.props"),
        "CodexHelper.sln",
        Path.Combine("src", "CodexHelper.Core", "CodexHelper.Core.csproj"),
        Path.Combine("scripts", "build.ps1")
    ];

    /// <summary>
    /// 生产默认入口：从当前 Helper 运行上下文（AppContext.BaseDirectory 与当前进程路径）逐级向上找
    /// 首个满足 Helper 源码布局标记的目录；找不到返回 null（保守，不猜测、不把任意项目当 Helper）。
    /// </summary>
    public static string? TryResolve()
        => TryResolveFromCandidates(
        [
            AppContext.BaseDirectory,
            Environment.ProcessPath is { Length: > 0 } processPath ? Path.GetDirectoryName(processPath) : null
        ]);

    /// <summary>显式起始目录版（测试用）：从该目录开始逐级向上解析；返回 null 表示未命中真实 Helper 根。</summary>
    public static string? TryResolveFrom(string? startDirectory)
        => TryResolveFromCandidates([startDirectory]);

    /// <summary>
    /// 判定某个目录是否具备真实 Helper 项目根的源码布局标记。四个布局文件必须都存在；
    /// Directory.Build.props 必须可读取且为合法 XML，且其 <c>&lt;Product&gt;</c> 元素文本等于
    /// "Codex Helper"（XML 值精确校验，非任意子串包含）。props 读取失败/损坏/非 Helper 产品一律拒绝。
    /// </summary>
    public static bool LooksLikeHelperRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        foreach (var marker in RequiredMarkers)
        {
            if (!File.Exists(Path.Combine(directory, marker))) return false;
        }
        string propsText;
        try
        {
            propsText = File.ReadAllText(Path.Combine(directory, "Directory.Build.props"), System.Text.Encoding.UTF8);
        }
        catch
        {
            return false; // 读取失败（占用/权限/IO）→ 拒绝，绝不因文件存在而放行。
        }
        try
        {
            var document = XDocument.Parse(propsText);
            var product = document.Descendants("Product").FirstOrDefault()?.Value?.Trim();
            return string.Equals(product, "Codex Helper", StringComparison.Ordinal);
        }
        catch
        {
            return false; // props 不可解析（非法 XML/损坏）→ 拒绝。
        }
    }

    private static string? TryResolveFromCandidates(IEnumerable<string?> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var current = Path.GetFullPath(candidate);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (LooksLikeHelperRoot(current)) return current;
                var parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
        return null;
    }
}
