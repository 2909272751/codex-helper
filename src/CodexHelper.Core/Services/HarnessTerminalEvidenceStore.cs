using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 任务目录内的最小可信终态证明（HARNESS_TERMINAL_EVIDENCE.json，第六阶段）。
/// 由本地监督器在"Runner 已退出 + HARNESS_STATUS.json 非 running + EXECUTION_REPORT.md 报告门禁通过"
/// 的时刻原子写入；绑定 taskId、合同指纹、Runner PID/启动身份（PID 重用防护）与终态状态/时间。
/// 证据文件只在上述完成条件成立时存在：监督器把终态落为 completed 时写入，其余终态
/// （cancelled/failed/uncertain）或复算离开完成态时删除，绝不残留可误用为"已通过"的旧证据。
/// 独立 GPT 验收以本文件（配合 HARNESS_SUPERVISOR.json）做任务目录内一致性核验；
/// 不再要求验收线程读取系统进程命令行或猜测其它 Runner。写入一律 UTF-8 无 BOM 原子写
/// （<see cref="AtomicFile"/>），不包含合同正文、凭据、token 或聊天内容。
/// </summary>
public sealed record HarnessTerminalEvidence(
    int SchemaVersion,
    string TaskId,
    string ContractFingerprint,
    int RunnerPid,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime RunnerStartedUtc,
    string TerminalOutcome,
    [property: JsonConverter(typeof(HarnessUtcConverter))] DateTime TerminalUtc,
    int? RunnerExitCode = null,
    string? TerminalState = null)
{
    public bool IsTerminalCompleted
        => string.Equals(TerminalOutcome, HarnessSupervisor.OutcomeCompleted, StringComparison.OrdinalIgnoreCase);
}

/// <summary>HARNESS_TERMINAL_EVIDENCE.json 的常量与宽容读写宿主。</summary>
public static class HarnessTerminalEvidenceStore
{
    public const string RecordFileName = "HARNESS_TERMINAL_EVIDENCE.json";
    public const int RecordSchemaVersion = 1;

    public static string RecordPath(string taskDirectory) => Path.Combine(taskDirectory, RecordFileName);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new HarnessUtcConverter() }
    };

    /// <summary>宽容读取可信终态证据；缺失/损坏返回 null（绝不抛异常，绝不猜测结论）。</summary>
    public static HarnessTerminalEvidence? TryReadRecord(string taskDirectory)
    {
        var path = RecordPath(taskDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<HarnessTerminalEvidence>(text, Options);
        }
        catch { return null; }
    }

    /// <summary>原子写入可信终态证据（UTF-8 无 BOM；失败上抛，绝不静默丢证据）。</summary>
    public static void WriteRecord(string taskDirectory, HarnessTerminalEvidence evidence)
        => AtomicFile.WriteAllText(RecordPath(taskDirectory), JsonSerializer.Serialize(evidence, Options));

    /// <summary>删除可信终态证据（幂等；写入中断/删除失败静默保留，读方以内容核验为准）。</summary>
    public static void TryDelete(string taskDirectory)
    {
        try { File.Delete(RecordPath(taskDirectory)); }
        catch { /* 保留旧文件；读取侧仍按 taskId/指纹/终态严格核验，绝不凭存在性通过 */ }
    }

    /// <summary>
    /// 一致性核验（供验收协调器/读取链路使用，第七阶段收紧）：证据通过必须【同时】绑定
    /// taskId、合同指纹、terminalState、runnerPid、runnerStartedUtc，并与 HARNESS_SUPERVISOR.json
    /// 的同一 Runner 身份（PID + 启动 UTC）及 HARNESS_STATUS.json 的非运行终态一致。
    /// 任一字段缺失、taskId/指纹错配、非完成终态、Runner 身份错配、监督记录缺失/矛盾、
    /// HARNESS_STATUS 缺失/仍在运行/状态与证据矛盾，一律保守失败；绝不凭证据文件存在性放行。
    /// </summary>
    public static EvidenceCheck Check(string taskDirectory, string expectedTaskId, string? expectedFingerprint)
    {
        var evidence = TryReadRecord(taskDirectory);
        if (evidence is null)
            return EvidenceCheck.Fail("任务目录缺少可信终态证据（" + RecordFileName + "）");
        if (!string.Equals(evidence.TaskId, expectedTaskId, StringComparison.Ordinal))
            return EvidenceCheck.Fail("可信终态证据 taskId 与当前任务不匹配（" + HarnessAcceptanceQueue.Safe(evidence.TaskId) + "）");
        if (string.IsNullOrWhiteSpace(expectedFingerprint)
            || !string.Equals(evidence.ContractFingerprint, expectedFingerprint, StringComparison.Ordinal))
            return EvidenceCheck.Fail("可信终态证据合同指纹与当前任务不匹配");
        if (!evidence.IsTerminalCompleted)
            return EvidenceCheck.Fail("可信终态证据非完成终态（" + HarnessAcceptanceQueue.Safe(evidence.TerminalOutcome) + "）");
        if (string.IsNullOrWhiteSpace(evidence.TerminalState))
            return EvidenceCheck.Fail("可信终态证据缺少 terminalState，无法与任务终态核对");
        if (evidence.RunnerPid <= 0 || evidence.RunnerStartedUtc == default)
            return EvidenceCheck.Fail("可信终态证据缺少 Runner 启动身份（PID/启动时间）");

        // 与 HARNESS_SUPERVISOR.json 同一 Runner 身份：监督记录必须存在、已到完成终态，
        // taskId/合同指纹非空且与当前任务一致，且 PID 与启动 UTC 都一致（PID 重用防护）。
        var supervisor = HarnessSupervisor.TryReadRecord(taskDirectory);
        if (supervisor is null)
            return EvidenceCheck.Fail("任务目录缺少 HARNESS_SUPERVISOR.json，无法核对 Runner 身份（保守拒绝）");
        if (string.IsNullOrWhiteSpace(supervisor.TaskId))
            return EvidenceCheck.Fail("HARNESS_SUPERVISOR.json 缺少 taskId，无法核对任务归属（保守拒绝）");
        if (string.IsNullOrWhiteSpace(supervisor.ContractFingerprint))
            return EvidenceCheck.Fail("HARNESS_SUPERVISOR.json 缺少合同指纹，无法核对任务归属（保守拒绝）");
        if (!string.Equals(supervisor.TaskId, expectedTaskId, StringComparison.Ordinal))
            return EvidenceCheck.Fail("HARNESS_SUPERVISOR.json taskId 与当前任务不一致（" + HarnessAcceptanceQueue.Safe(supervisor.TaskId) + "）");
        if (!string.Equals(supervisor.ContractFingerprint, expectedFingerprint, StringComparison.Ordinal))
            return EvidenceCheck.Fail("HARNESS_SUPERVISOR.json 合同指纹与当前任务不一致");
        if (!supervisor.IsTerminal
            || !string.Equals(supervisor.TerminalOutcome, HarnessSupervisor.OutcomeCompleted, StringComparison.OrdinalIgnoreCase))
            return EvidenceCheck.Fail("HARNESS_SUPERVISOR.json 未到完成终态，与可信终态证据矛盾（保守拒绝）");
        if (supervisor.RunnerPid != evidence.RunnerPid)
            return EvidenceCheck.Fail("可信终态证据 Runner PID 与监督记录不一致（" + evidence.RunnerPid + " vs " + supervisor.RunnerPid + "）");
        if ((supervisor.RunnerStartedUtc - evidence.RunnerStartedUtc).Duration() > TimeSpan.FromSeconds(2))
            return EvidenceCheck.Fail("可信终态证据 Runner 启动身份与监督记录不一致（PID 重用防护，保守拒绝）");

        // 与 HARNESS_STATUS.json 的非运行终态一致：状态存在、非 running、与证据终态同值。
        var truth = TryReadTaskStatus(taskDirectory);
        if (truth is null)
            return EvidenceCheck.Fail("任务目录缺少 HARNESS_STATUS.json，无法核对任务终态（保守拒绝）");
        if (truth.IsRunning)
            return EvidenceCheck.Fail("HARNESS_STATUS.json 仍为运行状态（" + HarnessAcceptanceQueue.Safe(truth.State) + "），与完成终态证据矛盾");
        if (!string.Equals(truth.State, evidence.TerminalState, StringComparison.OrdinalIgnoreCase))
            return EvidenceCheck.Fail("HARNESS_STATUS.json 终态与证据 terminalState 不一致（"
                + HarnessAcceptanceQueue.Safe(truth.State) + " vs " + HarnessAcceptanceQueue.Safe(evidence.TerminalState) + "）");
        if (!string.Equals(truth.TaskId, evidence.TaskId, StringComparison.Ordinal))
            return EvidenceCheck.Fail("HARNESS_STATUS.json taskId 与可信终态证据不一致");
        if (string.IsNullOrWhiteSpace(truth.ContractFingerprint)
            || !string.Equals(truth.ContractFingerprint, evidence.ContractFingerprint, StringComparison.Ordinal))
            return EvidenceCheck.Fail("HARNESS_STATUS.json 合同指纹与可信终态证据不一致");
        return EvidenceCheck.Ok();
    }

    /// <summary>宽容读取任务目录真相源 HARNESS_STATUS.json（与监督器同一语义：容忍旧日期/损坏）。</summary>
    private static HarnessTaskStatus? TryReadTaskStatus(string taskDirectory)
    {
        var path = Path.Combine(taskDirectory, HarnessTaskStateStore.StatusFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new HarnessUtcConverter() }
            };
            return JsonSerializer.Deserialize<HarnessTaskStatus>(text, options);
        }
        catch { return null; }
    }

    /// <summary>证据核验结果（Valid=false 时 Reason 为可诊断保守失败原因）。</summary>
    public sealed record EvidenceCheck(bool Valid, string Reason)
    {
        public static EvidenceCheck Ok() => new(true, "通过");
        public static EvidenceCheck Fail(string reason) => new(false, reason);
    }
}
