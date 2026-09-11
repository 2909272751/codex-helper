using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexHelper.Core.Services;

public sealed partial class DeepSeekHarnessEventStream
{
    private async IAsyncEnumerable<HarnessMuxFrame> ListenGatewayAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SessionIdFilter)) throw new InvalidOperationException("Gateway 订阅必须指定会话。");
        using var socket = (WebSocketFactory ?? (() => new ClientWebSocket()))();
        using var abort = cancellationToken.Register(() => { try { socket.Abort(); } catch { } });
        var streamId = Guid.NewGuid().ToString("N");
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new Uri(wsUrl.Replace("/api/events.mux", "/api/remote.mux", StringComparison.Ordinal)), connect.Token);
            var open = new JsonObject
            {
                ["type"] = "open", ["streamId"] = streamId, ["endpoint"] = "session/follow",
                ["payload"] = new JsonObject { ["args"] = new JsonObject { ["request"] = new JsonObject
                {
                    ["address"] = new JsonObject { ["kind"] = "session", ["sessionId"] = SessionIdFilter },
                    ["maxMessages"] = 1
                } } }
            };
            await socket.SendAsync(Encoding.UTF8.GetBytes(open.ToJsonString()), WebSocketMessageType.Text, true, connect.Token);
            var buffer = new byte[16 * 1024];
            using var bytes = new MemoryStream();
            var subscribed = false;
            while (true)
            {
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) yield break;
                if (received.MessageType != WebSocketMessageType.Text || bytes.Length + received.Count > MaxFrameBytes)
                    throw new InvalidOperationException("Gateway 事件帧类型或大小不可信。");
                bytes.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage) continue;
                var json = new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
                bytes.SetLength(0);
                if (JsonNode.Parse(json) is not JsonObject frame || HarnessJson.Text(frame["streamId"]) != streamId) continue;
                var type = HarnessJson.Text(frame["type"]);
                if (type == "error") throw new InvalidOperationException("Gateway 订阅返回错误，等待重连核验。");
                if (type == "end") yield break;
                if (type != "item" || frame["value"] is not JsonObject value) continue;
                var valueType = HarnessJson.Text(value["type"]);
                JsonArray records;
                if (valueType == "snapshot")
                {
                    if (value["cursor"] is not JsonValue cursorValue || !cursorValue.TryGetValue<long>(out var cursor)
                        || cursor < 0 || cursor > 9007199254740991L)
                        throw new InvalidOperationException("Gateway 订阅缺少可信水位。");
                    if (!subscribed)
                    {
                        subscribed = true;
                        // 不把 snapshot 的最高水位冒充已消费序号：尾窗里的真实终态仍需逐条处理。
                        yield return new HarnessMuxFrame("session/subscribed", SessionIdFilter, null, null, null);
                    }
                    records = value["records"] as JsonArray ?? new JsonArray();
                }
                else if (valueType == "event" && subscribed)
                    records = new JsonArray(value.DeepClone());
                else continue;
                foreach (var record in records)
                {
                    if (record?["event"] is not JsonObject evt) continue;
                    var legacy = new JsonObject { ["type"] = "server-request", ["payload"] = new JsonObject
                    {
                        ["type"] = "session/event", ["sessionId"] = SessionIdFilter, ["event"] = evt.DeepClone()
                    } };
                    var parsed = HarnessMuxFrame.Parse(legacy.ToJsonString());
                    if (parsed is not null && parsed.Seq is >= 0) yield return parsed;
                }
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    var cancel = new JsonObject { ["type"] = "cancel", ["streamId"] = streamId };
                    await socket.SendAsync(Encoding.UTF8.GetBytes(cancel.ToJsonString()), WebSocketMessageType.Text, true, cleanup.Token);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cleanup.Token);
                }
            }
            catch { }
            finally { socket.Abort(); }
        }
    }
}
