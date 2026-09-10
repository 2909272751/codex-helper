using System.Text.Json.Nodes;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// 用户显式保存的执行模型选择：provider 与模型 ID 都必须是 DSH <c>session.models</c> 返回的原样标识，
/// 绝不保存 UI 显示名、绝不拼接前缀或硬编码别名/版本名。空值表示"用户尚未选择"。
/// </summary>
public sealed record HarnessModelChoice(string? Provider, string? Model, string? ReasoningEffort)
{
    /// <summary>是否已保存完整选择（provider + 模型 ID 都非空）。</summary>
    public bool HasValue => !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>DSH 原始模型的 provider-qualified 展示文本（仅用于界面显示）。</summary>
    public string Describe() => HasValue ? $"{Provider}/{Model}" : "未选择";

    /// <summary>目录键，与 <see cref="HarnessSessionModels"/> 的判定一致。</summary>
    public string Key => (Provider ?? string.Empty) + "\u0000" + (Model ?? string.Empty);

    /// <summary>从设置读取用户选择（旧设置缺失即为"未选择"，绝不代用户猜测默认模型）。</summary>
    public static HarnessModelChoice FromSettings(AppSettings settings)
        => new(settings.HarnessSelectedModelProvider, settings.HarnessSelectedModel, settings.HarnessSelectedModelReasoningEffort);

    /// <summary>把用户选择写回设置（保存的是 DSH 原始 provider/model ID，不是显示名）。</summary>
    public void SaveTo(AppSettings settings)
    {
        settings.HarnessSelectedModelProvider = Provider ?? string.Empty;
        settings.HarnessSelectedModel = Model ?? string.Empty;
        settings.HarnessSelectedModelReasoningEffort = ReasoningEffort ?? string.Empty;
    }

    /// <summary>与 DSH 当前 current 是否一致（provider 与模型 ID 都相等）。</summary>
    public bool MatchesCurrent(string? provider, string? model)
        => HasValue
           && string.Equals(Provider, provider, StringComparison.Ordinal)
           && string.Equals(Model, model, StringComparison.Ordinal);
}

/// <summary>
/// DSH 运行时模型目录快照（全部来自 <c>session.models</c>，不硬编码任何 provider 或模型名）。
/// <see cref="Succeeded"/> 为 false 表示目录不可读：此时调用方必须保留最近一次已验证选择，
/// 但禁止提交新任务，并把 <see cref="Error"/> 明确告诉用户。
/// </summary>
public sealed record HarnessModelCatalog(
    bool Succeeded,
    IReadOnlyList<HarnessModelEntry> Models,
    string? CurrentProvider,
    string? CurrentModel,
    bool ProviderRoutable,
    string? Error)
{
    /// <summary>Host 当前会话正在使用的 provider-qualified 模型（用于"未选择时显示 Host current"）。</summary>
    public string CurrentText => string.IsNullOrWhiteSpace(CurrentModel)
        ? "未知"
        : string.IsNullOrWhiteSpace(CurrentProvider) ? CurrentModel! : CurrentProvider + "/" + CurrentModel;

    /// <summary>从 session.models 响应构造目录快照；响应形状不可信时返回不可读快照。</summary>
    public static HarnessModelCatalog FromValue(JsonNode? value)
    {
        var parsed = HarnessSessionModels.Parse(value);
        if (parsed is null)
            return new HarnessModelCatalog(false, Array.Empty<HarnessModelEntry>(), null, null, false, "DSH 返回的模型目录形状不可信。");
        return new HarnessModelCatalog(true, parsed.Available, parsed.Provider, parsed.Model, parsed.ProviderRoutable, null);
    }

    public static HarnessModelCatalog Failed(string error)
        => new(false, Array.Empty<HarnessModelEntry>(), null, null, false, error);
}

/// <summary>
/// 执行模型选择的应用与核验（SPEC 必须实现 2/4/5）。DSH 侧唯一合法的选模方式是官方
/// <c>session.selectModel</c>，且 <c>routable</c> 只表示 provider 有适配器、并不表示模型仍在目录中，
/// 因此本类统一执行：读目录 → 校验选择在目录内 → selectModel → 二次确认 current 与选择一致。
/// 任何一步失败都返回可读中文原因，调用方必须零 prompt 失败；本类绝不自动挑选替代模型、
/// 绝不取消或迁移旧会话（旧会话失效只给诊断与"请重新选择模型"）。
/// </summary>
public static class HarnessModelPicker
{
    /// <summary>一次模型应用的步骤轨迹（仅记录 RPC 方法与结果，供状态与报告说明真实顺序）。</summary>
    public sealed record ApplyResult(bool Success, string? ModelId, string? Error, IReadOnlyList<string> Steps);

    /// <summary>
    /// 读取某会话的运行时模型目录（失败返回不可读快照，绝不抛异常给调用方）。
    /// <paramref name="attempts"/> &gt; 1 仅用于"会话刚创建、Host 可能尚未完成模型解析"的场景：
    /// 只重复同一只读 RPC，绝不创建第二个会话；确认 current 一致时固定使用一次。
    /// </summary>
    public static async Task<HarnessModelCatalog> LoadCatalogAsync(
        HarnessRpcClient rpc, string sessionId, CancellationToken cancellationToken = default, int attempts = 1)
    {
        HarnessModelCatalog? last = null;
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            var models = await rpc.GetSessionModelsAsync(sessionId, cancellationToken);
            last = models.Success
                ? HarnessModelCatalog.FromValue(models.Value)
                : HarnessModelCatalog.Failed(models.ErrorMessage ?? "session.models 调用失败。");
            if (last.Succeeded) return last;
            if (attempt + 1 < attempts) await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        return last!;
    }

    /// <summary>
    /// 为新会话应用用户保存的模型选择（SPEC 必须实现 4）。顺序固定且不可跳过：
    /// ① session.models 读目录；② 校验保存选择（provider + 模型 ID）确实在目录中；③ session.selectModel；
    /// ④ 再次 session.models 确认 current 与选择一致。任一步失败都不发送 prompt（由调用方保证）。
    /// </summary>
    public static async Task<ApplyResult> ApplyToNewSessionAsync(
        HarnessRpcClient rpc, string sessionId, HarnessModelChoice choice, CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var catalog = await LoadCatalogAsync(rpc, sessionId, cancellationToken, attempts: 2);
        steps.Add("session.models");
        if (!catalog.Succeeded)
            return new ApplyResult(false, null, "无法读取 DSH 当前模型目录：" + (catalog.Error ?? "未知原因") + "；已保留最近一次已验证选择，但本轮不提交任务。", steps);
        if (catalog.Models.Count == 0)
            return new ApplyResult(false, null, "DSH 当前模型目录为空（没有可路由 provider/模型）；已保留最近一次已验证选择，但本轮不提交任务。", steps);

        if (!choice.HasValue)
        {
            // 用户尚未在设置中选择模型：只采用 Host 的 current/default，且必须可见（状态写明采用了哪个模型）。
            // 不调用 selectModel、绝不猜测或硬编码，也不把 Host 的默认值悄悄写成用户选择。
            if (string.IsNullOrWhiteSpace(catalog.CurrentModel))
                return new ApplyResult(false, null, "尚未在 Helper 的 Harness 设置中选择执行模型，且 DSH 未返回会话当前模型（session.models 无 current.model）。请刷新模型列表、重新选择模型后再提交。", steps);
            var currentKey = (catalog.CurrentProvider ?? string.Empty) + "\u0000" + catalog.CurrentModel;
            if (!catalog.Models.Any(model => string.Equals(model.Key, currentKey, StringComparison.Ordinal)))
                return new ApplyResult(false, null, $"尚未在 Helper 的 Harness 设置中选择执行模型，且 Host 当前/默认模型 {catalog.CurrentText} 不在 DSH 可路由目录中；请刷新模型列表、重新选择可路由模型后再提交。", steps);
            return new ApplyResult(true, catalog.CurrentModel, $"未保存模型选择：已可见地采用 Host 当前/默认模型 {catalog.CurrentText}（该模型已在本轮 DSH 目录中核验）。", steps);
        }

        var entry = catalog.Models.FirstOrDefault(model => string.Equals(model.Key, choice.Key, StringComparison.Ordinal));
        if (entry is null)
            return new ApplyResult(false, null, $"已保存的模型选择 {choice.Describe()} 已不在 DSH 当前模型目录中；请在 Helper 的 Harness 设置中重新选择模型后再提交。", steps);

        var selected = await rpc.SelectModelAsync(sessionId, entry.Provider, entry.ModelId, choice.ReasoningEffort, cancellationToken);
        steps.Add("session.selectModel");
        if (!selected.Success)
            return new ApplyResult(false, null, "session.selectModel 失败：" + (selected.ErrorMessage ?? "未知错误") + "；未提交任何合同正文。", steps);

        // selectModel 的响应本身也必须回显请求的 provider/model（防 Host 静默改成别的模型）。
        var selectedBlock = selected.Value?["selected"] as JsonObject;
        var echoedProvider = HarnessJson.Text(selectedBlock?["provider"]);
        var echoedModel = HarnessJson.Text(selectedBlock?["model"]);
        if (!string.Equals(echoedProvider, entry.Provider, StringComparison.Ordinal)
            || !string.Equals(echoedModel, entry.ModelId, StringComparison.Ordinal))
        {
            return new ApplyResult(false, null, "session.selectModel 返回的选择与请求不一致（未确认切换成功）；未提交任何合同正文。", steps);
        }

        var confirm = await LoadCatalogAsync(rpc, sessionId, cancellationToken);
        steps.Add("session.models(确认)");
        if (!confirm.Succeeded)
            return new ApplyResult(false, null, "切换模型后无法再次读取 session.models 确认：" + (confirm.Error ?? "未知原因") + "；未提交任何合同正文。", steps);
        if (!string.Equals(confirm.CurrentProvider, entry.Provider, StringComparison.Ordinal)
            || !string.Equals(confirm.CurrentModel, entry.ModelId, StringComparison.Ordinal))
        {
            return new ApplyResult(false, null, $"切换后的会话 current 仍是 {confirm.CurrentText}，与所选 {entry.Provider}/{entry.ModelId} 不一致；未提交任何合同正文。", steps);
        }
        return new ApplyResult(true, entry.ModelId, null, steps);
    }

    /// <summary>
    /// 核验已存在会话（同组键续接/接回运行中会话）的当前模型仍可路由，并明确该会话与用户选择的差异（SPEC 必须实现 5）。
    /// 只读校验：绝不 selectModel、绝不 cancel、绝不迁移；不可路由/目录不可读都返回失败原因，由调用方零 prompt 结束
    /// （新任务提交路径）或仅诊断（运行中会话的观察路径）。
    /// </summary>
    public static async Task<ApplyResult> EnsureSessionModelRoutableAsync(
        HarnessRpcClient rpc, string sessionId, HarnessModelChoice choice, CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var catalog = await LoadCatalogAsync(rpc, sessionId, cancellationToken);
        steps.Add("session.models");
        if (!catalog.Succeeded)
            return new ApplyResult(false, null, "无法读取 DSH 当前模型目录：" + (catalog.Error ?? "未知原因") + "；必须在设置中重新选择模型并确认后再继续。", steps);
        if (string.IsNullOrWhiteSpace(catalog.CurrentModel))
            return new ApplyResult(false, null, $"会话 {sessionId} 未返回当前模型（session.models 无 current.model），无法确认其可路由；请在设置中重新选择模型后再创建新的任务。", steps);

        var key = (catalog.CurrentProvider ?? string.Empty) + "\u0000" + catalog.CurrentModel;
        var entry = catalog.Models.FirstOrDefault(model => string.Equals(model.Key, key, StringComparison.Ordinal));
        if (entry is null)
            return new ApplyResult(false, null,
                $"会话 {sessionId} 当前模型 {catalog.CurrentText} 已不在 DSH 可路由模型目录中（{(catalog.ProviderRoutable ? "provider 有适配器但目录内已无该模型" : "provider 无适配器")}）；"
                + "请在 Helper 的 Harness 设置中重新选择模型后再创建新的任务，本 Helper 不会自动取消、迁移或改写该会话。", steps);

        var difference = choice.HasValue && !choice.MatchesCurrent(catalog.CurrentProvider, catalog.CurrentModel)
            ? $"（该会话当前模型 {catalog.CurrentText} 与设置中选择的 {choice.Describe()} 不同：同组键续接沿用会话自身的已确认模型，不做自动迁移）"
            : string.Empty;
        return new ApplyResult(true, entry.ModelId, difference.Length == 0 ? null : difference, steps);
    }
}
