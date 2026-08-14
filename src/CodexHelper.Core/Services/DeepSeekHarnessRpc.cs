using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexHelper.Core.Services;

/// <summary>Harness RPC 业务结果：成功携带 value；失败携带可读中文错误消息。不记录合同正文/密钥/完整敏感响应。</summary>
public sealed record HarnessRpcResult(bool Success, string? ErrorMessage, JsonNode? Value)
{
    public string? GetString(string property) => HarnessJson.Text(Value?[property]);
    public static HarnessRpcResult Ok(JsonNode? value) => new(true, null, value);
    public static HarnessRpcResult Fail(string message) => new(false, message, null);
}

/// <summary>JSON 文本读取与脱敏截断助手。</summary>
internal static class HarnessJson
{
    public static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }
}

/// <summary>
/// Harness Web Host 一元 RPC 客户端（rc.6 原生协议）：POST /api/{method}，
/// 请求信封 { type:"client-request", rpcId, method, payload }；
/// 响应 { type:"server-response", rpcId（必须回显请求 rpcId）, result:{ ok:true, value } | { ok:false, error:{ code, message, details } } }。
/// RPC ID 每次唯一并校验响应回显；业务错误转换为可读失败；JSON 以 UTF-8 字节发送（支持中文路径）。
/// 仅连接本机回环地址（由调用方保证 baseUrl）。
/// </summary>
public sealed class HarnessRpcClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string baseUrl;

    /// <param name="baseUrl">Host 基地址（默认 http://127.0.0.1:3080）。</param>
    /// <param name="http">可选注入的 HttpClient（默认新建，Timeout 10 秒，随实例释放）。</param>
    public HarnessRpcClient(string? baseUrl = null, HttpClient? http = null, TimeSpan? timeout = null)
    {
        this.baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DeepSeekHarnessVersions.WebHostDefaultUrl : baseUrl).TrimEnd('/');
        if (http is not null)
        {
            this.http = http;
            ownsHttp = false;
        }
        else
        {
            this.http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
            ownsHttp = true;
        }
    }

    public string BaseUrl => baseUrl;

    /// <summary>session.create：以 cwd 为项目根目录创建标准编码会话（agentPreset="standard"）。</summary>
    public Task<HarnessRpcResult> CreateSessionAsync(string cwd, string agentPreset, CancellationToken cancellationToken = default)
        => CallAsync("session.create", new JsonObject { ["cwd"] = cwd, ["agentPreset"] = agentPreset }, cancellationToken);

    /// <summary>session.prompt：排队提交只含任务目录定位与执行职责的短提示（正文只从项目内合同文件读取）。</summary>
    public Task<HarnessRpcResult> PromptAsync(string sessionId, string text, string clientTimeZone, CancellationToken cancellationToken = default)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        return CallAsync("session.prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["mode"] = "queue",
            ["content"] = content,
            ["clientTimeZone"] = clientTimeZone
        }, cancellationToken);
    }

    /// <summary>session.cancel：取消指定会话的进行中回合（用户停止）。</summary>
    public Task<HarnessRpcResult> CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        => CallAsync("session.cancel", new JsonObject { ["sessionId"] = sessionId }, cancellationToken);

    public Task<HarnessRpcResult> ListSessionsAsync(CancellationToken cancellationToken = default)
        => CallAsync("session.list", new JsonObject(), cancellationToken);

    /// <summary>执行一次一元 RPC；网络/协议/业务错误均转换为可读失败，不抛出（取消除外）。</summary>
    public async Task<HarnessRpcResult> CallAsync(string method, JsonNode? payload, CancellationToken cancellationToken = default)
    {
        var rpcId = Guid.NewGuid().ToString("N");
        var envelope = new JsonObject
        {
            ["type"] = "client-request",
            ["rpcId"] = rpcId,
            ["method"] = method,
            ["payload"] = payload ?? new JsonObject()
        };

        using var content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync($"{baseUrl}/api/{method}", content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HarnessRpcResult.Fail($"无法连接 Harness Host（{baseUrl}）：{HarnessJson.Truncate(ex.Message, 160)}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return HarnessRpcResult.Fail($"Harness Host 返回 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。");

            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return HarnessRpcResult.Fail("读取 Harness 响应失败：" + HarnessJson.Truncate(ex.Message, 160));
            }
            if (text.Length > 2_000_000)
                return HarnessRpcResult.Fail("Harness 响应过大，已丢弃。");

            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch { return HarnessRpcResult.Fail("Harness 响应不是有效 JSON。"); }

            if (!string.Equals(HarnessJson.Text(root?["type"]), "server-response", StringComparison.Ordinal))
                return HarnessRpcResult.Fail("Harness 响应信封类型无效。");
            if (!string.Equals(HarnessJson.Text(root?["rpcId"]), rpcId, StringComparison.Ordinal))
                return HarnessRpcResult.Fail("RPC 响应回显不匹配，已丢弃该响应。");

            var result = root?["result"];
            var ok = result?["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var okBool) && okBool;
            if (ok) return HarnessRpcResult.Ok(result?["value"]);

            var code = HarnessJson.Text(result?["error"]?["code"]) ?? "unknown";
            var message = HarnessJson.Text(result?["error"]?["message"]);
            return HarnessRpcResult.Fail(ReadableError(code, message));
        }
    }

    private static string ReadableError(string code, string? message)
    {
        var reason = code switch
        {
            "session-not-found" => "会话不存在或已被删除",
            "session-conflict" => "会话冲突：同一会话标识已被不同工作目录占用",
            "agent-preset-not-found" => "Agent 预设不存在",
            "agent-preset-invalid" => "Agent 预设无效",
            "agent-busy" => "Agent 忙，请求被拒绝",
            "bad-request" => "请求参数无效",
            "cancelled" => "请求被取消",
            "internal" => "Harness Host 内部错误",
            "directory-unreadable" => "工作目录不可读",
            _ => $"业务错误 {code}"
        };
        var detail = HarnessJson.Truncate(message, 160);
        return string.IsNullOrWhiteSpace(detail) ? reason : $"{reason}：{detail}";
    }

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }
}

/// <summary>解析后的 mux 事件帧（只保留 runner 需要的结构化字段，不保留正文/密钥等敏感内容）。</summary>
public sealed record HarnessMuxFrame(string Type, string? SessionId, string? EventType, string? TurnEndKind, string? ErrorMessage)
{
    /// <summary>解析服务端文本帧；非法/无关帧返回 null（静默忽略，不中断流）。</summary>
    public static HarnessMuxFrame? Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return null; }
        if (!string.Equals(HarnessJson.Text(root?["type"]), "server-request", StringComparison.Ordinal)) return null;
        var payload = root?["payload"];
        var type = HarnessJson.Text(payload?["type"]);
        if (type is null) return null;

        var sessionId = HarnessJson.Text(payload?["sessionId"]);
        string? eventType = null;
        string? turnEndKind = null;
        if (type == "session/event")
        {
            eventType = HarnessJson.Text(payload?["event"]?["type"]);
            turnEndKind = HarnessJson.Text(payload?["event"]?["data"]?["reason"]?["kind"]);
        }
        var errorMessage = type == "stream/error" ? HarnessJson.Text(payload?["error"]?["message"]) : null;
        return new HarnessMuxFrame(type, sessionId, eventType, turnEndKind, errorMessage);
    }
}

/// <summary>
/// Harness Web Host 事件流监听（rc.6 原生协议）：WebSocket ws://127.0.0.1:3080/api/events.mux。
/// 帧为文本 JSON { type:"server-request", rpcId, method:帧类型, payload:{ type:"session/event"|..., ... } }；
/// GET /api/events.mux 返回 426 属正常协议提示。服务端关闭或连接失败时枚举结束（不抛出）；
/// 用户取消以 CancellationToken 传播。WebSocket 随枚举结束释放。
/// </summary>
public sealed class DeepSeekHarnessEventStream : IDisposable
{
    private readonly string wsUrl;

    /// <summary>WebSocket 工厂（默认 ClientWebSocket；测试可注入）。</summary>
    public Func<ClientWebSocket>? WebSocketFactory { get; init; }
    /// <summary>单帧字节上限；超限帧被丢弃但不破坏流同步。</summary>
    public int MaxFrameBytes { get; init; } = 8 * 1024 * 1024;

    public DeepSeekHarnessEventStream(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        var scheme = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        wsUrl = $"{scheme}://{uri.Host}:{uri.Port}/api/events.mux";
    }

    public string WebSocketUrl => wsUrl;

    public void Dispose()
    {
        // 连接由 ListenAsync 内的 using 持有并在枚举结束时释放；本类型无长驻资源。
    }

    public async IAsyncEnumerable<HarnessMuxFrame> ListenAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var socket = (WebSocketFactory ?? (() => new ClientWebSocket()))();
        try
        {
            await socket.ConnectAsync(new Uri(wsUrl), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法连接事件流（{wsUrl}）：{HarnessJson.Truncate(ex.Message, 160)}", ex);
        }

        var chunk = new byte[16 * 1024];
        while (true)
        {
            byte[]? bytes;
            using (var memory = new MemoryStream())
            {
                var skipped = false;
                while (true)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await socket.ReceiveAsync(chunk, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"事件流接收失败：{HarnessJson.Truncate(ex.Message, 160)}", ex);
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
                        yield break;
                    }
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        skipped = true;
                        memory.SetLength(0);
                        if (result.EndOfMessage) break;
                        continue;
                    }
                    if (!skipped)
                    {
                        if (memory.Length + result.Count > MaxFrameBytes)
                        {
                            skipped = true;
                            memory.SetLength(0);
                        }
                        else
                        {
                            memory.Write(chunk, 0, result.Count);
                        }
                    }
                    if (result.EndOfMessage) break;
                }
                if (skipped) continue;
                bytes = memory.ToArray();
            }

            var json = Encoding.UTF8.GetString(bytes);
            var frame = HarnessMuxFrame.Parse(json);
            if (frame is not null) yield return frame;
        }
    }
}

/// <summary>
/// 真实 Harness 中继能力探测（rc.6 原生协议）：分别验证 提交（session.create）、
/// 事件流（/api/events.mux WebSocket）、取消（session.cancel）；三项全部通过时
/// Confirmed=true，任何一项失败都诚实降级并给出逐项具体原因。
/// 探测会话在结束时被取消，不运行任何 Agent 回合。对未来合法语义版本保持同一探测策略
/// （入口与能力决定可用性，版本不单独阻止）。
/// </summary>
public sealed class DeepSeekHarnessRelayProbe : IDeepSeekHarnessRelay
{
    private readonly string baseUrl;

    /// <summary>单次 RPC 超时（默认 3 秒）。</summary>
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>事件流等待首帧超时（默认 3 秒）。</summary>
    public TimeSpan EventWaitTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>探测会话的工作目录（默认系统临时目录；必须存在且可读）。</summary>
    public string? ProbeCwd { get; init; }
    /// <summary>HttpClient 工厂（测试可注入）。</summary>
    public Func<HttpClient>? HttpClientFactory { get; init; }
    /// <summary>WebSocket 工厂（测试可注入）。</summary>
    public Func<ClientWebSocket>? WebSocketFactory { get; init; }

    public DeepSeekHarnessRelayProbe(string? baseUrl = null)
        => this.baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DeepSeekHarnessVersions.WebHostDefaultUrl : baseUrl;

    public async Task<HarnessRelayCapability> ProbeCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var http = HttpClientFactory?.Invoke() ?? new HttpClient { Timeout = RpcTimeout };
        using var rpc = new HarnessRpcClient(baseUrl, http);
        var probeCwd = ProbeCwd ?? Path.GetTempPath();

        // 1) 提交能力：创建探测会话（cwd + standard 预设，与正式提交同一载荷形状）。
        var submitOk = false;
        string? submitReason = null;
        string? probeSessionId = null;
        try
        {
            var create = await rpc.CreateSessionAsync(probeCwd, "standard", cancellationToken);
            if (!create.Success)
            {
                submitReason = create.ErrorMessage;
            }
            else
            {
                probeSessionId = create.GetString("sessionId");
                if (string.IsNullOrWhiteSpace(probeSessionId))
                    submitReason = "响应缺少 sessionId";
                else
                    submitOk = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            submitReason = "RPC 异常（" + HarnessJson.Truncate(ex.Message, 160) + "）";
        }

        // 2) 事件流能力：连接 mux，等待任一非错误事件帧。
        //    Host 会在流打开时为每个已附着的会话发出 session/subscribed（含刚创建的探测会话）。
        var eventsOk = false;
        string? eventsReason = null;
        try
        {
            using var stream = new DeepSeekHarnessEventStream(baseUrl) { WebSocketFactory = WebSocketFactory };
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(EventWaitTimeout);
            await foreach (var frame in stream.ListenAsync(wait.Token))
            {
                if (frame.Type == "stream/error")
                {
                    eventsReason = "事件流返回错误（" + HarnessJson.Truncate(frame.ErrorMessage, 160) + "）";
                    break;
                }
                eventsOk = true;
                break;
            }
            if (!eventsOk && eventsReason is null)
                eventsReason = $"在 {EventWaitTimeout.TotalSeconds:0.#} 秒内未收到任何事件帧";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            eventsReason = $"在 {EventWaitTimeout.TotalSeconds:0.#} 秒内未收到任何事件帧";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            eventsReason = "无法连接事件流（" + HarnessJson.Truncate(ex.Message, 160) + "）";
        }

        // 3) 取消能力：取消探测会话。
        var cancelOk = false;
        string? cancelReason = null;
        if (probeSessionId is null)
        {
            cancelReason = "未取得探测会话，无法验证取消";
        }
        else
        {
            try
            {
                var cancel = await rpc.CancelAsync(probeSessionId, cancellationToken);
                if (cancel.Success) cancelOk = true;
                else cancelReason = cancel.ErrorMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancelReason = "RPC 异常（" + HarnessJson.Truncate(ex.Message, 160) + "）";
            }
        }

        var confirmed = submitOk && eventsOk && cancelOk;
        var message = string.Join("；", new[]
        {
            $"提交：{(submitOk ? "通过" : $"失败（{submitReason}）")}",
            $"事件流：{(eventsOk ? "通过" : $"失败（{eventsReason}）")}",
            $"取消：{(cancelOk ? "通过" : $"失败（{cancelReason}）")}"
        }) + (confirmed ? "。中继三项能力均已由运行时探测确认。" : "。中继能力未完全确认，自动提交已降级。");
        return new HarnessRelayCapability(submitOk, eventsOk, cancelOk, confirmed, message);
    }
}
