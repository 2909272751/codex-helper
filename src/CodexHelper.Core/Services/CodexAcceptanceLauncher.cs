using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 独立验收线程在任务目录内落盘的验收账本 GPT_ACCEPTANCE.json（UTF-8 无 BOM 原子写入）。
/// 字段：schema、taskId、fingerprint、verdict（accepted / needs-repair / acceptance-failed）、
/// verdictUtc、安全摘要与检查命令/结果短摘要；绝不包含合同正文、凭据、token 或 DSH 消息正文。
/// 只有任务 ID+合同指纹都匹配的该文件，才能驱动外层把任务标为 accepted/needs-repair。
/// </summary>
public sealed record GptAcceptanceRecord(
    int SchemaVersion,
    string TaskId,
    string? ContractFingerprint,
    string Verdict,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime VerdictUtc,
    string? SafeSummary,
    string? ChecksSummary);

/// <summary>GPT_ACCEPTANCE.json 的常量与宽容原子读写宿主。</summary>
public static class GptAcceptanceStore
{
    public const string RecordFileName = "GPT_ACCEPTANCE.json";
    public const int RecordSchemaVersion = 1;

    public const string VerdictAccepted = "accepted";
    public const string VerdictNeedsRepair = "needs-repair";
    public const string VerdictFailed = "acceptance-failed";

    public static string RecordPath(string taskDirectory) => Path.Combine(taskDirectory, RecordFileName);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    /// <summary>
    /// 宽容读取（绝不抛异常，绝不猜测结论）：同一账本支持合同字段与旧 PascalCase 同义 key。
    /// 读取只做字段投影，taskId/fingerprint 的严格匹配由调用方（验收/修复驱动）强制。
    /// </summary>
    public static GptAcceptanceRecord? TryReadRecord(string taskDirectory)
    {
        var path = RecordPath(taskDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null) return null;
            return new GptAcceptanceRecord(
                GptLedgerJson.GetInt(node, "schema") ?? RecordSchemaVersion,
                GptLedgerJson.GetText(node, "taskId") ?? string.Empty,
                GptLedgerJson.GetText(node, "fingerprint"),
                GptLedgerJson.GetText(node, "verdict") ?? string.Empty,
                GptLedgerJson.GetUtc(node, "verdictUtc") ?? DateTime.UnixEpoch,
                GptLedgerJson.GetText(node, "safeSummary"),
                GptLedgerJson.GetText(node, "checksSummary"));
        }
        catch { return null; }
    }

    /// <summary>原子写入（UTF-8 无 BOM；稳定下划线 key 集合，失败上抛）。</summary>
    public static void WriteRecord(string taskDirectory, GptAcceptanceRecord record)
    {
        var json = new JsonObject
        {
            ["schema"] = record.SchemaVersion,
            ["taskId"] = record.TaskId,
            ["fingerprint"] = record.ContractFingerprint,
            ["verdict"] = record.Verdict,
            ["verdictUtc"] = GptLedgerJson.FormatUtc(record.VerdictUtc),
            ["safeSummary"] = record.SafeSummary,
            ["checksSummary"] = record.ChecksSummary
        };
        AtomicFile.WriteAllText(RecordPath(taskDirectory), json.ToJsonString(WriteOptions));
    }
}

/// <summary>
/// 账本 JSON 的单一安全投影器：显式同义 key 映射（不依赖大小写宽容来连接不同字段名）。
/// 新写统一为合同字段（schema/taskId/fingerprint/verdict/verdictUtc/safeSummary/…Summary）；
/// 读取同时接受旧 PascalCase 同义 key（SchemaVersion/Schema、ContractFingerprint/Fingerprint 等）。
/// </summary>
public static class GptLedgerJson
{
    private static readonly string[] SchemaKeys = ["schema", "Schema", "SchemaVersion", "schemaVersion"];
    private static readonly string[] TaskIdKeys = ["taskId", "TaskId"];
    private static readonly string[] FingerprintKeys = ["fingerprint", "Fingerprint", "contractFingerprint", "ContractFingerprint"];
    private static readonly string[] VerdictKeys = ["verdict", "Verdict"];

    public static int? GetInt(JsonObject node, string semantic)
    {
        foreach (var key in KeysFor(semantic))
        {
            var value = FindValue(node, key);
            if (value is null) continue;
            if (value.TryGetValue<int>(out var intValue)) return intValue;
            if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;
        }
        return null;
    }

    public static string? GetText(JsonObject node, string semantic)
    {
        foreach (var key in KeysFor(semantic))
        {
            var value = FindValue(node, key);
            if (value is null) continue;
            if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    public static DateTime? GetUtc(JsonObject node, string semantic)
    {
        foreach (var key in KeysFor(semantic))
        {
            var value = FindValue(node, key);
            if (value is null) continue;
            if (value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed.UtcDateTime;
        }
        return null;
    }

    public static string FormatUtc(DateTime value)
        => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string[] KeysFor(string semantic) => semantic.ToLowerInvariant() switch
    {
        "schema" => SchemaKeys,
        "taskid" => TaskIdKeys,
        "fingerprint" => FingerprintKeys,
        "verdict" => VerdictKeys,
        "verdictutc" => ["verdictUtc", "VerdictUtc", "verdict_utc"],
        "safesummary" => ["safeSummary", "SafeSummary"],
        "checkssummary" => ["checksSummary", "ChecksSummary"],
        "scopesummary" => ["scopeSummary", "ScopeSummary"],
        _ => [semantic]
    };

    private static JsonValue? FindValue(JsonObject node, string key)
    {
        foreach (var property in node)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase) && property.Value is JsonValue value)
                return value;
        }
        return null;
    }
}

/// <summary>
/// 阶段二真实验收启动器：实现 <see cref="IHarnessAcceptanceLauncher"/>，默认经本机 codex
/// `app-server`（stdio JSON-RPC，独立验收线程）驱动一次验收 turn。等待阶段零 Codex 模型调用；
/// 只有真实 DSH 完成后的一次独立验收会消耗 Codex 用量。启动器判定只认两件事：
/// (1) App Server 验收 turn 到达真实终态；(2) 任务目录 GPT_ACCEPTANCE.json 的 taskId+合同指纹匹配。
/// 超时/协议错误/缺账本/账本不匹配/异常一律落盘 acceptance-failed（脱敏可诊断），绝不伪装成功；
/// accepted/needs-repair/acceptance-failed 后由外层协调器保证永不自动重试。
/// 输入只含绝对项目根/任务目录/任务 ID/合同指纹与短固定验收指令，绝不转发 DSH 消息正文、
/// 推理流、工具参数、全量历史或报告全文；验收线程自行从本地任务目录读取所需文件。
/// </summary>
public sealed class CodexAcceptanceLauncher : IHarnessAcceptanceLauncher
{
    /// <summary>验收 turn 默认最长时长（一次独立验收）；超时按 acceptance-failed 处理（测试可注入缩短）。</summary>
    public static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromMinutes(45);

    public TimeSpan TurnTimeout { get; init; } = DefaultTurnTimeout;

    /// <summary>codex 可执行解析器（默认 <see cref="CodexExecutableResolver.Resolve"/>；测试可注入）。</summary>
    public Func<string?>? CodexPathResolver { get; init; }

    /// <summary>传输工厂（默认生成 <see cref="CodexAppServerProcessTransport"/>；测试注入假 App Server 传输）。</summary>
    public Func<CancellationToken, Task<ICodexAppServerTransport>>? TransportFactory { get; init; }

    /// <summary>验收固定指令构建器（默认 <see cref="BuildDefaultInstruction"/>；测试可注入以捕获并断言内容）。</summary>
    public Func<HarnessAcceptanceLaunchContext, string>? InstructionBuilder { get; init; }

    public async Task<HarnessAcceptanceLauncherResult> LaunchAsync(HarnessAcceptanceLaunchContext context, CancellationToken cancellationToken)
    {
        if (context is null) throw new CodexAcceptanceException("验收启动器缺少上下文。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TurnTimeout);
        var token = linked.Token;
        try
        {
            ICodexAppServerTransport transport;
            if (TransportFactory is not null)
            {
                transport = await TransportFactory(token);
            }
            else
            {
                var resolver = CodexPathResolver ?? (() => CodexExecutableResolver.Resolve());
                var executable = resolver();
                transport = new CodexAppServerProcessTransport(
                    executable ?? throw new CodexAcceptanceException("未找到 codex 可执行文件，独立验收未启动（未调用任何模型）。"));
            }
            await using (transport)
            {
                await transport.InitializeAsync(token);
                // 受限验收：thread 级 sandbox=read-only（项目根不可写）+ turn 最小权限仅任务目录可写。
                var threadId = await transport.StartThreadAsync(context.ProjectRoot, token, CodexTurnPermissions.ReadOnlySandbox);
                var outcome = await transport.RunTurnAsync(threadId, BuildInstruction(context), token,
                    new CodexTurnPermissions(WritableRoot: context.TaskDirectory));
                if (outcome is null || !outcome.Completed)
                    throw new CodexAcceptanceException("App Server 验收 turn 未到可确认终态（" + HarnessAcceptanceQueue.Safe(outcome?.FailureReason) + "）；已保守落盘失败，绝不凭工具返回/流静默猜测完成。");
            }

            // 只有真实终态 + GPT_ACCEPTANCE.json（taskId+指纹匹配）才允许 accepted/needs-repair。
            var verdictRecord = GptAcceptanceStore.TryReadRecord(context.TaskDirectory);
            if (verdictRecord is null)
                throw new CodexAcceptanceException("App Server 验收 turn 已到真实终态，但任务目录缺少 GPT_ACCEPTANCE.json，无法确认验收结论（保守失败，绝不伪装成功）。");
            if (!string.Equals(verdictRecord.TaskId, context.TaskId, StringComparison.Ordinal))
                throw new CodexAcceptanceException("GPT_ACCEPTANCE.json 的 taskId 与本任务不匹配，无法确认验收结论（保守失败）。");
            if (string.IsNullOrWhiteSpace(context.ContractFingerprint)
                || !string.Equals(verdictRecord.ContractFingerprint, context.ContractFingerprint, StringComparison.Ordinal))
                throw new CodexAcceptanceException("GPT_ACCEPTANCE.json 的合同指纹与本任务不匹配，无法确认验收结论（保守失败）。");

            var verdict = (verdictRecord.Verdict ?? string.Empty).ToLowerInvariant();
            if (verdict is GptAcceptanceStore.VerdictAccepted or GptAcceptanceStore.VerdictNeedsRepair)
            {
                return new HarnessAcceptanceLauncherResult(
                    verdict,
                    "独立验收线程已落账 GPT_ACCEPTANCE.json（verdict=" + verdict + "）：" + HarnessAcceptanceQueue.Safe(verdictRecord.SafeSummary));
            }
            throw new CodexAcceptanceException("独立验收线程已落账 GPT_ACCEPTANCE.json（verdict=acceptance-failed）：" + HarnessAcceptanceQueue.Safe(verdictRecord.SafeSummary));
        }
        catch (OperationCanceledException)
        {
            // 外层取消 / 验收 turn 超时：都按“未完成、可诊断失败”处理，绝不伪装成功。
            throw new CodexAcceptanceException(cancellationToken.IsCancellationRequested
                ? "独立验收被外层取消，未完成。"
                : "独立验收 turn 超时（超过 " + TurnTimeout.TotalMinutes + " 分钟），未完成（保守失败，未调用额外模型）。");
        }
        catch (CodexAcceptanceException) { throw; }
        catch (Exception ex)
        {
            throw new CodexAcceptanceException("独立验收驱动异常：" + HarnessAcceptanceQueue.Safe(ex.Message));
        }
    }

    private string BuildInstruction(HarnessAcceptanceLaunchContext context)
        => InstructionBuilder is not null
            ? InstructionBuilder(context)
            : BuildDefaultInstruction(context);

    /// <summary>
    /// 固定验收指令（短、不含任何 DSH 消息正文/推理/工具参数/报告全文；只含定位与身份信息）。
    /// 要求验收线程先只读任务目录合同/状态/报告/可信终态证据并独立检查改动、运行聚焦 gptChecks，
    /// 结论原子写入任务目录 GPT_ACCEPTANCE.json 后结束 turn；禁止提交/推送/安装/打包/发布/
    /// 删除/重置及修改任何其他文件。
    /// 终态判定以任务目录内证据为准：本指令明确要求核验 HARNESS_TERMINAL_EVIDENCE.json 与
    /// HARNESS_SUPERVISOR.json（若存在）同 HARNESS_STATUS.json/EXECUTION_REPORT.md 的一致性；
    /// 绝不要求读取系统进程命令行或凭猜测其它 Runner 的存活来决定 accepted（Runner 退出已由
    /// 本地监督器的可信终态证据证明，缺证据/错配/非终态一律保守失败）。
    /// </summary>
    public static string BuildDefaultInstruction(HarnessAcceptanceLaunchContext context)
    {
        var instruction = string.Join(Environment.NewLine, new[]
        {
            "你是本机独立验收执行器（Codex App Server 独立线程，不依赖任何既有会话）。只做一次聚焦验收，完成后结束 turn。",
            "任务目录：" + context.TaskDirectory,
            "项目根：" + context.ProjectRoot,
            "任务 ID：" + context.TaskId,
            "合同指纹：" + context.ContractFingerprint,
            "规则：先只读本任务目录的 SPEC.md、HANDOFF.md、manifest.json、HARNESS_STATUS.json、HARNESS_TERMINAL_EVIDENCE.json、HARNESS_SUPERVISOR.json、EXECUTION_REPORT.md 与 WORKER_ACCEPTANCE.md（绝不读取 ACCEPTANCE.md）；",
            "先核验任务目录内可信终态证据与本任务状态/报告的一致性：HARNESS_TERMINAL_EVIDENCE.json 必须存在、taskId/合同指纹与本任务一致、terminalOutcome=completed，且 HARNESS_SUPERVISOR.json（如存在）同 HARNESS_STATUS.json（非 running 的完成终态）与 EXECUTION_REPORT.md 报告门禁一致；",
            "Runner 是否已退出由上述本地监督器证据决定，你不需要也绝不能通过读取系统进程命令行、任务列表或猜测其它 Runner 的存活来判断 accepted；若证据缺失、taskId/指纹错配或非终态，必须保守失败；",
            "然后独立检查执行器的实际改动并运行聚焦 gptChecks。不得提交、推送、安装、打包、发布、删除、重置或修改除本任务目录 GPT_ACCEPTANCE.json 外的任何内容。",
            "结论：可确认通过则在本任务目录以 UTF-8 无 BOM 原子方式写 GPT_ACCEPTANCE.json（schema=1、taskId=" + context.TaskId + "、fingerprint=" + context.ContractFingerprint + "、verdict=accepted、verdictUtc=UTC、safeSummary、checksSummary）；",
            "需修复写 verdict=needs-repair；无法确认、证据缺失/错配/非终态或失败写 verdict=acceptance-failed。写完后结束 turn。"
        });
        return instruction;
    }
}