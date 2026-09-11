using System.Net.WebSockets;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
/// DSH 终态 reason 的统一识别：区分"正常完成"、"用户中止"与"单回合输出达到 max-tokens 上限截断"。
/// 上游用 stopReason="length" 表示输出因长度上限结束；Host 侧的 turn/end reason.kind 兼容多种拼写
/// （length / max-tokens / max_tokens / token-limit 等）。只有同一合同会话以此原因结束时，Runner 才
/// 向同一 Session 提交一次短恢复提示（绝不新建 Session、绝不无限重试）。
/// </summary>
internal static class HarnessTurnEnd
{
    /// <summary>判定 reason.kind 是否属于 max-tokens 截断终态（大小写/连字符/下划线宽容）。</summary>
    public static bool IsMaxTokenKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        var normalized = kind.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized is "length" or "max_tokens" or "max_token" or "token_limit" or "max_output_tokens" or "completion_tokens";
    }

    /// <summary>判定 stopReason 文本是否为长度截断（"length" 及常见同义值）。</summary>
    public static bool IsLengthStopReason(string? stopReason)
        => stopReason is not null && IsMaxTokenKind(stopReason);

    /// <summary>
    /// 归一化 turn/end 的结束原因：reason.kind 优先；kind 未覆盖 max-token 时再检查
    /// reason.stopReason / data.stopReason 是否为 "length"，命中则归一为 "length"。
    /// 返回值直接交给 <see cref="MapTurnEnd"/> 语义映射。
    /// </summary>
    public static string? Coerce(JsonObject? eventData, string? kind)
    {
        var reason = eventData?["reason"] as JsonObject;
        if (kind == "aborted")
            return HarnessJson.Text(reason?["reason"]?["kind"]) is "error" or "failed" ? "error" : "aborted";
        if (IsMaxTokenKind(kind)) return kind;
        if (reason is not null && IsLengthStopReason(HarnessJson.Text(reason["stopReason"]))) return "length";
        if (IsLengthStopReason(HarnessJson.Text(eventData?["stopReason"]))) return "length";
        if (eventData is not null && IsLengthStopReason(HarnessJson.Text(eventData["stop_reason"]))) return "length";
        return kind;
    }
}

/// <summary>DSH 模型目录条目（provider、Host 原始模型 ID、Host 显示名与可选思考强度；全部来自运行时 RPC，不硬编码）。</summary>
public sealed record HarnessModelEntry(string Provider, string ModelId, string DisplayName, IReadOnlyList<string> Efforts)
{
    /// <summary>目录键：provider + 模型 ID（与 <see cref="HarnessSessionModels"/> 判定一致）。</summary>
    public string Key => Provider + "\u0000" + ModelId;

    /// <summary>下拉框展示文本：Host 显示名（provider 分组名）+ 原始 ID；保存的永远是原始 ID。</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? ModelId : $"{DisplayName}（{ModelId}）";
}

/// <summary>
/// session.models 响应的可信解析（形状取自 DSH rc.6 Host 的 sessionModelsValueSchema，已由运行时 RPC 复核）：
/// <c>{ current:{provider,model,reasoningEffort?}, routable:boolean, groups:[{id,name,models:[{id,name}]}], failures:[{id,name,message}] }</c>。
/// <para>
/// 关键事实（不得凭字符串推断）：<c>routable</c> 只表示 <b>provider</b> 是否有适配器
/// （Host 内部即 <c>routeServed(current.provider)</c>），它 <b>不</b> 表示 current.model 仍在目录中。
/// 因此"当前模型是否仍可路由"必须同时满足：存在 <c>groups[].id == current.provider</c> 的分组，
/// 且该分组的 <c>models[].id</c> 含 <c>current.model</c>。模型 ID 一律原样保留
/// （DSH 返回的就是 provider-qualified ID），绝不拼接前缀、绝不硬编码别名或 UI 名称。
/// </para>
/// </summary>
public sealed record HarnessSessionModels(
    string? Provider,
    string? Model,
    bool ProviderRoutable,
    IReadOnlySet<string> CatalogKeys,
    IReadOnlySet<string> CatalogProviderIds,
    IReadOnlyList<HarnessModelEntry> Available)
{
    /// <summary>解析 session.models 的 value；value 不是对象时返回 null（形状不可信）。</summary>
    public static HarnessSessionModels? Parse(JsonNode? value)
    {
        if (value is not JsonObject root) return null;
        var current = root["current"] as JsonObject;
        var provider = HarnessJson.Text(current?["provider"]);
        var model = HarnessJson.Text(current?["model"]);
        var routable = root["routable"] is JsonValue routableValue
            && routableValue.TryGetValue<bool>(out var flag) && flag;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var providers = new HashSet<string>(StringComparer.Ordinal);
        var available = new List<HarnessModelEntry>();
        if (root["groups"] is JsonArray groups)
        {
            foreach (var node in groups)
            {
                if (node is not JsonObject group) continue;
                var groupId = HarnessJson.Text(group["id"]);
                if (string.IsNullOrWhiteSpace(groupId)) continue;
                providers.Add(groupId);
                var groupName = HarnessJson.Text(group["name"]) ?? groupId;
                if (group["models"] is not JsonArray models) continue;
                foreach (var entry in models)
                {
                    if (entry is not JsonObject modelEntry) continue;
                    var modelId = HarnessJson.Text(modelEntry["id"]);
                    if (string.IsNullOrWhiteSpace(modelId)) continue;
                    keys.Add(Key(groupId, modelId));
                    var efforts = new List<string>();
                    if ((modelEntry["reasoning"] as JsonObject)?["efforts"] is JsonArray effortList)
                        foreach (var effort in effortList)
                        {
                            var effortId = HarnessJson.Text((effort as JsonObject)?["id"]);
                            if (!string.IsNullOrWhiteSpace(effortId)) efforts.Add(effortId!);
                        }
                    // 显示名只用于 UI 展示；保存的选择一律使用 provider + 模型原始 ID。
                    var display = HarnessJson.Text(modelEntry["name"]);
                    available.Add(new HarnessModelEntry(groupId, modelId,
                        string.IsNullOrWhiteSpace(display) ? modelId : $"{groupName} · {display}", efforts));
                }
            }
        }
        return new HarnessSessionModels(provider, model, routable, keys, providers, available);
    }

    /// <summary>目录键：provider + 模型 ID（两者都来自 DSH，原样保存）。</summary>
    private static string Key(string provider, string model) => provider + "\u0000" + model;

    /// <summary>当前 provider-qualified 模型 ID（DSH 返回值原样）；缺失时为 null。</summary>
    public string? CurrentModelId => string.IsNullOrWhiteSpace(Model) ? null : Model;

    /// <summary>当前模型是否仍在 DSH 当前可路由模型目录中（provider 有适配器 + 分组内确有该模型 ID）。</summary>
    public bool CurrentModelInCatalog
        => ProviderRoutable
           && !string.IsNullOrWhiteSpace(Provider)
           && CurrentModelId is not null
           && CatalogKeys.Contains(Key(Provider!, CurrentModelId!));

    /// <summary>可诊断的目录摘要（只含 provider 标识与条目计数，无任何凭据/正文）。</summary>
    public string CatalogSummary
        => CatalogKeys.Count == 0
            ? "目录为空或缺席"
            : "当前可路由 provider：" + string.Join("/", CatalogProviderIds) + "（共 " + CatalogKeys.Count + " 个模型）";
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
    private enum ProtocolFlavor { Unknown, LegacyDotRpc, GatewaySlashRpc }
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string baseUrl;
    private ProtocolFlavor protocol;

    /// <param name="baseUrl">Host 基地址（默认 http://127.0.0.1:3080）。</param>
    /// <param name="http">可选注入的 HttpClient（默认新建，Timeout 30 秒，随实例释放）。</param>
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
            // session.history 可能携带长会话的事件页；10 秒会把仍健康的历史读取误判为不可用，
            // 进而破坏安全的连续会话。30 秒仅扩大单次 RPC 的容错窗口，不改变取消语义。
            this.http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
            ownsHttp = true;
        }
    }

    public string BaseUrl => baseUrl;

    /// <summary>当前 Host 经只读探测确认的协议。null 表示尚未调用 RPC。</summary>
    public string? ProtocolName => protocol switch
    {
        ProtocolFlavor.LegacyDotRpc => "legacy-dot-rpc",
        ProtocolFlavor.GatewaySlashRpc => "gateway-slash-rpc",
        _ => null
    };

    /// <summary>新版公开 Gateway 已由只读 session/list 探测确认。</summary>
    public bool UsesGatewayProtocol => protocol == ProtocolFlavor.GatewaySlashRpc;

    /// <summary>
    /// 远程 Origin 验证用：为非空时随请求发送 <c>Origin</c> 头（形如 <c>http://authority</c>）。
    /// 默认 null（本机回环调用不需要）；绝不携带凭据、cookie 或 token。
    /// </summary>
    public string? OriginHeader { get; init; }

    /// <summary>读取运行中会话数的保守估计：只读 session.list，统计 running=true 的条目。</summary>
    public async Task<int> CountRunningSessionsAsync(CancellationToken cancellationToken = default)
    {
        var list = await ListSessionsAsync(cancellationToken);
        if (!list.Success) throw new InvalidOperationException(list.ErrorMessage ?? "session.list 失败。");
        var sessions = list.Value?["sessions"] as JsonArray ?? list.Value?["items"] as JsonArray;
        if (sessions is null) return 0;
        var count = 0;
        foreach (var node in sessions)
        {
            if (node is not JsonObject session) continue;
            if (session["running"] is JsonValue running && running.TryGetValue<bool>(out var flag) && flag) count++;
        }
        return count;
    }

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

    /// <summary>
    /// 旧版协议的历史事件读取（<c>session.history</c>，仅 legacy 端点存在）。
    /// 新版 Gateway 不公开旧入口；自动改用 follow 的小尾窗，只返回可信终态元数据。
    /// 不把旧接口请求发到新版 Host，避免将接口不存在误判成任务失败。
    /// </summary>
    public async Task<HarnessRpcResult> GetSessionHistoryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureProtocolAsync(cancellationToken);
        if (protocol == ProtocolFlavor.GatewaySlashRpc)
            return await GetGatewaySnapshotBaselineAsync(sessionId, cancellationToken);
        return await CallWireAsync("session.history", "session.history", new JsonObject { ["sessionId"] = sessionId }, cancellationToken);
    }

    /// <summary>
    /// 轻量历史/基线读取（旧版协议路径）：以 DSH 实际支持的 session.history 分页参数请求最小尾部窗口，
    /// 只取 projections.asOfSeq（会话级可信最高序号，旧 Host 可能缺失），绝不下载完整事件列表。
    /// DSH rc.6 schema：请求可带 beforeSeq（向后分页锚点）与 maxMessages（正整数，按消息数
    /// 从窗口尾部向前分页，chunk 按 sourceEventSeqs 分组不切消息）；省略 beforeSeq 时 Host
    /// 在响应中附带 projections 块（asOfSeq = 会话投影水位）。这里 maxMessages=1 只取最后
    /// 一组消息，响应有界、远小于 2MB 上限；旧 Host 会把未知键剥离并返回默认大页，此时
    /// 仍由 CallAsync 的 2MB 上限保守截断，语义与既有行为一致（宁可不续接也不猜基线）。
    /// </summary>
    public Task<HarnessRpcResult> GetSessionHistoryBaselineAsync(string sessionId, CancellationToken cancellationToken = default)
        => CallAsync("session.history", new JsonObject
        {
            ["sessionId"] = sessionId,
            // 消息数而非事件数：尾页按消息分组，单个大回合的 chunk 仍会整组返回；
            // 对已完成前序"最近一次 turn/end 后"的基线探测足够且响应有界。
            ["maxMessages"] = 1
        }, cancellationToken);

    /// <summary>
    /// 新版 Gateway 的事件基线/水位读取：官方 Remote 流多路复用端点上的 <c>session/follow</c>
    /// 首个 <c>snapshot</c> 的 <c>cursor</c>（会话级可信最高序号）。结果归一成与旧
    /// <c>session.history</c> 兼容的形状（<c>projections.asOfSeq</c>），调用方无需分支；
    /// 不传 <c>assistantStream</c>，不消费也不返回任何正文/推理增量。
    /// 缺少水位、无法验证或临时网络失败时返回失败并保留会话 ID（由调用方写明"等待/不能确认续接"），
    /// 绝不用 0 或伪序号代替错误，也绝不把 session/page 当无 throughSeq 的 history。
    /// </summary>
    public async Task<HarnessRpcResult> GetGatewaySnapshotBaselineAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var reader = new HarnessGatewaySnapshotReader(baseUrl);
        var opening = await reader.ReadOpeningAsync(sessionId, cancellationToken);
        if (!opening.Success) return HarnessRpcResult.Fail(opening.ErrorMessage ?? "Gateway snapshot 读取失败。");
        var baseline = opening.Baseline;
        if (baseline is null || baseline < 0)
            return HarnessRpcResult.Fail("Gateway snapshot 缺少可信水位（不是 0，也不猜测），不能确认续接。");
        var value = new JsonObject
        {
            ["projections"] = new JsonObject { ["asOfSeq"] = baseline.Value },
            ["source"] = "gateway-follow-snapshot",
            ["cursor"] = baseline.Value,
            ["events"] = opening.TerminalEvents.DeepClone()
        };
        if (opening.HighestSequence is not null) value["highestSeq"] = opening.HighestSequence.Value;
        if (opening.LastTurnEndKind is not null) value["lastTurnEnd"] = opening.LastTurnEndKind;
        return HarnessRpcResult.Ok(value);
    }

    /// <summary>
    /// 事件基线读取入口：按当前已探测协议选择实现，绝不混用两种协议。
    /// 旧版继续用 <c>session.history, maxMessages=1</c>；新版走官方 follow 水位。
    /// </summary>
    public async Task<HarnessRpcResult> GetSessionBaselineAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureProtocolAsync(cancellationToken);
        return protocol == ProtocolFlavor.GatewaySlashRpc
            ? await GetGatewaySnapshotBaselineAsync(sessionId, cancellationToken)
            : await GetSessionHistoryBaselineAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// session.models：读取一个会话已选中的模型（current，provider-qualified ID）以及 DSH 当前
    /// 可路由模型目录（groups/failures）。该信息不在 session.list 投影中保证存在。
    /// 形状见 <see cref="HarnessSessionModels"/>；DSH 侧唯一合法的选模方式是 session.selectModel
    /// （payload {sessionId,provider,model,reasoningEffort?}，失败码 model-unavailable），
    /// 本 Helper 不猜测替代模型，因此刻意不代为选模，只在目录不可达时诚实拒绝提交。
    /// </summary>
    public async Task<HarnessRpcResult> GetSessionModelsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureProtocolAsync(cancellationToken);
        if (protocol != ProtocolFlavor.GatewaySlashRpc)
            return await CallAsync("session.models", new JsonObject { ["sessionId"] = sessionId }, cancellationToken);

        // Gateway 的会话模型只来自官方 follow 的 modelSelection.next；Host default 不是会话选择。
        var catalogTask = CallGatewayAsync("session/modelCatalog", new JsonObject { ["args"] = new JsonObject() }, cancellationToken);
        var selectionTask = new HarnessGatewaySnapshotReader(baseUrl).ReadOpeningAsync(sessionId, cancellationToken);
        await Task.WhenAll(catalogTask, selectionTask);
        var catalog = await catalogTask;
        if (!catalog.Success || catalog.Value is not JsonObject catalogValue) return catalog;
        var snapshot = await selectionTask;
        if (!snapshot.Success) return HarnessRpcResult.Fail(snapshot.ErrorMessage ?? "会话模型投影无法核验。");
        var normalized = (JsonObject)catalogValue.DeepClone();
        if (normalized["routable"] is null) normalized["routable"] = true;
        normalized.Remove("current");
        if (snapshot.CurrentModel is not null) normalized["current"] = snapshot.CurrentModel.DeepClone();
        return HarnessRpcResult.Ok(normalized);
    }

    /// <summary>
    /// session.selectModel：DSH 官方唯一的会话选模入口。请求形状取自 rc.6 Host 的
    /// sessionSelectModelRequestSchema：<c>{ sessionId, provider, model, reasoningEffort? }</c>
    /// （provider 与 model 都是 Host 的原样标识，不拼接、不改写）；响应 value 为
    /// <c>{ selected:{ provider, model, reasoningEffort? } }</c>，失败码 model-unavailable（该 provider/model
    /// 不可用）或 agent-busy（会话被占用）。调用方必须在成功后再次读 session.models 确认 current 一致。
    /// </summary>
    public Task<HarnessRpcResult> SelectModelAsync(string sessionId, string provider, string model, string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["provider"] = provider,
            ["model"] = model
        };
        // 只在保存了思考强度时携带；空值必须省略，否则 Host 会用空字符串校验 effort 并拒绝。
        if (!string.IsNullOrWhiteSpace(reasoningEffort)) payload["reasoningEffort"] = reasoningEffort;
        return CallAsync("session.selectModel", payload, cancellationToken);
    }

    /// <summary>执行一次一元 RPC；网络/协议/业务错误均转换为可读失败，不抛出（取消除外）。</summary>
    public async Task<HarnessRpcResult> CallAsync(string method, JsonNode? payload, CancellationToken cancellationToken = default)
    {
        await EnsureProtocolAsync(cancellationToken);
        if (protocol == ProtocolFlavor.GatewaySlashRpc)
        {
            if (!TryMapGateway(method, payload, out var endpoint, out var gatewayPayload))
                return HarnessRpcResult.Fail($"当前 DSH Gateway 未公开 {method} 的兼容入口；未提交或猜测请求。");
            return await CallGatewayAsync(endpoint, gatewayPayload, cancellationToken);
        }
        return await CallWireAsync(method, method, payload ?? new JsonObject(), cancellationToken);
    }

    /// <summary>
    /// 只读协议探测：优先新版 session/list，再回退旧 session.list。禁止通过 create/prompt
    /// 猜测协议，避免一次探测就产生孤儿会话或消耗模型额度。
    /// </summary>
    private async Task EnsureProtocolAsync(CancellationToken cancellationToken)
    {
        if (protocol != ProtocolFlavor.Unknown) return;
        var gateway = await CallWireAsync("session/list", "session/list", new JsonObject
        {
            ["args"] = new JsonObject { ["_request"] = new JsonObject() }
        }, cancellationToken);
        // 只把具有新版 list 固定结果形状（items 数组）的响应视为 Gateway。某些旧
        // Host 会把未知路径宽松地回 200/{}，不能因此误选新版并把后续写请求送错端点。
        if (gateway.Success && gateway.Value?["items"] is JsonArray)
        {
            protocol = ProtocolFlavor.GatewaySlashRpc;
            return;
        }
        var legacy = await CallWireAsync("session.list", "session.list", new JsonObject(), cancellationToken);
        if (legacy.Success)
        {
            protocol = ProtocolFlavor.LegacyDotRpc;
            return;
        }
        // 保留旧协议路径以给调用方提供其原始、可读的失败原因；不把未知协议伪装成可用。
        protocol = ProtocolFlavor.LegacyDotRpc;
    }

    private static bool TryMapGateway(string method, JsonNode? payload, out string endpoint, out JsonObject gatewayPayload)
    {
        endpoint = string.Empty;
        gatewayPayload = new JsonObject();
        var value = payload as JsonObject ?? new JsonObject();
        switch (method)
        {
            case "session.list":
                endpoint = "session/list";
                gatewayPayload["args"] = new JsonObject { ["_request"] = value.DeepClone() };
                return true;
            case "session.create":
            case "session.cancel":
            case "session.selectModel":
                endpoint = method.Replace('.', '/');
                gatewayPayload["args"] = new JsonObject { ["request"] = value.DeepClone() };
                return true;
            case "session.prompt":
                endpoint = "session/prompt";
                var request = (JsonObject)value.DeepClone();
                request["requestId"] ??= Guid.NewGuid().ToString("N");
                gatewayPayload["args"] = new JsonObject { ["request"] = request };
                return true;
            default:
                return false;
        }
    }

    private Task<HarnessRpcResult> CallGatewayAsync(string endpoint, JsonObject payload, CancellationToken cancellationToken)
        => CallWireAsync(endpoint, endpoint, payload, cancellationToken);

    /// <summary>执行已明确协议的单次 RPC；响应信封校验始终相同。</summary>
    private async Task<HarnessRpcResult> CallWireAsync(string endpoint, string method, JsonNode? payload, CancellationToken cancellationToken)
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/{endpoint}") { Content = content };
        if (!string.IsNullOrWhiteSpace(OriginHeader))
        {
            try { request.Headers.TryAddWithoutValidation("Origin", OriginHeader.Trim()); } catch { /* 非法头不阻断本机调用 */ }
        }
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
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

            JsonNode? parsed;
            try { parsed = JsonNode.Parse(text); }
            catch { return HarnessRpcResult.Fail("Harness 响应不是有效 JSON。"); }
            if (parsed is not JsonObject root)
                return HarnessRpcResult.Fail("Harness 响应 JSON 根节点不是对象。");

            if (!string.Equals(HarnessJson.Text(root["type"]), "server-response", StringComparison.Ordinal))
                return HarnessRpcResult.Fail("Harness 响应信封类型无效。");
            if (!string.Equals(HarnessJson.Text(root["rpcId"]), rpcId, StringComparison.Ordinal))
                return HarnessRpcResult.Fail("RPC 响应回显不匹配，已丢弃该响应。");

            if (root["result"] is not JsonObject result)
                return HarnessRpcResult.Fail("Harness 响应 result 不是对象。");
            var ok = result["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var okBool) && okBool;
            if (ok) return HarnessRpcResult.Ok(result["value"]);

            var error = result["error"] as JsonObject;
            var code = HarnessJson.Text(error?["code"]) ?? "unknown";
            var message = HarnessJson.Text(error?["message"]);
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
            "model-unavailable" => "所选 provider/模型在该会话不可用（模型可能已下线或未配置）",
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

/// <summary>
/// 解析后的 mux 事件帧（只保留 runner 需要的结构化字段，不保留正文/密钥等敏感内容）。
/// 文本增量与工具参数增量都做长度截断，仅用于进程内有界退化检测，绝不落盘。
/// </summary>
public sealed record HarnessMuxFrame(
    string Type,
    string? SessionId,
    string? EventType,
    string? TurnEndKind,
    string? ErrorMessage,
    long? Seq = null,
    string? AssistantChunkKind = null,
    string? TextDelta = null,
    string? ToolName = null,
    string? ToolArgsDelta = null,
    long? SubscriptionLastSeq = null,
    string? ToolCallId = null)
{
    /// <summary>assistant 增量文本保留上限（防记录完整合同正文/超大内容）。</summary>
    public const int MaxTextDeltaChars = 2048;
    /// <summary>工具参数增量保留上限。</summary>
    public const int MaxToolArgsDeltaChars = 1024;

    /// <summary>解析服务端文本帧；非法/无关帧返回 null（静默忽略，不中断流）。</summary>
    public static HarnessMuxFrame? Parse(string json)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(json); }
        catch { return null; }
        // Host 可能在事件通道插入 JSON 原始值/心跳；它们不是协议帧，必须静默忽略，
        // 不能因 JsonNode 的索引器类型异常把健康 Relay 整体降级。
        if (parsed is not JsonObject root) return null;
        if (!string.Equals(HarnessJson.Text(root["type"]), "server-request", StringComparison.Ordinal)) return null;
        if (root["payload"] is not JsonObject payload) return null;
        var type = HarnessJson.Text(payload["type"]);
        if (type is null) return null;

        var sessionId = HarnessJson.Text(payload["sessionId"]);
        string? eventType = null;
        string? turnEndKind = null;
        long? seq = null;
        string? assistantChunkKind = null;
        string? textDelta = null;
        string? toolName = null;
        string? toolArgsDelta = null;
        string? toolCallId = null;
        long? subscriptionLastSeq = null;
        if (type == "session/subscribed"
            && payload["lastSeq"] is JsonValue baselineValue
            && baselineValue.TryGetValue<long>(out var baseline))
            subscriptionLastSeq = baseline;
        if (type == "session/event")
        {
            if (payload["event"] is not JsonObject eventNode) return null;
            eventType = HarnessJson.Text(eventNode["type"]);
            var eventData = eventNode["data"] as JsonObject;
            var reason = eventData?["reason"] as JsonObject;
            turnEndKind = HarnessTurnEnd.Coerce(eventData, HarnessJson.Text(reason?["kind"]));
            if (eventNode["seq"] is JsonValue seqValue && seqValue.TryGetValue<long>(out var seqNumber))
                seq = seqNumber;
            // 非终态/启动事件：尽力提取 assistant 增量字段（多种协议形状兼容），全部截断保留。
            if (!string.Equals(eventType, "turn/start", StringComparison.Ordinal)
                && !string.Equals(eventType, "turn/end", StringComparison.Ordinal))
            {
                var data = eventData;
                var part = data?["part"] as JsonObject;
                var chunk = data?["chunk"] as JsonObject;
                var tool = data?["tool"] as JsonObject;
                var partTool = part?["tool"] as JsonObject;
                textDelta = Cap(FirstText(data?["text"], data?["delta"], data?["content"], part?["text"], part?["delta"], part?["content"], chunk?["text"]), MaxTextDeltaChars);
                toolName = Cap(FirstText(data?["name"], data?["toolName"], tool?["name"], part?["name"], part?["toolName"], partTool?["name"], chunk?["name"]), 256);
                toolArgsDelta = Cap(FirstText(data?["arguments"], data?["args"], tool?["arguments"], part?["arguments"], part?["args"], chunk?["arguments"], chunk?["argumentsDelta"]), MaxToolArgsDeltaChars);
                toolCallId = Cap(FirstText(tool?["id"], partTool?["id"], chunk?["id"]), 256);
                if (textDelta is not null || toolName is not null)
                    assistantChunkKind = eventType;
            }
        }
        var errorMessage = type == "stream/error" ? HarnessJson.Text((payload["error"] as JsonObject)?["message"]) : null;
        return new HarnessMuxFrame(type, sessionId, eventType, turnEndKind, errorMessage, seq, assistantChunkKind, textDelta, toolName, toolArgsDelta, subscriptionLastSeq, toolCallId);
    }

    /// <summary>取第一个非空文本值（工具/对象节点转紧凑 JSON 文本）。</summary>
    private static string? FirstText(params JsonNode?[] candidates)
    {
        foreach (var node in candidates)
        {
            if (node is null) continue;
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && text is not null) return text;
            if (node is JsonObject or JsonArray) return node.ToJsonString();
        }
        return null;
    }

    private static string? Cap(string? text, int max)
        => text is null || text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// 退化检测的进程内压缩摘要：只含阶段与诊断计数（工具调用数、推理事件数、无进展告警数），
/// 绝不携带原始推理文本、工具参数或凭据，也不落盘（仅由 runner 合并进任务状态的展示字段）。
/// </summary>
public sealed record HarnessProgressSummary(
    string? Stage,
    int ToolCallCount,
    int ReasoningEventCount,
    int NoProgressWarnings);

/// <summary>
/// 有界滚动退化检测器：在单个 assistant step（turn）内检测两类退化——
/// 1) 显著长度文本片段的连续重复；2) 相同工具名 + 完整规范化参数在同阶段内连续重复且无进展证据。
/// 取消必须同时满足"同阶段 + 同规范化动作 + 无进展证据"：
/// <list type="bullet">
/// <item>阶段（<see cref="EnterStage"/>）：阶段开始、计划/读取、编辑、检查、报告/结束的转换即进展，清除无进展计数；</item>
/// <item>同规范化动作：只统计连续相同工具名 + 规范化参数哈希，不同工具/不同参数（不同目标）自然中断；</item>
/// <item>无进展证据：文件写入/编辑、检查/命令、报告写入（<see cref="ProgressTools"/>）与显式
/// <see cref="NoteProgress"/> 都记为进展并清除计数，产生退化原因前不得有此类证据。</item>
/// </list>
/// <see cref="RepeatThreshold"/> / <see cref="ReadOnlyRepeatThreshold"/> 仅作为"最小观察窗口"
/// （非终止阈值）：达到窗口即记一次无进展告警，但只有在上述三条件同时满足时才产生退化原因。
/// 总 steps、token、缓存命中不作为停止条件。
/// 只读/查询工具（read/grep/glob/web_search 等）重复容忍度更高，因为正常实现流程中反复查询
/// 同一文件属常见动作；只有同一阶段内连续重复同一目标且无进展证据才判定。
/// 工具调用按一次调用聚合：带 toolName 的事件开始新调用并提交上一次；纯参数增量事件只追加
/// 到当前调用、不单独计次（协议分片绝不误当多次调用）。参数超限或已被截断（无法可靠比较）时
/// 不参与重复计数（防止截断前缀把不同参数误判为相同），并以唯一签名中断连续计数。
/// 每次 turn/start 必须调用 <see cref="ResetStep"/> 重置 step 级窗口（WS 与 HTTP 轮询一致）。
/// 签名只保留工具名 + 规范化参数的哈希，绝不记录正文、凭据或完整参数；仅进程内内存使用，
/// 不写盘、不落日志。诊断计数（工具调用数/推理事件数/无进展告警数）供 runner 合并为压缩摘要。
/// </summary>
public sealed class HarnessDegenerationDetector
{
    private static readonly HashSet<string> ProgressOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "todo_write",
        "update_plan"
    };
    /// <summary>只读/查询类工具（含测试使用的别名）：重复容忍度更高，正常反复查询不误判。</summary>
    private static readonly HashSet<string> ReadOnlyQueryTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read",
        "read_file",
        "grep",
        "glob",
        "web_search",
        "job_output",
        "job_list",
        "list_agents",
        "get_goal",
        "skill",
        "read_image",
        "ask_user_question",
        "query",
        "search",
        "lookup",
        "history",
        "view",
        "show",
        "peek",
        "stat",
        "list",
        "fetch"
    };
    /// <summary>
    /// 进展型工具：文件写入/编辑、删除/移动、命令/检查执行、报告写入等。调用即产生有效进展，
    /// 必须清除"无进展重复计数"，绝不参与重复判定，也不作为取消依据（写入/编辑/检查/报告都是
    /// 合同推进证据，不应因重复写入而熔断覆盖）。
    /// </summary>
    private static readonly HashSet<string> ProgressTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文件写入 / 编辑 / 结构操作。
        "write_file",
        "write",
        "edit_file",
        "edit",
        "multi_edit",
        "insert",
        "insert_content",
        "append",
        "apply_patch",
        "apply_edit",
        "patch",
        "delete",
        "delete_range",
        "delete_symbol",
        "move_file",
        "rename",
        "copy_file",
        "mkdir",
        "rmdir",
        // 命令 / 检查 / 报告写入（检查结果与报告写入都是进展证据）。
        "bash",
        "pwsh",
        "shell",
        "run",
        "execute",
        "run_command",
        "exec",
        "test",
        "build",
        "publish",
        "run_check",
        "run_worker_check",
        "worker_check",
        "dotnet",
        "npm",
        "pytest",
        "go_test",
        "git_add",
        "git_commit"
    };
    /// <summary>参与重复计数的最小片段长度（字符）；低于该长度的短词/符号片段直接忽略。</summary>
    public int MinFragmentLength { get; init; } = 20;
    /// <summary>写入型/命令型工具同一签名连续出现达到该次数即判定退化（普通重试通常 1-2 次，不误判）。</summary>
    public int RepeatThreshold { get; init; } = 3;
    /// <summary>只读/查询工具同一签名连续出现达到该次数才判定退化（正常反复读取查询不误判）。</summary>
    public int ReadOnlyRepeatThreshold { get; init; } = 6;
    /// <summary>无计划、编辑、检查或报告时允许的累计只读调用上限，避免不同参数的读取循环绕过重复签名检测。</summary>
    public int MaxReadOnlyCallsWithoutProgress { get; init; } = 80;
    /// <summary>同一工具持续发出空参数增量的上限；这不是有效工具调用，而是上游协议流异常。</summary>
    public int EmptyToolDeltaThreshold { get; init; } = 6;
    /// <summary>单片段参与比较的最大长度；超出部分截断。</summary>
    public int FragmentCap { get; init; } = 2048;
    /// <summary>工具参数规范化后参与比较的最大长度；超出后视为不可可靠比较（不参与重复计数，避免截断前缀误判）。</summary>
    public int ToolArgsCap { get; init; } = 4096;
    /// <summary>滚动窗口最大保留条数。</summary>
    public int MaxWindowEntries { get; init; } = 64;
    /// <summary>滚动窗口最大保留总字符数。</summary>
    public int MaxWindowChars { get; init; } = 16 * 1024;
    /// <summary>滚动窗口时间跨度；早于该跨度的观察不再参与判定。</summary>
    public TimeSpan WindowSpan { get; init; } = TimeSpan.FromMinutes(5);

    private readonly List<Entry> window = new();
    private int windowChars;
    private string? reason;
    private string? lastToolName;
    private readonly StringBuilder textStream = new();
    private string? pendingToolName;
    private readonly StringBuilder pendingArgs = new();
    private bool pendingOversize;
    /// <summary>当前任务阶段（阶段转换即进展，清除无进展重复计数）。</summary>
    private string? stage;
    /// <summary>已提交的工具调用总数（含进展工具；仅诊断计数，不落盘）。</summary>
    private int toolCallCount;
    /// <summary>已观察的推理/文本增量事件数（不作为进度或步骤）。</summary>
    private int reasoningEventCount;
    /// <summary>无进展重复告警数（达到最小观察窗口时的告警次数）。</summary>
    private int noProgressWarnings;
    private int readOnlyCallsWithoutProgress;
    private int emptyToolDeltas;
    private string? pendingToolId;

    private readonly record struct Entry(DateTime Time, string Signature);

    public bool IsDegenerate => reason is not null;

    /// <summary>触发原因（可读中文）；未触发为 null。</summary>
    public string? Reason => reason;

    /// <summary>当前阶段；未进入任何阶段为 null。</summary>
    public string? Stage => stage;

    /// <summary>已提交的工具调用总数（含进展工具）。</summary>
    public int ToolCallCount => toolCallCount;

    /// <summary>已观察的推理/文本增量事件数（不作为进度或步骤）。</summary>
    public int ReasoningEventCount => reasoningEventCount;

    /// <summary>无进展重复告警数。</summary>
    public int NoProgressWarnings => noProgressWarnings;

    /// <summary>进程内压缩摘要快照（只含诊断计数与阶段，绝不持久化原始文本/参数/凭据）。</summary>
    public HarnessProgressSummary Snapshot() => new(stage, toolCallCount, reasoningEventCount, noProgressWarnings);

    /// <summary>
    /// 进入（或切换）任务阶段。阶段转换本身是有效进展：清除无进展重复计数与文本流，
    /// 只有"同一阶段内连续同一规范化动作且无进展证据"才可能产生退化。
    /// 返回是否发生了阶段切换（true = 切换），供调用方只在真实阶段变化时投影状态/PROGRESS。
    /// 同一阶段重复调用为无操作（返回 false）。
    /// </summary>
    public bool EnterStage(string? next)
    {
        if (string.Equals(next, stage, StringComparison.Ordinal)) return false;
        stage = next;
        // read/start 仅是观察状态，不足以证明合同推进；跨回合的只读计数必须保留。
        if (next is "plan" or "edit" or "check" or "report") NoteProgress();
        else ClearNoProgress();
        return true;
    }

    /// <summary>显式记录一次进展证据（检查结果/报告写入等）：清除无进展重复计数。</summary>
    public void NoteProgress()
    {
        readOnlyCallsWithoutProgress = 0;
        emptyToolDeltas = 0;
        ClearNoProgress();
    }

    private void ClearNoProgress()
    {
        window.Clear();
        windowChars = 0;
        textStream.Clear();
        // 进行中的未提交工具调用增量也一并丢弃：进展点之前的碎片不得进入新阶段的无进展计数。
        pendingToolName = null;
        pendingArgs.Clear();
        pendingOversize = false;
    }

    /// <summary>
    /// 工具名 → 阶段映射（供 runner 在事件流与 HTTP 轮询中统一推断，保证两条路径一致）。
    /// 计划/读取/写入编辑/命令检查各自归入 plan/read/edit/check；无法归类的返回 null（不切换阶段）。
    /// </summary>
    public static string? MapToolStage(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        if (ProgressOnlyTools.Contains(toolName)) return "plan";
        if (ReadOnlyQueryTools.Contains(toolName)) return "read";
        if (ProgressTools.Contains(toolName)) return "edit";
        return null;
    }

    /// <summary>新 assistant step（turn/start）开始时调用：清空窗口与进行中的工具调用累积（步骤内判定，防止跨步骤累积误判）。</summary>
    /// <summary>
    /// 新 assistant step（turn/start）开始时调用：清空 step 级窗口与进行中的工具调用累积
    /// （步骤内判定，防止跨步骤累积误判）。阶段与诊断计数跨 step 保留（阶段表示任务的推进，
    /// 计数是整任务累计）。
    /// </summary>
    public void ResetStep()
    {
        pendingToolName = null;
        pendingArgs.Clear();
        pendingOversize = false;
        emptyToolDeltas = 0;
        window.Clear();
        windowChars = 0;
        lastToolName = null;
        textStream.Clear();
    }

    /// <summary>观察一次助手文本/推理增量（推理事件数作为诊断计数，不作为进度或步骤）。</summary>
    public void ObserveText(string? delta)
    {
        if (reason is not null || string.IsNullOrWhiteSpace(delta)) return;
        reasoningEventCount++;
        textStream.Append(delta);
        if (textStream.Length > MaxWindowChars)
            textStream.Remove(0, textStream.Length - MaxWindowChars);
        DetectRepeatedTextSuffix();
    }

    private void DetectRepeatedTextSuffix()
    {
        if (reason is not null) return;
        var text = textStream.ToString();
        var maxUnit = Math.Min(FragmentCap, text.Length / RepeatThreshold);
        for (var unit = maxUnit; unit >= MinFragmentLength; unit--)
        {
            var repeatedLength = unit * RepeatThreshold;
            var start = text.Length - repeatedLength;
            if (start < 0) continue;
            var sample = text.AsSpan(start, unit);
            var significant = false;
            for (var i = 0; i < sample.Length; i++)
            {
                if (char.IsLetterOrDigit(sample[i])) { significant = true; break; }
            }
            if (!significant) continue;
            var same = true;
            for (var copy = 1; copy < RepeatThreshold && same; copy++)
                same = sample.SequenceEqual(text.AsSpan(start + copy * unit, unit));
            if (!same) continue;
            reason = $"相同文本片段已连续重复 {RepeatThreshold} 次，无有效进展。";
            return;
        }
    }

    /// <summary>
    /// 观察一次工具调用事件。toolName 非空表示一次新的完整工具调用开始（同时提交上一次
    /// 尚未提交的调用，计一次）；仅提供参数增量（toolName 为空）时追加到当前进行中的调用，
    /// 不单独计次——协议参数分片绝不会被误当成多次完整调用。
    /// 相同工具名 + 完整规范化参数连续重复即无有效进展；参数超限或已被截断（无法可靠比较）时
    /// 不参与重复计数，但会中断连续计数。
    /// </summary>
    public void ObserveToolCall(string? toolName, string? argsDelta, string? toolCallId = null)
    {
        if (reason is not null) return;
        if (toolName is not null)
        {
            if (!string.IsNullOrWhiteSpace(toolCallId) && string.Equals(toolCallId, pendingToolId, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(argsDelta)) emptyToolDeltas = 0;
                AppendPendingArgs(argsDelta);
                return;
            }
            // 某些 OpenAI 兼容网关会不断重发 `tool-call-delta(name=read)`，却从不
            // 提供 arguments。这不是 80 次真实 read；保留首个 pending 调用等待参数，
            // 连续空帧达到小窗口后明确报告协议异常。
            if (string.Equals(toolName, pendingToolName, StringComparison.Ordinal)
                && pendingArgs.Length == 0 && string.IsNullOrWhiteSpace(argsDelta))
            {
                if (++emptyToolDeltas >= EmptyToolDeltaThreshold)
                {
                    noProgressWarnings++;
                    reason = $"工具 {TruncateForReason(toolName)} 连续发送空参数增量 {EmptyToolDeltaThreshold} 次，未形成可执行调用。";
                }
                return;
            }
            CommitToolCall();
            pendingToolName = toolName;
            pendingToolId = toolCallId;
            pendingArgs.Clear();
            pendingOversize = false;
            emptyToolDeltas = string.IsNullOrWhiteSpace(argsDelta) ? 1 : 0;
            AppendPendingArgs(argsDelta);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(argsDelta)) emptyToolDeltas = 0;
            AppendPendingArgs(argsDelta);
        }
    }

    private void AppendPendingArgs(string? argsDelta)
    {
        if (string.IsNullOrEmpty(argsDelta) || pendingToolName is null) return;
        if (pendingOversize) return;
        if (pendingArgs.Length + argsDelta.Length > ToolArgsCap)
        {
            // 参数增量已超可比上限：丢弃累积并标记不可比，防止截断前缀被误判为相同。
            pendingOversize = true;
            pendingArgs.Clear();
            return;
        }
        pendingArgs.Append(argsDelta);
    }

    /// <summary>提交当前进行中的一次工具调用（计一次）；无进行中调用时为无操作。</summary>
    public void CommitToolCall()
    {
        if (reason is not null || pendingToolName is null) return;
        var toolName = pendingToolName;
        var oversize = pendingOversize;
        var args = pendingOversize ? string.Empty : pendingArgs.ToString();
        pendingToolName = null;
        pendingToolId = null;
        pendingArgs.Clear();
        pendingOversize = false;
        // 计划书签可以合法地以相同载荷多次更新且永不改动项目文件，不是生成循环证据。
        if (ProgressOnlyTools.Contains(toolName)) return;
        toolCallCount++;
        // 写入/编辑/检查/报告等进展型工具调用即产生有效进展：清除无进展重复计数，
        // 绝不参与重复判定（重复写入不应被熔断覆盖）。仅计入工具调用总数。
        if (ProgressTools.Contains(toolName)) { NoteProgress(); return; }
        var isReadOnly = ReadOnlyQueryTools.Contains(toolName);
        if (isReadOnly && ++readOnlyCallsWithoutProgress > MaxReadOnlyCallsWithoutProgress)
        {
            noProgressWarnings++;
            reason = $"连续只读工具调用超过 {MaxReadOnlyCallsWithoutProgress} 次，未出现计划、编辑、检查或报告进展。";
            return;
        }
        var normalized = string.Empty;
        if (!oversize)
        {
            if (!TryNormalizeToolArgs(args, out normalized)) oversize = true;
            else if (toolName.Length + normalized.Length < MinFragmentLength) return;
        }
        lastToolName = toolName;
        // 签名只保留工具名 + 规范化参数的哈希：绝不记录正文、凭据或完整参数，
        // 同时保持"相同工具 + 相同参数"的相等语义用于连续重复判定。
        // 不可比（超限/截断）的调用以唯一签名入窗：自身永不匹配，但会中断连续重复计数。
        var signature = oversize
            ? (isReadOnly ? "R:" : "C:") + toolName + "\u0000" + Guid.NewGuid().ToString("N")
            : (isReadOnly ? "R:" : "C:") + toolName + "\u0000" + HashToolArgs(normalized);
        Add(DateTime.UtcNow, signature, isReadOnly);
    }

    private void Add(DateTime time, string signature, bool isReadOnly)
    {
        var cutoff = time - WindowSpan;
        while (window.Count > 0 && window[0].Time < cutoff)
        {
            windowChars -= window[0].Signature.Length;
            window.RemoveAt(0);
        }
        while (window.Count >= MaxWindowEntries || windowChars + signature.Length > MaxWindowChars)
        {
            windowChars -= window[0].Signature.Length;
            window.RemoveAt(0);
        }
        window.Add(new Entry(time, signature));
        windowChars += signature.Length;

        // 从尾部向前统计同一签名的连续出现次数；出现不同签名（其他工具/不同参数/其他有效进展）
        // 时连续计数自然重新开始。进度型进展（NoteProgress）会清空窗口，因此此处的连续重复
        // 一定满足"同阶段 + 同规范化动作 + 无进展证据"三条件。
        var streak = 0;
        for (var i = window.Count - 1; i >= 0 && window[i].Signature == signature; i--) streak++;
        var threshold = isReadOnly ? ReadOnlyRepeatThreshold : RepeatThreshold;
        if (streak >= threshold)
        {
            // 阈值仅作为"最小观察窗口"（非终止阈值）：达到窗口记一次无进展告警；
            // 取消已由上面的三条件（同阶段 + 同规范化动作 + 无进展证据）约束。
            noProgressWarnings++;
            reason = signature.StartsWith("R:", StringComparison.Ordinal)
                ? $"相同只读/查询调用已连续重复 {streak} 次（工具名 {TruncateForReason(lastToolName)}），无有效进展。"
                : $"相同工具调用已连续重复 {streak} 次（工具名 {TruncateForReason(lastToolName)}），无有效进展。";
        }
    }

    /// <summary>
    /// 工具参数规范化：JSON 参数紧凑序列化（忽略空白差异），其余原样。
    /// 返回 false 表示参数不可可靠比较（超过 <see cref="ToolArgsCap"/>，或已被解析层截断
    /// 带 "…" 后缀）——此时不得以截断前缀参与重复判定，避免不同参数误判为相同。
    /// </summary>
    private bool TryNormalizeToolArgs(string? args, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(args)) return true;
        var trimmed = args.Trim();
        try
        {
            var node = JsonNode.Parse(trimmed);
            if (node is not null) trimmed = node.ToJsonString();
        }
        catch { /* 非 JSON 参数：原样参与比较 */ }
        if (trimmed.Length > ToolArgsCap || trimmed.EndsWith("…", StringComparison.Ordinal))
            return false;
        normalized = trimmed;
        return true;
    }

    /// <summary>规范化参数 → 定长哈希（SHA-256 前 16 字节十六进制），不保留参数原文。</summary>
    private static string HashToolArgs(string normalized)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes.AsSpan(0, 16));
    }

    private static string TruncateForReason(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "未知";
        return text.Length <= 60 ? text : text[..60] + "…";
    }
}

/// <summary>
/// Harness Web Host 事件流监听（rc.6 原生协议）：WebSocket ws://127.0.0.1:3080/api/events.mux。
/// 帧为文本 JSON { type:"server-request", rpcId, method:帧类型, payload:{ type:"session/event"|..., ... } }；
/// GET /api/events.mux 返回 426 属正常协议提示。服务端关闭或连接失败时枚举结束（不抛出）；
/// 用户取消以 CancellationToken 传播。WebSocket 随枚举结束释放。
/// </summary>
public sealed partial class DeepSeekHarnessEventStream : IDisposable
{
    private readonly string wsUrl;
    private readonly string hostWsUrl;

    /// <summary>WebSocket 工厂（默认 ClientWebSocket；测试可注入）。</summary>
    public Func<ClientWebSocket>? WebSocketFactory { get; init; }
    /// <summary>单帧字节上限；超限帧被丢弃但不破坏流同步。</summary>
    public int MaxFrameBytes { get; init; } = 8 * 1024 * 1024;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>可用时使用与 DSH 同版本 Node 的本地事件中继，避免 .NET WebSocket 双通道时序差异。</summary>
    public string? NodeExecutablePath { get; init; }
    /// <summary>随 Harness Runner 发布的中继脚本路径。</summary>
    public string? NodeRelayScriptPath { get; init; }
    /// <summary>仅转发该 Harness 会话的事件，防止全局 mux 历史会话淹没当前合同。</summary>
    public string? SessionIdFilter { get; init; }
    public bool UseGatewayProtocol { get; init; }
    /// <summary>
    /// rc.6 的一代事件连接只有在 mux/host 两条下行流均打开且 host.describe 成功后才算可用。
    /// Runner 注入本回调，避免把"能连上一个 WebSocket"误报为实时中继已就绪。
    /// </summary>
    public Func<CancellationToken, Task>? ReadyCheckAsync { get; init; }

    public DeepSeekHarnessEventStream(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        var scheme = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        wsUrl = $"{scheme}://{uri.Host}:{uri.Port}/api/events.mux";
        hostWsUrl = $"{scheme}://{uri.Host}:{uri.Port}/api/events.host";
    }

    public string WebSocketUrl => wsUrl;
    public string HostWebSocketUrl => hostWsUrl;

    public void Dispose()
    {
        // 连接由 ListenAsync 内的 using 持有并在枚举结束时释放；本类型无长驻资源。
    }

    public async IAsyncEnumerable<HarnessMuxFrame> ListenAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (UseGatewayProtocol)
        {
            await foreach (var frame in ListenGatewayAsync(cancellationToken)) yield return frame;
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(NodeExecutablePath) && File.Exists(NodeExecutablePath)
            && !string.IsNullOrWhiteSpace(NodeRelayScriptPath) && File.Exists(NodeRelayScriptPath))
        {
            await foreach (var frame in ListenThroughNodeRelayAsync(cancellationToken))
                yield return frame;
            yield break;
        }

        using var socket = (WebSocketFactory ?? (() => new ClientWebSocket()))();
        using var hostSocket = (WebSocketFactory ?? (() => new ClientWebSocket()))();
        // 某些 DSH Web Host 在连接空闲时不会把取消令牌传递到底层
        // ReceiveAsync；仅取消 token 会让枚举器与 Runner 永久停在“重连中”。
        // 主动 Abort 只作用于本次事件流连接，RPC/会话仍由上层 HTTP 回退和
        // session.cancel 管理，确保无帧 watchdog 可以确定地释放 ReceiveAsync。
        using var abortOnCancel = cancellationToken.Register(static state =>
        {
            foreach (var item in (ClientWebSocket[])state!)
                try { item.Abort(); } catch { }
        }, new[] { socket, hostSocket });
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            // 官方 dsh-client-connection 同时打开 mux 与 host。顺序连接会让某些 rc.6
            // Host 在 generation 尚未完整时错过订阅/事件，随后被 Runner 误判为静默。
            await Task.WhenAll(
                socket.ConnectAsync(new Uri(wsUrl), connectCts.Token),
                hostSocket.ConnectAsync(new Uri(hostWsUrl), connectCts.Token));
            if (ReadyCheckAsync is not null)
                await ReadyCheckAsync(connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"连接事件流超时（{wsUrl}）。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法连接事件流（{wsUrl}）：{HarnessJson.Truncate(ex.Message, 160)}", ex);
        }

        // DSH rc.6 将 mux 与 host 作为一代连接的两个下行通道；只开 mux 会被 Host
        // 视为不完整连接，可能没有后续 session/event。host 帧不参与合同进度，但需要持续排空。
        var hostEnded = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = DrainHostEventsAsync(hostSocket, cancellationToken).ContinueWith(task =>
        {
            hostEnded.TrySetResult(task.IsFaulted ? task.Exception?.GetBaseException() : null);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

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
                        var receiveTask = socket.ReceiveAsync(chunk, cancellationToken);
                        var completed = await Task.WhenAny(receiveTask, hostEnded.Task);
                        if (ReferenceEquals(completed, hostEnded.Task))
                        {
                            // 调用方的超时/停止会同时 Abort 两条 socket；优先传播取消，
                            // 不把主动结束误报为 host 通道故障。
                            cancellationToken.ThrowIfCancellationRequested();
                            try { socket.Abort(); } catch { }
                            var hostError = await hostEnded.Task;
                            throw new InvalidOperationException(hostError is null
                                ? "事件流 host 通道已关闭。"
                                : "事件流 host 通道异常结束：" + HarnessJson.Truncate(hostError.Message, 160));
                        }
                        result = await receiveTask;
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
                        var status = result.CloseStatus?.ToString() ?? "未提供状态";
                        var description = string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                            ? string.Empty
                            : "：" + HarnessJson.Truncate(result.CloseStatusDescription, 120);
                        throw new InvalidOperationException("事件流 mux 通道被 Host 关闭（" + status + description + "）。");
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

    private async IAsyncEnumerable<HarnessMuxFrame> ListenThroughNodeRelayAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(NodeExecutablePath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--no-warnings");
        start.ArgumentList.Add(NodeRelayScriptPath!);
        start.Environment["CODEX_HELPER_DSH_BASE_URL"] = new Uri(wsUrl).GetLeftPart(UriPartial.Authority).Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase).Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(SessionIdFilter)) start.Environment["CODEX_HELPER_DSH_SESSION_ID"] = SessionIdFilter;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 DSH Node 事件中继。");
        using var stop = cancellationToken.Register(static state =>
        {
            try { ((Process)state!).Kill(entireProcessTree: true); } catch { }
        }, process);
        var ready = false;
        while (true)
        {
            var readTask = process.StandardOutput.ReadLineAsync();
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var completed = await Task.WhenAny(readTask, exitTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(completed, exitTask))
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException("DSH Node 事件中继已退出：" + HarnessJson.Truncate(error, 160));
            }
            var line = await readTask;
            if (line is null) continue;
            if (string.Equals(line, "{\"type\":\"helper/relay-ready\"}", StringComparison.Ordinal))
            {
                ready = true;
                if (ReadyCheckAsync is not null) await ReadyCheckAsync(cancellationToken);
                yield return new HarnessMuxFrame("relay/ready", null, null, null, null);
                continue;
            }
            if (!ready) continue;
            var frame = HarnessMuxFrame.Parse(line);
            if (frame is not null) yield return frame;
        }
    }

    private static async Task DrainHostEventsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            while (!result.EndOfMessage)
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
            }
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

        // 2) rc.6 完整连接能力：mux + host 两条下行通道均由 EventStream 打开，
        //    再用 host.describe 确认当前 connection generation 可用。
        var eventsOk = false;
        string? eventsReason = null;
        try
        {
            using var stream = new DeepSeekHarnessEventStream(baseUrl)
            {
                WebSocketFactory = WebSocketFactory, SessionIdFilter = probeSessionId,
                UseGatewayProtocol = rpc.ProtocolName == "gateway-slash-rpc"
            };
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
            if (eventsOk && rpc.ProtocolName != "gateway-slash-rpc")
            {
                var described = await rpc.CallAsync("host.describe", new JsonObject(), cancellationToken);
                if (!described.Success)
                {
                    eventsOk = false;
                    eventsReason = "host.describe 失败（" + described.ErrorMessage + "）";
                }
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
        }) + (confirmed
            ? "。中继三项能力均已由运行时探测确认。"
            : submitOk && cancelOk
                ? "。实时事件流未确认；任务可提交并会改用安全的会话状态轮询，不会重提。"
                : "。提交或取消能力未确认，自动提交已降级。");
        return new HarnessRelayCapability(submitOk, eventsOk, cancelOk, confirmed, message);
    }
}
