using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexHelper.Core.Services;

/// <summary>
/// 新版 DSH Gateway 的会话水位读取器：官方 Remote 流多路复用端点
/// <c>/api/remote.mux</c> 上打开一次 <c>session/follow</c> 逻辑流，只取第一个匹配 streamId 的
/// <c>snapshot</c>（含 <c>cursor</c> 打开水位与 <c>records</c>），随后立即发送 <c>cancel</c> 并关闭连接。
/// </summary>
/// <remarks>
/// 约束（与 SPEC 一致，全部为协议事实而非猜测）：
/// <list type="bullet">
/// <item>客户端帧：<c>{type:"open",streamId,endpoint:"session/follow",payload:{args:{request:{address:{kind:"session",sessionId},maxMessages:1}}}}</c>；
/// 刻意不传 <c>assistantStream</c>，因此不消费也不返回任何 assistant 正文/推理增量。</item>
/// <item>服务端帧：<c>{type:"item",streamId,value}</c> / <c>{type:"error",streamId,error}</c> / <c>{type:"end",streamId}</c>；
/// streamId 不匹配的帧一律丢弃，绝不把别人的流当成本会话水位。</item>
/// <item>streamId 是<b>每次读取的局部变量</b>：同一实例并发读取两份会话时各自独立取消自己的逻辑流，
/// 取消逻辑流从不取消 session，也从不订阅 assistantStream。</item>
/// <item>有界：单帧上限 <see cref="MaxFrameChars"/>（对应 2MB 级上限）与整体超时 <see cref="Timeout"/>；
/// 读取与清理都有界——清理最多约 <see cref="CleanupBudget"/>，只发送一次不可取消的 cancel、
/// 再用 <c>CloseOutputAsync</c> / <c>Abort</c> / <c>Dispose</c>，<b>绝不等待对端回应 close</b>，
/// 因此成功/错误/超时/外部取消都必定退出。</item>
/// <item>按字节限额聚合完整 UTF-8：跨 WebSocket 分片的多字节字符（中文等）用增量解码器还原，
/// 绝不按片直接解码损坏字符。</item>
/// <item>数字水位必须是非负安全整数；小数、非有限值、超安全整数范围与负值一律视为不可信，
/// 且 records 内的事件序号不得超过 cursor（超过即放弃该响应，不猜水位）。</item>
/// <item>对外只暴露水位与标准化事件元数据（seq/type/turn-end 结果），绝不返回 records 正文；
/// 错误信息统一脱敏，绝不携带凭据或正文。</item>
/// </list>
/// </remarks>
public sealed class HarnessGatewaySnapshotReader
{
    /// <summary>单帧字符上限（2MB 级；超过即视为不可信大包，直接失败而不是截断后猜水位）。</summary>
    public const int MaxFrameChars = 2_000_000;

    /// <summary>清理阶段的总时间预算：cancel/close/abort 都不得让调用方悬挂。</summary>
    public static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(1);

    /// <summary>默认整体超时：首 snapshot 或终帧到达的上限。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly string muxUrl;
    private readonly TimeSpan timeout;

    /// <param name="baseUrl">Host 基地址（http/https 或 ws/wss 均可，回环）。</param>
    /// <param name="timeout">整体超时；null 时使用 <see cref="DefaultTimeout"/>。</param>
    public HarnessGatewaySnapshotReader(string baseUrl, TimeSpan? timeout = null)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            muxUrl = "wss://" + trimmed["https://".Length..];
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            muxUrl = "ws://" + trimmed["http://".Length..];
        else if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            muxUrl = trimmed;
        else
            muxUrl = "ws://" + trimmed;
        if (!muxUrl.EndsWith(HarnessContractPresetCompatibility.RemoteMuxPath, StringComparison.Ordinal))
            muxUrl += HarnessContractPresetCompatibility.RemoteMuxPath;
        this.timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>实际连接的 mux 地址（只读诊断用；脱敏，不含凭据）。</summary>
    public string MuxUrl => SanitizeUrl(muxUrl);

    /// <summary>本读取器使用的整体超时（诊断用）。</summary>
    public TimeSpan Timeout => timeout;

    /// <summary>一次跟着打开的结果：首个 snapshot 的水位与标准化事件元数据。</summary>
    public sealed record FollowOpening(
        bool Success,
        string? ErrorMessage,
        long? Cursor,
        long? HighestSequence,
        string? LastTurnEndKind,
        IReadOnlyList<string> EventMetadata)
    {
        /// <summary>可用的续接基线水位（只取可信 cursor；缺失即无可信水位）。</summary>
        public long? Baseline => Cursor;
        public JsonArray TerminalEvents { get; init; } = new();
        public JsonObject? CurrentModel { get; init; }

        public static FollowOpening Fail(string message) => new(false, message, null, null, null, Array.Empty<string>());

        /// <summary>有界诊断文本：只含水位与事件类型计数，绝不含 records 正文。</summary>
        public string Describe()
        {
            if (!Success) return "读取失败：" + (ErrorMessage ?? "未知原因");
            return "水位=" + (Cursor?.ToString() ?? "无")
                + "，事件元数据 " + EventMetadata.Count + " 条"
                + (LastTurnEndKind is null ? string.Empty : "，最后 turn/end=" + LastTurnEndKind);
        }
    }

    /// <summary>
    /// 打开一次 follow 并只取首个 snapshot。任何协议异常都返回 <see cref="FollowOpening.Fail"/>，
    /// 由调用方决定"等待/不能确认续接"（绝不默默新建会话）。
    /// </summary>
    public async Task<FollowOpening> ReadOpeningAsync(string sessionId, CancellationToken cancellationToken = default,
        string? explicitStreamId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return FollowOpening.Fail("会话 ID 为空，无法读取 Gateway 水位。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;

        ClientWebSocket? socket = null;
        // 每次读取独立的逻辑流 ID：清理只取消自己的流，并发读取互不影响。
        // 仅测试注入显式 ID（生产路径不传，始终使用新的随机逻辑流）。
        var streamId = string.IsNullOrWhiteSpace(explicitStreamId) ? Guid.NewGuid().ToString("N") : explicitStreamId!;
        try
        {
            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(muxUrl), token);
            var request = new JsonObject
            {
                ["address"] = new JsonObject { ["kind"] = "session", ["sessionId"] = sessionId },
                ["maxMessages"] = 1
            };
            var open = new JsonObject
            {
                ["type"] = "open",
                ["streamId"] = streamId,
                ["endpoint"] = "session/follow",
                ["payload"] = new JsonObject { ["args"] = new JsonObject { ["request"] = request } }
            };
            await SendTextAsync(socket, open.ToJsonString(), token);

            // 按字节限额聚合完整 UTF-8：跨分片的多字节字符由增量解码器还原。
            // 限额按字节计（每片 16KB，先累后判），单条 WebSocket 消息内按序写入，消息结束才解码。
            const int byteLimit = MaxFrameChars;
            var buffer = new byte[16 * 1024];
            using var frameBytes = new MemoryStream();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                WebSocketReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return FollowOpening.Fail("等待 Gateway 首帧超时（" + (int)timeout.TotalSeconds + " 秒内未收到 session/follow 的 snapshot）。");
                }
                if (received.MessageType == WebSocketMessageType.Close)
                    return FollowOpening.Fail("Gateway 在返回 snapshot 之前关闭了 mux 连接（原始状态：" + socket.CloseStatus + "）。");
                if (received.MessageType != WebSocketMessageType.Text)
                    return FollowOpening.Fail("Gateway mux 返回了非文本帧，协议不可信。");
                if (frameBytes.Length + received.Count > byteLimit)
                    return FollowOpening.Fail("Gateway mux 单帧超过上限（" + MaxFrameChars + " 字符），已放弃该响应。");
                frameBytes.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage) continue;

                var frameText = new UTF8Encoding(false, true).GetString(frameBytes.GetBuffer(), 0, (int)frameBytes.Length);
                frameBytes.SetLength(0); // WebSocket EndOfMessage 是 JSON 边界，不能累积到下一条消息。
                if (TryHandleFrame(frameText, streamId, out var opening, out var matched) && matched)
                    return opening!;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FollowOpening.Fail("等待 Gateway 首帧超时（" + (int)timeout.TotalSeconds + " 秒内未收到 session/follow 的 snapshot）。");
        }
        catch (Exception ex)
        {
            return FollowOpening.Fail("连接 Gateway mux 失败（" + MuxUrl + "）：" + HarnessContractPresetCompatibility.Sanitize(ex.Message));
        }
        finally
        {
            // 有界清理：只取消自己的逻辑流并放弃连接，绝不等待对端回应 close。
            if (socket is not null) await CloseBoundedAsync(socket, streamId);
        }
    }

    /// <summary>
    /// 有界清理：发送一次不可取消的 cancel（受 <see cref="CleanupBudget"/> 限制），随后用
    /// <c>CloseOutputAsync</c> 送出 close 帧并立即 <c>Abort</c> 放弃套接字——绝不等待服务端回应，
    /// 也不依赖任何服务端响应，因此对端不回 close 时同样在约 1 秒内退出。
    /// </summary>
    private static async Task CloseBoundedAsync(ClientWebSocket socket, string streamId)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(CleanupBudget);
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await SendTextAsync(socket, new JsonObject { ["type"] = "cancel", ["streamId"] = streamId }.ToJsonString(), cleanup.Token);
                }
                catch { /* 取消帧发送失败不影响已读到的水位结论 */ }
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "snapshot-read-complete", cleanup.Token);
                }
                catch { /* 对端不回应/连接已坏：继续放弃套接字 */ }
            }
        }
        catch { /* 清理本身绝不向外抛异常 */ }
        finally
        {
            try { socket.Abort(); } catch { }
            try { socket.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 解析一帧。返回 handled=true 表示该 streamId 已得到结论（snapshot 成功或 error/end 失败）；
    /// matched=false 表示帧不属于本流（调用方继续读）。
    /// </summary>
    private bool TryHandleFrame(string frameText, string streamId, out FollowOpening? opening, out bool matched)
    {
        opening = null;
        matched = false;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(frameText); }
        catch { return false; } // 非 JSON 帧：忽略（不把噪声当水位）。
        if (parsed is not JsonObject frame) return false;
        var frameStreamId = HarnessJson.Text(frame["streamId"]);
        if (!string.Equals(frameStreamId, streamId, StringComparison.Ordinal)) return false;
        matched = true;
        var type = HarnessJson.Text(frame["type"]);
        switch (type)
        {
            case "item":
                var value = frame["value"];
                if (value is not JsonObject valueObject) return false;
                var frameType = HarnessJson.Text(valueObject["type"]);
                if (!string.Equals(frameType, "snapshot", StringComparison.Ordinal))
                    return false; // follow 的后续事件帧：首 snapshot 已足够，不需要消费。
                opening = ParseSnapshot(valueObject);
                return true;
            case "error":
                var error = frame["error"] as JsonObject;
                var code = HarnessJson.Text(error?["code"]) ?? "unknown";
                var message = HarnessContractPresetCompatibility.Sanitize(HarnessJson.Text(error?["message"]));
                opening = FollowOpening.Fail("Gateway 返回错误帧（" + HarnessContractPresetCompatibility.Sanitize(code) + "）：" + message);
                return true;
            case "end":
                opening = FollowOpening.Fail("Gateway 在返回 snapshot 之前结束了该流（未取得水位，不能确认续接）。");
                return true;
            default:
                matched = false;
                return false; // 未知帧类型：保守忽略。
        }
    }

    /// <summary>
    /// 从 snapshot 帧抽取水位与标准化事件元数据（seq/type/最后 turn/end），绝不保留正文。
    /// cursor 必须是可信的非负安全整数；records 内序号不得超过 cursor，也不得为小数/负数。
    /// </summary>
    private FollowOpening ParseSnapshot(JsonObject snapshot)
    {
        var cursor = ReadWatermark(snapshot["cursor"]);
        if (cursor is null)
            return FollowOpening.Fail("Gateway snapshot 缺少可信 cursor 水位（必须是非负整数；未取得水位，不能确认续接）。");

        long? highest = null;
        string? lastTurnEnd = null;
        var metadata = new List<string>();
        var terminalEvents = new Queue<JsonObject>();
        if (snapshot["records"] is JsonArray records)
        {
            foreach (var node in records)
            {
                if (node is not JsonObject record) continue;
                var sequence = ReadEventSequence(record);
                if ((record["event"]?["seq"] is not null || record["seq"] is not null) && sequence is null)
                    return FollowOpening.Fail("Gateway snapshot 包含不可信的事件序号。");
                if (sequence is not null)
                {
                    // records 序号不能超过 cursor：超过即水位不可信，绝不猜。
                    if (sequence.Value > cursor.Value)
                        return FollowOpening.Fail("Gateway snapshot 的 records 序号（" + sequence.Value
                            + "）超过 cursor 水位（" + cursor.Value + "），水位不可信，不能确认续接。");
                    metadata.Add(sequence.Value + ":" + (ReadEventType(record) ?? "unknown"));
                    if (highest is null || sequence.Value > highest.Value) highest = sequence.Value;
                }
                var eventType = ReadEventType(record);
                if (sequence is not null && eventType is "turn/start" or "turn/end")
                {
                    var data = record["event"]?["data"] as JsonObject ?? record["data"] as JsonObject;
                    var kind = HarnessTurnEnd.Coerce(data, ReadTurnEndKind(record));
                    if (eventType == "turn/end") lastTurnEnd = kind;
                    terminalEvents.Enqueue(new JsonObject { ["event"] = new JsonObject
                    {
                        ["seq"] = sequence.Value, ["type"] = eventType,
                        ["data"] = new JsonObject { ["reason"] = new JsonObject { ["kind"] = kind } }
                    } });
                    if (terminalEvents.Count > 64) terminalEvents.Dequeue();
                }
            }
        }
        // 只保留有界元数据（64 条足够判断终态），绝不累积任意长正文。
        if (metadata.Count > 64)
        {
            var kept = metadata.GetRange(metadata.Count - 64, 64);
            metadata.Clear();
            metadata.AddRange(kept);
        }
        var selection = snapshot["projections"]?["values"]?["modelSelection"] as JsonObject;
        var next = selection?["next"] as JsonObject;
        JsonObject? current = null;
        var provider = HarnessJson.Text(next?["provider"]);
        var model = HarnessJson.Text(next?["model"]);
        if (!string.IsNullOrWhiteSpace(provider) && provider.Length <= 256
            && !string.IsNullOrWhiteSpace(model) && model.Length <= 512)
        {
            current = new JsonObject { ["provider"] = provider, ["model"] = model };
            var effort = HarnessJson.Text(next?["reasoningEffort"]);
            if (!string.IsNullOrWhiteSpace(effort) && effort.Length <= 128) current["reasoningEffort"] = effort;
        }
        return new FollowOpening(true, null, cursor, highest, lastTurnEnd, metadata)
        {
            TerminalEvents = new JsonArray(terminalEvents.Cast<JsonNode?>().ToArray()),
            CurrentModel = current
        };
    }

    /// <summary>record 的标准化事件序号：同时兼容 event 包裹与扁平两种形状，缺失/不可信时返回 null。</summary>
    private static long? ReadEventSequence(JsonObject record)
    {
        var @event = record["event"] as JsonObject;
        return ReadWatermark(@event?["seq"]) ?? ReadWatermark(record["seq"]);
    }

    /// <summary>record 的事件类型（只用于终态诊断，不含正文）。</summary>
    private static string? ReadEventType(JsonObject record)
    {
        var @event = record["event"] as JsonObject;
        return HarnessJson.Text(@event?["type"]) ?? HarnessJson.Text(record["type"]);
    }

    /// <summary>turn/end 的结束原因（completed / aborted / 长度截断等），只取 kind/stopReason 这类短标识。</summary>
    private static string? ReadTurnEndKind(JsonObject record)
    {
        var @event = record["event"] as JsonObject;
        var data = @event?["data"] as JsonObject ?? record["data"] as JsonObject;
        var kind = HarnessJson.Text((data?["reason"] as JsonObject)?["kind"])
            ?? HarnessJson.Text(data?["stopReason"])
            ?? HarnessJson.Text(data?["stop_reason"])
            ?? HarnessJson.Text(data?["kind"]);
        if (string.IsNullOrWhiteSpace(kind)) return null;
        return kind!.Length <= 40 ? kind : kind[..40];
    }

    /// <summary>
    /// 读取可信序号水位：只接受非负安全整数。小数（如 3.5）、负数、非有限值、超 long 范围的
    /// 数值与字符串数字一律返回 null——绝不把小数截断成整数、绝不用 0 或伪序号代替错误。
    /// </summary>
    private static long? ReadWatermark(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        try
        {
            if (value.TryGetValue<long>(out var number))
                return number >= 0 && number <= 9007199254740991L ? number : null;
            if (value.TryGetValue<int>(out var intNumber))
                return intNumber >= 0 ? intNumber : null;
            // 其他数字类型（decimal/double 等）都可能是小数：只有确为整数且在安全范围内才接受。
            if (value.TryGetValue<decimal>(out var decimalNumber))
                return decimal.Truncate(decimalNumber) == decimalNumber && decimalNumber >= 0 && decimalNumber <= 9007199254740991L
                    ? (long)decimalNumber
                    : null;
            if (value.TryGetValue<double>(out var doubleNumber))
                return !double.IsNaN(doubleNumber) && !double.IsInfinity(doubleNumber)
                    && Math.Truncate(doubleNumber) == doubleNumber && doubleNumber >= 0 && doubleNumber <= 9007199254740991L
                    ? (long)doubleNumber
                    : null;
        }
        catch
        {
            // JSON 数字精度受限（超 long 等）时视为无水位，绝不用 0 代替错误。
        }
        return null;
    }

    /// <summary>脱敏 mux 地址诊断：只保留 scheme://host:port + 路径，去掉可能的凭据/查询串。</summary>
    private static string SanitizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return HarnessContractPresetCompatibility.Sanitize(url);
        return uri.Scheme + "://" + uri.Authority + uri.AbsolutePath;
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }
}
