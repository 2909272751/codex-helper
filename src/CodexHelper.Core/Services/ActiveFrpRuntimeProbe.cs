using System.Diagnostics;
using System.Text;

namespace CodexHelper.Core.Services;

/// <summary>
/// 当前机器上"正在运行的 88FRP/frpc"实际使用的 runtime-frpc.toml 路径探测。
/// <para>目的：同一实例 ID 可能在用户版（<c>%LOCALAPPDATA%</c>）与系统服务版
/// （<c>%COMMONAPPDATA%</c>）两个根下各留一份配置，静止的旧文件会把修好的信任改回旧 IP。
/// 以真实活动进程的 <c>-c/--config</c> 指向为准，可消除这种"双根永久歧义"。</para>
/// <para>安全与隐私边界：</para>
/// <list type="bullet">
/// <item>只收集匹配 <c>frpc</c>/<c>88frpc</c> 两个进程名、且配置落在已知 88frp 实例根内、
/// 文件名为 <c>runtime-frpc.toml</c> 的路径；其他进程与其他位置一律忽略。</item>
/// <item>完整进程命令行绝不离开探针子进程：固定的有界 PowerShell 脚本内部先按进程名筛选、
/// 再用正则提取 <c>-c/--config</c> 取值，stdout 每行只输出一个配置路径（显式 UTF-8）。</item>
/// <item>只读查询：<c>UseShellExecute=false</c>、<c>CreateNoWindow</c>、UTF-8、最多 3 秒；
/// 超时立即清理该探针子进程，绝不留给后续请求。</item>
/// <item>只做短时间缓存（默认数秒）以减少每次启动成本；不写任何持久缓存，避免旧 IP 被固化。</item>
/// </list>
/// </summary>
public static class ActiveFrpRuntimeProbe
{
    /// <summary>真实活动进程名（不带扩展名，按不区分大小写比较）。</summary>
    public static readonly IReadOnlyList<string> ProcessNames = new[] { "frpc", "88frpc" };

    /// <summary>只接受该文件名的配置路径（88frp 运行时配置的固定名）。</summary>
    public const string RuntimeConfigFileName = "runtime-frpc.toml";

    /// <summary>
    /// 进程探测可接受的路径上限。超过该上限说明拿到的候选集不可信：
    /// 绝不截断、绝不信任任意子集，而是明确返回"未能探测"。
    /// </summary>
    public const int MaxActiveConfigPaths = 128;

    /// <summary>探针硬超时（有界只读查询；超时清理探针子进程）。</summary>
    public static TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>结果缓存有效期；只用于减少每次启动成本，绝不持久化（避免旧 IP 被固化）。</summary>
    public static TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>CIM/PowerShell 查询进程活动配置（返回原始路径文本；测试可注入）。</summary>
    public static Func<CancellationToken, Task<IReadOnlyList<string>>> Query { get; set; } = QueryViaCimAsync;

    /// <summary>
    /// 命令文本固定：只输出路径；不做任何写入、不加载用户 profile；显式把控制台输出编码设为 UTF-8，
    /// 保证带非 ASCII 字符的实例路径不会因默认 OEM 代码页而乱码。完整命令行只存在于子进程内。
    /// </summary>
    private const string CimScript =
        "$ErrorActionPreference='SilentlyContinue';" +
        "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
        "Get-CimInstance Win32_Process -Filter \"Name='88frpc.exe' or Name='frpc.exe'\" | ForEach-Object {" +
        " $text=$_.CommandLine; if([string]::IsNullOrWhiteSpace($text)){ return };" +
        " $found=[regex]::Matches($text,'(?:^|\\s)(?:-c|--config)(?:\\s+|=)(?:\"([^\"]+)\"|''([^'']+)''|(\\S+))');" +
        " foreach($item in $found){" +
        "  $value=$item.Groups[1].Value;" +
        "  if([string]::IsNullOrWhiteSpace($value)){ $value=$item.Groups[2].Value };" +
        "  if([string]::IsNullOrWhiteSpace($value)){ $value=$item.Groups[3].Value };" +
        "  $value=$value.Trim().Trim([char]34).Trim([char]39);" +
        "  if(-not [string]::IsNullOrWhiteSpace($value)){ Write-Output $value } } }";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly AsyncLocal<bool> Probing = new();
    private static readonly object CacheLock = new();
    private static ProbeResult? cached;
    private static DateTimeOffset cachedAt;

    /// <summary>清空短时缓存（测试隔离与显式重解析用）。</summary>
    public static void ResetCache()
    {
        lock (CacheLock) { cached = null; cachedAt = default; }
    }

    /// <summary>当前活动进程实际使用的 88frp 运行时配置路径（有界、去重、稳定排序）。</summary>
    public static async Task<ProbeResult> ResolveActiveConfigPathsAsync(CancellationToken cancellationToken = default)
    {
        lock (CacheLock)
        {
            if (cached is not null && DateTimeOffset.UtcNow - cachedAt < CacheTtl) return cached;
        }

        // 嵌套/重入：内层直接放弃探测（保守），绝不与已持锁的外层争用。
        if (Probing.Value) return ProbeResult.NotAttempted;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (CacheLock)
            {
                if (cached is not null && DateTimeOffset.UtcNow - cachedAt < CacheTtl) return cached;
            }
            Probing.Value = true;
            var result = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
            Probing.Value = false;
            lock (CacheLock)
            {
                cached = result;
                cachedAt = DateTimeOffset.UtcNow;
            }
            return result;
        }
        finally
        {
            Probing.Value = false;
            Gate.Release();
        }
    }

    private static async Task<ProbeResult> ProbeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            var raw = await Query(timeout.Token).ConfigureAwait(false) ?? Array.Empty<string>();
            var roots = FrpRuntimeAuthorityResolver.ResolveInstanceRoots();
            // 注意：这里绝不 Take(N) 截断。截断会在"哪一条才是 DSH"尚未判定之前丢掉候选，
            // 例如第三个活动实例才是 DSH 时会被误判为"查不到"。上限只用于整体可信度判定。
            var accepted = raw
                .Select(value => TryNormalizeActiveConfigPath(value, roots))
                .Where(path => path is not null)
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // 候选超过上限：不截断也不猜测，明确返回"未探测"（调用方按保守回退处理）。
            if (accepted.Length > MaxActiveConfigPaths) return ProbeResult.NotAttempted;
            return accepted.Length == 0 ? ProbeResult.None : new(true, true, accepted);
        }
        catch (OperationCanceledException)
        {
            // 超时/取消：按"未查到活动进程"保守处理，由调用方回退到静止双根；绝不抛出。
            return ProbeResult.NotAttempted;
        }
        catch
        {
            return ProbeResult.NotAttempted;
        }
    }

    /// <summary>
    /// 规范化并校验单个候选路径：必须是绝对路径、文件名为 <c>runtime-frpc.toml</c>、
    /// 且严格位于已知 88frp 实例根内；已知根自身、根到目标的每一级祖先以及目标文件本身
    /// 都不得是 reparse point（目录联接/符号链接会把"已知根内的配置"重定向到根外，一律拒绝、
    /// 绝不跟随）；已知根之外一律拒绝；不做 reparse 跟随写入。
    /// </summary>
    internal static string? TryNormalizeActiveConfigPath(string? candidate, IReadOnlyList<string> knownInstanceRoots)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var value = candidate.Trim().Trim('"');
        if (value.Length == 0) return null;
        // 命令行取值里混入的引号属于非法文件名字符：拒绝而不是猜测。
        if (value.IndexOfAny(['"', '\'', '\0']) >= 0) return null;
        try
        {
            if (!Path.IsPathRooted(value)) return null;
            var full = Path.GetFullPath(value);
            if (!string.Equals(Path.GetFileName(full), RuntimeConfigFileName, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var root in knownInstanceRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string fullRoot;
                try { fullRoot = Path.GetFullPath(root); } catch { continue; }
                if (!CodexHelper.Core.Infrastructure.PathSafety.IsWithin(full, fullRoot)) continue;
                return HasReparsePointOnChain(fullRoot, full) ? null : full;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>已知根自身、根到目标的每一级祖先（含目标文件）是否含 reparse point。</summary>
    private static bool HasReparsePointOnChain(string fullRoot, string fullTarget)
    {
        if (IsReparsePoint(fullRoot)) return true;
        if (string.Equals(fullRoot, fullTarget, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = fullTarget.Length > fullRoot.Length ? fullTarget[(fullRoot.Length + 1)..] : string.Empty;
        var current = fullRoot;
        foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current)) break;
            if (IsReparsePoint(current)) return true;
        }
        return false;
    }

    /// <summary>路径是否已存在且带 reparse 属性（不存在或不可读时按"不是"保守处理，由调用方其它校验兜底）。</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 固定 PowerShell 脚本（只读暴露给测试断言"显式 UTF-8 + 只输出路径 + 不带出命令行"；
    /// 任何调用方都不得改写它）。
    /// </summary>
    internal static string QueryScriptForTests => CimScript;

    /// <summary>
    /// 固定有界只读 CIM 查询：脚本内部按进程名筛选，并在子进程内用正则提取 <c>-c/--config</c> 取值，
    /// stdout 每行只输出一个配置路径（显式 UTF-8）。完整命令行绝不带出子进程。
    /// </summary>
    private static async Task<IReadOnlyList<string>> QueryViaCimAsync(CancellationToken cancellationToken)
    {
        var shell = ResolvePowerShellPath();
        if (shell is null) return Array.Empty<string>();

        try
        {
            var start = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(CimScript);

            using var process = new Process { StartInfo = start };
            if (!process.Start()) return Array.Empty<string>();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时必须清理探针子进程，避免留下游离 PowerShell。
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return Array.Empty<string>();
            }
            if (process.ExitCode != 0) return Array.Empty<string>();
            string text;
            try { text = await stdout.ConfigureAwait(false); }
            catch { return Array.Empty<string>(); }
            _ = stderr;
            return ReadPathLines(text);
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 读取固定脚本 stdout 的"每行一个路径"输出：只去空白、不做任何猜测或补全
    /// （合法性由 <see cref="TryNormalizeActiveConfigPath"/> 逐条校验）。
    /// </summary>
    internal static IReadOnlyList<string> ReadPathLines(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<string>();
        var paths = new List<string>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length > 0) paths.Add(line);
        }
        return paths;
    }

    /// <summary>
    /// 从"完整命令行文本"中提取 <c>-c/--config</c> 取值的纯函数。生产链路已改为在固定脚本内
    /// 提取（命令行不出子进程），此函数只作为可测试的纯逻辑保留，绝不接收来自子进程的命令行。
    /// </summary>
    internal static IReadOnlyList<string> ExtractConfigPaths(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<string>();
        var paths = new List<string>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var match = System.Text.RegularExpressions.Regex.Match(
                line,
                "(?:^|\\s)(?:-c|--config)(?:\\s+|=)(?:\"(?<q>[^\"]+)\"|'(?<s>[^']+)'|(?<b>\\S+))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var value = match.Groups["q"].Success ? match.Groups["q"].Value
                : match.Groups["s"].Success ? match.Groups["s"].Value
                : match.Groups["b"].Value;
            value = value.Trim().Trim('"', '\'');
            if (value.Length > 0) paths.Add(value);
        }
        return paths;
    }

    /// <summary>定位 Windows PowerShell 的绝对路径（绝不经过 PATH 上的相对命令）。</summary>
    private static string? ResolvePowerShellPath()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(system) ? null : Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } root
                ? Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
                : null,
            Environment.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } programFiles
                ? Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe")
                : null
        };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { if (File.Exists(candidate)) return candidate; } catch { }
        }
        return null;
    }
}

/// <summary>
/// 一次"活动 88FRP 实例"探测结果。
/// <para><see cref="Probed"/>：是否真的执行了有界只读探测（超时/无 PowerShell/嵌套调用为 false）；
/// <see cref="Attempted"/>：是否查到活动进程；<see cref="Paths"/>：其实际使用的配置路径（有界）。</para>
/// </summary>
public sealed record ProbeResult(bool Probed, bool Attempted, IReadOnlyList<string> Paths)
{
    /// <summary>未探测（超时/无通道/嵌套）：调用方按"查不到活动进程"保守回退静止双根。</summary>
    public static ProbeResult NotAttempted { get; } = new(false, false, Array.Empty<string>());

    /// <summary>探测完成但没有活动进程：保守回退静止双根。</summary>
    public static ProbeResult None { get; } = new(true, false, Array.Empty<string>());
}
