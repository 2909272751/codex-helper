using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexHelper.Core.Services;

/// <summary>
/// 独立 Codex 验收线程的 turn 结局：Completed=true 仅当收到【本线程】的 `turn/completed` 通知且
/// `params.turn.status == "completed"`。工具调用、流静默、turn/start 响应、文本中出现 completed、
/// failed/interrupted、未匹配 thread、JSON-RPC error、进程退出、超时一律 Completed=false。
/// </summary>
public sealed record CodexTurnOutcome(bool Completed, string? FailureReason);

/// <summary>
/// 受限验收/triage turn 的最小权限形状（本机 schema 校准）：approvalPolicy="never"（仅用于固定受限指令），
/// sandboxPolicy={type:"workspaceWrite", writableRoots:[任务目录], networkAccess:false}——
/// 只允许向任务目录写账本（GPT_ACCEPTANCE/GPT_REMEDIATION），项目其它路径只读；
/// 任何超出该受限配置的审批/动态请求都会被客户端按失败安全处理（绝不自动批准）。
/// </summary>
public sealed record CodexTurnPermissions(string ApprovalPolicy = CodexTurnPermissions.NeverApproval, string? WritableRoot = null)
{
    public const string NeverApproval = "never";
    public const string WorkspaceWriteSandbox = "workspaceWrite";
    /// <summary>thread 级 sandbox（受限验收/复验）：只读项目，禁止项目根可写。</summary>
    public const string ReadOnlySandbox = "read-only";

    public bool IsEnabled => string.Equals(ApprovalPolicy, NeverApproval, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(WritableRoot);
}

/// <summary>
/// Codex App Server 传输抽象（stdio JSON-RPC / 可注入测试替身）。这是独立验收线程的唯一网络边界：
/// initialize → 建独立 thread（cwd=项目根）→ 启动一次验收 turn。传输层绝不承载合同正文、API Key、
/// Token、Cookie 或 DSH 消息正文（验收输入只含固定指令 + 绝对任务目录/项目根/任务 ID/合同指纹）。
/// </summary>
public interface ICodexAppServerTransport : IAsyncDisposable
{
    /// <summary>initialize（本地协议握手，零模型调用；失败抛异常）。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// thread/start：以项目根为 cwd 建独立验收线程，返回 threadId。
    /// sandboxMode 非空时按 schema 传 thread 级 sandbox（受限验收/复验用 "read-only"，使项目根不进入可写域）。
    /// </summary>
    Task<string> StartThreadAsync(string projectRoot, CancellationToken cancellationToken, string? sandboxMode = null);

    /// <summary>
    /// turn/start：向独立线程发送固定验收指令并等待真实终态（turn/completed + status=completed）。
    /// permissions 非空时按最小权限形状附加 approvalPolicy/sandboxPolicy。
    /// 协议错误/进程提前退出/不可识别形态/要求审批返回 Completed=false；外部取消抛 OperationCanceledException。
    /// </summary>
    Task<CodexTurnOutcome> RunTurnAsync(string threadId, string instruction, CancellationToken cancellationToken,
        CodexTurnPermissions? permissions = null);
}

/// <summary>
/// 真实验收驱动抛出的可诊断失败（消息已脱敏截断）；由协调器统一落盘 acceptance-failed，绝不伪装成功。
/// </summary>
public sealed class CodexAcceptanceException : InvalidOperationException
{
    public CodexAcceptanceException(string message) : base(message) { }
}

/// <summary>
/// 协议编译期基线（可审计）：依据本机 `codex app-server generate-json-schema` 输出校准。
/// 与运行时 schema 不匹配时按保守失败处理并记录安全摘要，绝不猜测字段。
/// </summary>
public static class CodexAppServerProtocolBaseline
{
    public const string SchemaSource =
        "codex app-server generate-json-schema（v1/v2 生成目录，本机 OpenAI Codex runtime）";
    public const string ClientInfoName = "codex-helper-harness-acceptance";
    /// <summary>initialize 的 clientInfo.version 为本机 schema 必填字段；这是协议客户端版本而非产品版本。</summary>
    public const string ClientInfoVersion = "1.0";
    /// <summary>initialize 响应必须具备的字段（v1 InitializeResponse.required）。</summary>
    public static readonly string[] InitializeRequiredFields = ["codexHome", "platformFamily", "platformOs", "userAgent"];
}

/// <summary>stdio 行读写抽象（JSON-RPC 新行分隔 JSON）；生产为进程，测试可注入脚本化替身。</summary>
public interface ICodexAppServerIo : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
    /// <summary>读到 EOF 返回 null；流异常也返回 null（调用方按进程退出保守处理）。</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 严格协议客户端（JSON line 级、可测试核心）：JSON-RPC 按 id 匹配 response、忽略无关通知；
/// 只认 `result.thread.id`（thread/start）与匹配 threadId 的 `turn/completed`+`params.turn.status=="completed"`。
/// 收到 App Server 发来的任何【请求】（需审批/动态交互，id+method）一律不自动批准 → 失败安全结论。
/// </summary>
public sealed class CodexAppServerProtocolClient : ICodexAppServerTransport
{
    private readonly ICodexAppServerIo io;
    private int nextId;

    public CodexAppServerProtocolClient(ICodexAppServerIo io)
    {
        this.io = io ?? throw new ArgumentNullException(nameof(io));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var id = NextId();
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = CodexAppServerProtocolBaseline.ClientInfoName,
                    ["version"] = CodexAppServerProtocolBaseline.ClientInfoVersion
                }
            }
        };
        await io.WriteLineAsync(JsonSerializer.Serialize(request), cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var line = await io.ReadLineAsync(deadline.Token);
                if (line is null) throw new CodexAcceptanceException("codex app-server 进程提前退出（initialize 未响应）。");
                var envelope = TryParse(line);
                if (envelope is null) continue; // 非 JSON 行忽略（保守：不猜成功）
                if (IsServerRequest(envelope))
                    throw new CodexAcceptanceException("initialize 期间收到 App Server 请求（需要审批/动态交互），未自动批准，独立验收未启动。");
                if (IsResponseFor(envelope, id))
                {
                    if (envelope["error"] is not null)
                        throw new CodexAcceptanceException("codex app-server initialize 返回 JSON-RPC error，独立验收未启动。");
                    var result = envelope["result"] as JsonObject;
                    // 按本机 schema 校验响应形状：缺必要字段即保守失败，绝不猜字段。
                    var missing = CodexAppServerProtocolBaseline.InitializeRequiredFields
                        .Where(field => result is null || result[field] is null).ToArray();
                    if (missing.Length > 0)
                        throw new CodexAcceptanceException(
                            "codex app-server initialize 响应与本机 schema 不匹配（缺 " + string.Join("/", missing) +
                            "），独立验收未启动（保守失败，来源：" + CodexAppServerProtocolBaseline.SchemaSource + "）。");
                    return;
                }
                // 无关通知/其它响应：继续等待（id 不匹配绝不视为握手成功）。
            }
        }
        catch (OperationCanceledException)
        {
            throw new CodexAcceptanceException("codex app-server initialize 超时（30 秒内未握手），独立验收未启动。");
        }
    }

    public async Task<string> StartThreadAsync(string projectRoot, CancellationToken cancellationToken, string? sandboxMode = null)
    {
        var id = NextId();
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "thread/start",
            ["params"] = new JsonObject { ["cwd"] = Path.GetFullPath(projectRoot) }
        };
        // 受限验收/复验：thread 级 sandbox 显式只读，避免默认把项目根纳入可写域。
        if (!string.IsNullOrWhiteSpace(sandboxMode))
            (request["params"] as JsonObject)!["sandbox"] = sandboxMode;
        await io.WriteLineAsync(JsonSerializer.Serialize(request), cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                var line = await io.ReadLineAsync(deadline.Token);
                if (line is null) throw new CodexAcceptanceException("codex app-server 进程提前退出（thread/start 未响应）。");
                var envelope = TryParse(line);
                if (envelope is null) continue;
                if (IsServerRequest(envelope))
                    throw new CodexAcceptanceException("thread/start 期间收到 App Server 请求（需要审批/动态交互），未自动批准。");
                if (IsResponseFor(envelope, id))
                {
                    if (envelope["error"] is not null)
                        throw new CodexAcceptanceException("codex app-server thread/start 返回 JSON-RPC error，验收线程未建立。");
                    // 严格从对应 response 的 result.thread.id 取 threadId。
                    var threadId = TryFindNestedString(envelope["result"], "thread", "id");
                    if (string.IsNullOrWhiteSpace(threadId))
                        throw new CodexAcceptanceException("thread/start 响应缺少 result.thread.id（或为空），验收线程未建立（不猜测 threadId）。");
                    return threadId;
                }
                // 无关通知/其它 id 响应：继续等待。
            }
        }
        catch (OperationCanceledException)
        {
            throw new CodexAcceptanceException("codex app-server thread/start 超时（60 秒内未取得 result.thread.id），验收线程未建立。");
        }
    }

    public async Task<CodexTurnOutcome> RunTurnAsync(string threadId, string instruction, CancellationToken cancellationToken,
        CodexTurnPermissions? permissions = null)
    {
        var id = NextId();
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "turn/start",
            ["params"] = new JsonObject
            {
                ["threadId"] = threadId,
                // 本机 schema：文本输入为 { type: "text", text: ... }，字段名 input；绝不发送 messages。
                ["input"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = instruction
                })
            }
        };
        // 最小权限形状（仅固定受限指令使用 approvalPolicy=never；sandbox 只向任务目录开放写）。
        if (permissions?.IsEnabled == true)
        {
            var turnParams = request["params"] as JsonObject;
            turnParams!["approvalPolicy"] = permissions.ApprovalPolicy;
            turnParams["sandboxPolicy"] = new JsonObject
            {
                ["type"] = CodexTurnPermissions.WorkspaceWriteSandbox,
                ["writableRoots"] = new JsonArray(permissions.WritableRoot),
                ["networkAccess"] = false
            };
        }
        await io.WriteLineAsync(JsonSerializer.Serialize(request), cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await io.ReadLineAsync(cancellationToken);
            if (line is null)
                return new CodexTurnOutcome(false, "codex app-server 进程提前退出（EOF），验收 turn 未到真实终态。");
            var envelope = TryParse(line);
            if (envelope is null)
                continue; // 非 JSON 行：不猜成功，继续等待

            // App Server 发来的请求（id+method，无 jsonrpc response 语义）= 需要审批/动态交互/能力询问：
            // 不得自动批准风险操作 → 失败安全结论。
            if (IsServerRequest(envelope))
                return new CodexTurnOutcome(false, "验收运行时要求用户审批或动态交互（" + (envelope["method"]?.ToString() ?? "未知") + "），未自动批准，验收中止。");

            if (IsResponseFor(envelope, id))
            {
                if (envelope["error"] is not null)
                    return new CodexTurnOutcome(false, "turn/start 返回 JSON-RPC error，验收 turn 未成功。");
                // turn/start 的即时响应（如 { turn: { status: inProgress } }）不是完成信号，继续等通知。
                continue;
            }

            if (string.Equals(FieldString(envelope, "method"), "turn/completed", StringComparison.Ordinal))
            {
                var paramsObj = envelope["params"] as JsonObject;
                var completedThreadId = FieldString(paramsObj, "threadId");
                // 只认匹配本线程的通知；其它线程的完成通知不影响本线程结论。
                if (!string.Equals(completedThreadId, threadId, StringComparison.Ordinal))
                    continue;
                var status = paramsObj?["turn"] is JsonObject turnObj ? FieldString(turnObj, "status") : null;
                if (string.Equals(status, "completed", StringComparison.Ordinal))
                    return new CodexTurnOutcome(true, null);
                return new CodexTurnOutcome(false,
                    "验收 turn 终态为 " + (status ?? "未知") + "（仅 completed 为成功；failed/interrupted 均为失败）。");
            }
            // 其它通知（推理增量、item 事件、工具状态等）：不推进完成判定，继续等待真实终态。
        }
    }

    public async ValueTask DisposeAsync() => await io.DisposeAsync();

    private int NextId() => Interlocked.Increment(ref nextId);

    private static string? FieldString(JsonObject? obj, string key)
    {
        try
        {
            if (obj is not null && obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        }
        catch (JsonException) { /* 类型不可解析按缺失处理 */ }
        return null;
    }

    private static JsonObject? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsResponseFor(JsonObject envelope, int expectedId)
    {
        // 响应 = 有 id 且无 method；id 按序列化文本精确匹配（客户端始终发送整数 id）。
        return envelope["method"] is null && envelope["id"] is not null
            && string.Equals(envelope["id"]!.ToJsonString(), expectedId.ToString(), StringComparison.Ordinal);
    }

    private static bool IsServerRequest(JsonObject envelope)
        => envelope["id"] is not null && envelope["method"] is not null;

    private static string? TryFindNestedString(JsonNode? node, string outer, string inner)
    {
        try
        {
            if (node is JsonObject outerObj && outerObj[outer] is JsonObject innerObj)
            {
                var value = innerObj[inner];
                if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch (JsonException) { /* 不可解析则按缺失处理 */ }
        return null;
    }
}

/// <summary>生产 stdio IO：驱动本机 codex 可执行文件的 `app-server`（从现有登录态取凭据，绝不读/记凭据）。</summary>
public sealed class CodexAppServerProcessIo : ICodexAppServerIo
{
    private readonly string codexExecutable;
    private Process? process;

    public CodexAppServerProcessIo(string codexExecutable)
    {
        if (string.IsNullOrWhiteSpace(codexExecutable) || !File.Exists(codexExecutable))
            throw new CodexAcceptanceException("未找到可用的 codex 可执行文件，无法启动独立验收（不调用任何模型）。");
        this.codexExecutable = Path.GetFullPath(codexExecutable);
    }

    private async Task EnsureProcessAsync(CancellationToken cancellationToken)
    {
        if (process is not null && !process.HasExited) return;
        var startInfo = new ProcessStartInfo(codexExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("app-server");
        process = Process.Start(startInfo)
            ?? throw new CodexAcceptanceException("codex app-server 进程启动失败，独立验收未启动。");
        // stderr 独立排空避免写满管道阻塞；内容不进入验收判定与记录。
        _ = process.StandardError.ReadToEndAsync();
        await Task.Yield();
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await EnsureProcessAsync(cancellationToken);
        await process!.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await process!.StandardInput.FlushAsync(cancellationToken);
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (process is null) return null;
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; } // 流异常按进程提前退出处理（保守失败）
    }

    public ValueTask DisposeAsync()
    {
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
            catch { /* 清理尽力而为 */ }
            finally
            {
                process.Dispose();
                process = null;
            }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 生产默认 stdio 传输：本机 codex 可执行文件 + 严格协议客户端。
/// 协议形状以本机 `codex app-server generate-json-schema` 输出为可审计依据（见
/// <see cref="CodexAppServerProtocolBaseline"/>），运行时不匹配即保守失败，绝不猜测字段。
/// </summary>
public sealed class CodexAppServerProcessTransport : ICodexAppServerTransport
{
    private readonly CodexAppServerProtocolClient inner;

    public CodexAppServerProcessTransport(string codexExecutable)
        => inner = new CodexAppServerProtocolClient(new CodexAppServerProcessIo(codexExecutable));

    public Task InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);
    public Task<string> StartThreadAsync(string projectRoot, CancellationToken cancellationToken, string? sandboxMode = null)
        => inner.StartThreadAsync(projectRoot, cancellationToken, sandboxMode);
    public Task<CodexTurnOutcome> RunTurnAsync(string threadId, string instruction, CancellationToken cancellationToken,
        CodexTurnPermissions? permissions = null)
        => inner.RunTurnAsync(threadId, instruction, cancellationToken, permissions);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>
/// 定位本机 codex 可执行文件（验收驱动默认解析，可注入/可测试）。绝不读取任何凭据文件：
/// 只做路径存在性探测。优先级：环境变量 CODEX_HELPER_CODEX_PATH → OpenAI Codex 安装目录 →
/// 用户目录 .codex/bin → PATH 中的 codex(.exe)。
/// </summary>
public static class CodexExecutableResolver
{
    private static readonly string[] KnownInstallRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin")
    ];

    public static string? Resolve()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_HELPER_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        // OpenAI Codex 安装根下可能有多个版本子目录（如 bin\8e5b...\codex.exe）：取最新。
        foreach (var root in KnownInstallRoots)
        {
            if (!Directory.Exists(root)) continue;
            var direct = Path.Combine(root, "codex.exe");
            if (File.Exists(direct)) return direct;
            var newest = Directory.EnumerateDirectories(root)
                .Where(sub => File.Exists(Path.Combine(sub, "codex.exe")))
                .OrderByDescending(sub => SafeLastWrite(sub))
                .Select(sub => Path.Combine(sub, "codex.exe"))
                .FirstOrDefault();
            if (newest is not null) return newest;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (var candidate in new[]
            {
                Path.Combine(home, ".codex", "bin", "codex.exe"),
                Path.Combine(home, ".codex", "bin", "codex")
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var name in new[] { "codex.exe", "codex" })
        {
            var fromPath = ResolveFromPath(name);
            if (fromPath is not null) return fromPath;
        }
        return null;
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static string? ResolveFromPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { /* 忽略无效 PATH 项 */ }
        }
        return null;
    }
}
