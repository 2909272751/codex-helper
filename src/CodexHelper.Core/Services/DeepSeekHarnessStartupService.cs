using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

public sealed record HarnessStartupStatus(bool Exists, bool MatchesCurrentPaths, string Message);

/// <summary>
/// Harness Web Host 登录自启动（Windows 计划任务）管理。
/// 计划任务执行已安装的 CodexHelper.exe 隐藏宿主模式（--harness-host --node &lt;绝对&gt; --dsh &lt;绝对&gt;），
/// 不再直接执行 node.exe：隐藏宿主先探测 127.0.0.1:3080，已健康安静退出，否则无窗口补位，
/// 避免每分钟直接启动控制台版 node.exe 导致 Node.js 窗口闪现。
/// 旧版直接启动 node 的计划任务会被识别为 stale。
/// </summary>
public sealed class DeepSeekHarnessStartupService
{
    public const string TaskName = "CodexHelper DeepSeek Harness Web";
    private readonly AppPaths paths;
    private string MetadataPath => Path.Combine(paths.BaseDirectory, "harness-startup.json");

    public DeepSeekHarnessStartupService(AppPaths paths) => this.paths = paths;

    public async Task<HarnessStartupStatus> GetStatusAsync(string nodePath, string dshEntryPath, CancellationToken cancellationToken = default)
    {
        var query = await RunSchtasksAsync(new[] { "/Query", "/TN", TaskName }, cancellationToken);
        if (query.ExitCode != 0) return new(false, false, "未配置登录自启动。");
        var xmlQuery = await RunSchtasksAsync(new[] { "/Query", "/TN", TaskName, "/XML" }, cancellationToken);
        if (xmlQuery.ExitCode != 0 || string.IsNullOrWhiteSpace(xmlQuery.StdOut))
            return new(true, false, "登录自启动已存在，但无法读取任务定义，请重新配置。");
        return EvaluateTaskDefinition(xmlQuery.StdOut, nodePath, dshEntryPath);
    }

    /// <summary>
    /// 配置登录自启动（隐藏宿主模式）。成功后替换旧计划任务（/F 覆盖，含旧版直接启动 node 的任务）。
    /// 所有外部路径以绝对路径 + ArgumentList 语义写入 XML 并正确转义。
    /// </summary>
    public async Task ConfigureAsync(string nodePath, string dshEntryPath, string? helperExePath = null, CancellationToken cancellationToken = default)
    {
        nodePath = Path.GetFullPath(nodePath);
        dshEntryPath = Path.GetFullPath(dshEntryPath);
        if (!File.Exists(nodePath)) throw new FileNotFoundException("Node 可执行文件不存在。", nodePath);
        if (!File.Exists(dshEntryPath)) throw new FileNotFoundException("Harness CLI 入口不存在。", dshEntryPath);
        helperExePath = ResolveHelperExePath(helperExePath);
        paths.EnsureCreated();
        var xmlPath = Path.Combine(paths.TempDirectory, $"harness-startup-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(xmlPath, BuildTaskXml(helperExePath, nodePath, dshEntryPath), Encoding.Unicode, cancellationToken);
            var create = await RunSchtasksAsync(new[] { "/Create", "/TN", TaskName, "/XML", xmlPath, "/F" }, cancellationToken);
            if (create.ExitCode != 0) throw new InvalidOperationException("创建登录自启动失败：" + FirstLine(create.StdErr, create.StdOut));
            var json = JsonSerializer.Serialize(new StartupMetadata(nodePath, dshEntryPath, helperExePath), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(MetadataPath, json, new UTF8Encoding(false), cancellationToken);
        }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunSchtasksAsync(new[] { "/Delete", "/TN", TaskName, "/F" }, cancellationToken);
        if (result.ExitCode != 0 && (await RunSchtasksAsync(new[] { "/Query", "/TN", TaskName }, cancellationToken)).ExitCode == 0)
            throw new InvalidOperationException("删除登录自启动失败：" + FirstLine(result.StdErr, result.StdOut));
        try { File.Delete(MetadataPath); } catch { }
    }

    /// <summary>当前进程即已安装的 CodexHelper.exe；开发/测试环境回退到输出目录同名可执行文件。</summary>
    public static string ResolveHelperExePath(string? helperExePath = null)
    {
        if (!string.IsNullOrWhiteSpace(helperExePath))
        {
            var full = Path.GetFullPath(helperExePath);
            if (File.Exists(full)) return full;
            throw new FileNotFoundException("Codex Helper 主程序不存在。", full);
        }
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return Path.GetFullPath(processPath);
        var fallback = Path.Combine(AppContext.BaseDirectory, "CodexHelper.exe");
        if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        throw new FileNotFoundException("找不到 CodexHelper.exe，无法配置隐藏宿主自启动。", fallback);
    }

    /// <summary>
    /// 构建计划任务 XML：登录触发 + 每分钟补位、LeastPrivilege、IgnoreNew、无时长上限。
    /// Action 是 CodexHelper.exe 隐藏宿主模式（--harness-host --node &lt;绝对&gt; --dsh &lt;绝对&gt;），
    /// 参数按 ArgumentList 语义逐项加引号并经 XML 转义。
    /// </summary>
    public static string BuildTaskXml(string helperExePath, string nodePath, string dshEntryPath)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");
        var command = SecurityElement.Escape(Path.GetFullPath(helperExePath))!;
        var arguments = SecurityElement.Escape(QuoteArgument("--harness-host") + " " + QuoteArgument("--node") + " " + QuoteArgument(Path.GetFullPath(nodePath)) + " " + QuoteArgument("--dsh") + " " + QuoteArgument(Path.GetFullPath(dshEntryPath)))!;
        var startBoundary = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss");
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
<Triggers>
  <LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger>
  <TimeTrigger><Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>{startBoundary}</StartBoundary><Enabled>true</Enabled></TimeTrigger>
</Triggers>
<Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure><Enabled>true</Enabled></Settings>
<Actions Context="Author"><Exec><Command>{command}</Command><Arguments>{arguments}</Arguments></Exec></Actions>
</Task>
""";
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    /// <summary>解析任务 XML 的第一个 Exec Action；无法解析返回 null。</summary>
    public static (string Command, string Arguments)? ParseTaskAction(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var exec = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Exec");
            if (exec is null) return null;
            var command = exec.Elements().FirstOrDefault(element => element.Name.LocalName == "Command")?.Value?.Trim();
            var arguments = exec.Elements().FirstOrDefault(element => element.Name.LocalName == "Arguments")?.Value;
            if (string.IsNullOrWhiteSpace(command)) return null;
            return (command, arguments ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>旧版计划任务直接执行 node.exe（无隐藏宿主包装）→ stale。</summary>
    public static bool IsStaleNodeAction(string command, string arguments)
    {
        var fileName = Path.GetFileName(command?.Trim() ?? string.Empty);
        return string.Equals(fileName, "node.exe", StringComparison.OrdinalIgnoreCase)
            && arguments.Contains("web", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 纯函数判定计划任务 XML 的配置状态（可独立测试）：
    /// 旧版直接启动 node 的 action → stale；隐藏宿主 action 且 --node/--dsh 与当前路径一致 → 匹配；
    /// 其余（无法解析 / 路径变化）→ 需要重新配置。绝不弹窗、不修改任何状态。
    /// </summary>
    public static HarnessStartupStatus EvaluateTaskDefinition(string xml, string nodePath, string dshEntryPath)
    {
        var action = ParseTaskAction(xml);
        if (action is null)
            return new(true, false, "登录自启动已存在，但任务定义无法解析，请重新配置。");
        if (IsStaleNodeAction(action.Value.Command, action.Value.Arguments))
            return new(true, false, "登录自启动是旧版直接启动 node 的任务，已过时；请重新配置为隐藏宿主模式。");
        var options = HarnessHiddenHostCli.TryParse(SplitArguments(action.Value.Arguments), out var parseError);
        if (options is null)
            return new(true, false, "登录自启动不是隐藏宿主模式（" + (string.IsNullOrWhiteSpace(parseError) ? "缺少 --harness-host" : parseError) + "），请重新配置。");
        var matches = PathEquals(options.NodePath, nodePath) && PathEquals(options.DshEntryPath, dshEntryPath);
        return new(true, matches,
            matches ? "登录自启动已配置为隐藏宿主，路径有效。" : "登录自启动已存在，但程序路径已变化，请重新配置。");
    }

    /// <summary>按 Windows 命令行语义拆分参数：空白分隔、引号分组、\" 与 "" 视为转义引号。</summary>
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var current = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                // 连续两个引号在引号内是转义引号；否则切换引号状态。
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    hasContent = true;
                    continue;
                }
                inQuotes = !inQuotes;
                hasContent = true;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (hasContent)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasContent = false;
                }
                continue;
            }
            current.Append(ch);
            hasContent = true;
        }
        if (hasContent) result.Add(current.ToString());
        return result;
    }

    private static Task<DeepSeekHarnessProcess.ProcessRunResult> RunSchtasksAsync(IReadOnlyList<string> args, CancellationToken token)
        => DeepSeekHarnessProcess.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), args, TimeSpan.FromSeconds(20), token);
    private static bool PathEquals(string? a, string? b) => string.Equals(Path.GetFullPath(a ?? "."), Path.GetFullPath(b ?? "."), StringComparison.OrdinalIgnoreCase);
    private static string FirstLine(params string[] values) => values.SelectMany(v => (v ?? "").Split('\r', '\n')).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "未知错误";
    private sealed record StartupMetadata(string NodePath, string DshEntryPath, string HelperExePath);
}
