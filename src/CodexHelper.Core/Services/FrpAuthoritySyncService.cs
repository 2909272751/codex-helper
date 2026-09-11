using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// 88frp 公网 authority 与 DSH 远程信任的同步状态（脱敏：只登记 authority 字符串、时间、
/// pending 原因与最近可读错误，绝不登记 88frp 认证字段、token、cookie 或 DSH 凭据）。
/// </summary>
public sealed record FrpAuthorityStatus
{
    /// <summary>已接受公网 authority、允许远程只读 RPC 的端口。</summary>
    public const string Verified = "verified";
    /// <summary>patch 已按检测入口更新，但 Host 尚未接受该 Origin。</summary>
    public const string Pending = "pending";
    /// <summary>检测入口被去抖暂存（尚未确认为稳定变化）。</summary>
    public const string Debouncing = "debouncing";
    /// <summary>未检测到唯一 authority（缺失、不完整或多实例歧义）。</summary>
    public const string Unknown = "unknown";
    /// <summary>上次同步失败（可读原因见 Error）。</summary>
    public const string Failed = "failed";

    /// <summary>最近一次从 88frp runtime 配置解析出的 authority（权威入口）。</summary>
    public string? DetectedAuthority { get; set; }
    /// <summary>最近一次经公网 Origin 只读 RPC 验证成功的 authority（已验证入口）。</summary>
    public string? VerifiedAuthority { get; set; }
    /// <summary>最近一次验证成功时间（UTC）。</summary>
    public DateTimeOffset? VerifiedUtc { get; set; }
    /// <summary>最近一次同步时间（UTC）。</summary>
    public DateTimeOffset? UpdatedUtc { get; set; }
    /// <summary>待切换/待安全窗口的原因；无 pending 时为 null。</summary>
    public string? PendingReason { get; set; }
    /// <summary>最近一次可读错误（脱敏、有界长度）。</summary>
    public string? Error { get; set; }
    /// <summary>最近一次状态分类（见本类常量）。</summary>
    public string State { get; set; } = Unknown;

    /// <summary>去抖候选：最近一次解析到的入口。</summary>
    public string? CandidateAuthority { get; set; }
    /// <summary>去抖候选首次出现时间（UTC）。</summary>
    public DateTimeOffset? CandidateSinceUtc { get; set; }

    /// <summary>已因该入口执行过受控 Host 重启（同一入口不重复重启，避免重启风暴）。</summary>
    public string? RestartPendingAuthority { get; set; }

    /// <summary>状态是否已对公网 authority 生效（可远程只读 RPC）。</summary>
    public bool IsVerified => string.Equals(State, Verified, StringComparison.Ordinal);

    /// <summary>界面/报告用一行脱敏摘要。</summary>
    public string Describe()
    {
        var detected = string.IsNullOrWhiteSpace(DetectedAuthority) ? "未检测到" : DetectedAuthority;
        var verified = string.IsNullOrWhiteSpace(VerifiedAuthority) ? "尚未验证" : VerifiedAuthority;
        var lines = new List<string>
        {
            $"88frp 检测入口：{detected}",
            $"已验证入口：{verified}" +
            (VerifiedUtc is { } time ? $"（{time.ToLocalTime():yyyy-MM-dd HH:mm}）" : string.Empty),
            "状态：" + DescribeState()
        };
        if (!string.IsNullOrWhiteSpace(PendingReason)) lines.Add("待处理：" + PendingReason);
        if (!string.IsNullOrWhiteSpace(Error)) lines.Add("最近错误：" + Error);
        return string.Join("；", lines) + "。";
    }

    private string DescribeState() => State switch
    {
        FrpAuthorityStatus.Verified => "公网 origin 已验证（只读 RPC 通过）",
        FrpAuthorityStatus.Pending => "patch 已更新，等待 Host 接受新 origin",
        FrpAuthorityStatus.Debouncing => "检测到入口变化，正在稳定性确认（去抖）",
        FrpAuthorityStatus.Failed => "同步失败",
        _ => "未检测到唯一 88frp DSH 入口"
    };

    /// <summary>复制一份用于持久化，绝不包含本类以外字段。</summary>
    public FrpAuthorityStatus Copy() => this with { };
}

/// <summary>同步状态的持久化（原子写入；损坏文件回退为初始状态而不是抛出）。</summary>
public interface IFrpAuthorityStateStore
{
    FrpAuthorityStatus Load();
    void Save(FrpAuthorityStatus status);
}

/// <summary>默认实现：%LOCALAPPDATA%\CodexHelper\frp-authority-sync.json（可注入隔离目录用于测试）。</summary>
public sealed class FrpAuthorityStateFileStore : IFrpAuthorityStateStore
{
    private readonly string statePath;
    private readonly JsonStore store = new();

    public FrpAuthorityStateFileStore(string? filePath = null)
        => statePath = filePath ?? System.IO.Path.Combine(new AppPaths().BaseDirectory, "frp-authority-sync.json");

    public string Path => statePath;

    public FrpAuthorityStatus Load()
    {
        try
        {
            var status = store.LoadOrCreate(statePath, static () => new FrpAuthorityStatus());
            return status ?? new FrpAuthorityStatus();
        }
        catch { return new FrpAuthorityStatus(); }
    }

    public void Save(FrpAuthorityStatus status)
    {
        try { store.Save(statePath, status.Copy()); } catch { /* 状态持久化失败不影响本次同步结论 */ }
    }
}

/// <summary>内存实现：单元测试与纯逻辑验证专用。</summary>
public sealed class FrpAuthorityMemoryStore : IFrpAuthorityStateStore
{
    private FrpAuthorityStatus status = new();

    public FrpAuthorityStatus Load() => status.Copy();

    public void Save(FrpAuthorityStatus value) => status = value.Copy();
}

/// <summary>受管 patch 更新结果：Changed=false 表示当前文件已与目标一致（不写盘）。</summary>
public sealed record FrpAuthorityPatchUpdate(bool Changed, string UpdatedText, IReadOnlyList<string> UpdatedEntries)
{
    /// <summary>两份文本等价（严格逐字节比较，避免无变化写盘）。</summary>
    public bool IsEquivalent(string? existing)
        => !Changed && string.Equals(existing ?? string.Empty, UpdatedText, StringComparison.Ordinal);
}

/// <summary>公网 Origin 验证结果（只读 session.list；RunningSessions 为托管会话数的保守估计）。</summary>
public sealed record FrpOriginVerification(bool Accepted, string? Error, int ActiveSessions)
{
    public static FrpOriginVerification Ok(int activeSessions) => new(true, null, activeSessions);
    public static FrpOriginVerification Reject(string error, int activeSessions = 0) => new(false, error, activeSessions);
}

/// <summary>一次同步检查的结果（脱敏、可回报给 UI/日志）。</summary>
public sealed record FrpAuthoritySyncOutcome(FrpAuthorityStatus Status, string Message, bool Restarted, bool PatchWritten)
{
    public bool Verified => Status.IsVerified;
}

/// <summary>
/// DSH 动态入口与远程信任同步：解析 88frp 为 127.0.0.1:3080 分配的公网 authority →
/// 去抖确认 → 原子更新 Helper 受管的 profile patch（web-runtime.trustedHosts 与
/// files-toolkit.trustedUploadHosts，只改对应列表项并备份）→ 以该 authority 作 Origin
/// 做只读 session.list 验证 → 仅在验证失败且没有运行中会话时做一次受控 Host 重启。
/// <para>
/// 安全边界：解析结果为空/不完整/多实例歧义时绝不改动已有信任（保留上次已验证入口）；
/// 重启必须由注入的 <c>RestartHostAsync</c> 执行，服务自身从不杀进程；检测、状态与日志
/// 绝不包含 88frp 用户/认证字段、token、cookie 或 DSH 凭据。
/// </para>
/// </summary>
public sealed class FrpAuthoritySyncService
{
    private readonly IFrpAuthorityStateStore store;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();

    public FrpAuthoritySyncService(IFrpAuthorityStateStore? store = null, Func<DateTimeOffset>? clock = null)
    {
        this.store = store ?? new FrpAuthorityStateFileStore();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>当前状态快照（脱敏）。</summary>
    public FrpAuthorityStatus Snapshot() => store.Load();

    /// <summary>界面用一行摘要。</summary>
    public string DescribeStatus() => Snapshot().Describe();

    public Task<FrpAuthoritySyncOutcome> CheckAsync(FrpAuthoritySyncOptions options, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(options, cancellationToken), cancellationToken);

    /// <summary>同步核心（同步实现：便于单测直接驱动状态机）。</summary>
    public FrpAuthoritySyncOutcome Check(FrpAuthoritySyncOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (gate)
        {
            var previous = store.Load();
            previous.DetectedAuthority = null;
            var now = clock();
            IReadOnlyList<string> candidates;
            try { candidates = options.ResolveAuthorities(); }
            catch (Exception ex)
            {
                previous.Error = Sanitize("读取 88frp 运行时配置失败：" + ex.Message);
                previous.PendingReason ??= "无法读取 88frp 运行时配置，保留既有信任。";
                previous.State = previous.IsVerified ? FrpAuthorityStatus.Pending : FrpAuthorityStatus.Failed;
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, previous.Error!, false, false);
            }

            var unique = candidates
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            if (unique.Length == 0)
            {
                // 文件暂时缺失/不完整：绝不清空已生效信任（保留 VerifiedAuthority 与状态）。
                previous.Error = null;
                previous.PendingReason = previous.PendingReason ?? "尚未检测到唯一 88frp DSH 入口，保留既有信任。";
                previous.State = previous.IsVerified ? FrpAuthorityStatus.Verified : FrpAuthorityStatus.Unknown;
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, "未检测到唯一 88frp DSH 入口；既有信任保持不变。", false, false);
            }
            if (unique.Length > 1)
            {
                previous.Error = null;
                previous.PendingReason = "88frp 多实例歧义（检测到多个 DSH 入口），保留既有信任。";
                previous.State = previous.IsVerified ? FrpAuthorityStatus.Verified : FrpAuthorityStatus.Unknown;
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, "88frp 多实例歧义，未改动信任配置。", false, false);
            }

            var detected = unique[0];
            if (!string.Equals(detected, previous.RestartPendingAuthority, StringComparison.OrdinalIgnoreCase))
                previous.RestartPendingAuthority = null;
            // 去抖：只有持续稳定达到阈值的变化才切换信任。
            if (!string.Equals(detected, options.DebounceConfirmedAuthority, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(detected, previous.CandidateAuthority, StringComparison.OrdinalIgnoreCase) || previous.CandidateSinceUtc is null)
                {
                    previous.CandidateAuthority = detected;
                    previous.CandidateSinceUtc = now;
                }
                var stableFor = now - previous.CandidateSinceUtc!.Value;
                if (stableFor < options.DebounceWindow)
                {
                    previous.State = previous.IsVerified ? FrpAuthorityStatus.Verified : FrpAuthorityStatus.Debouncing;
                    previous.PendingReason = "检测到入口变化，正在稳定性确认（去抖）。";
                    previous.Error = null;
                    previous.UpdatedUtc = now;
                    store.Save(previous);
                    return new(previous, $"检测到入口 {detected}，稳定性确认中（已 {(int)stableFor.TotalSeconds} 秒）。", false, false);
                }
            }

            previous.CandidateAuthority = detected;
            previous.CandidateSinceUtc ??= now;
            previous.DetectedAuthority = detected;
            previous.Error = null;

            // 1) patch 需要更新时先备份再原子写入；失败回滚并保留旧信任，不做任何验证/重启。
            var patchWritten = false;
            if (!string.IsNullOrWhiteSpace(options.PatchPath))
            {
                try
                {
                    var existing = File.Exists(options.PatchPath) ? File.ReadAllText(options.PatchPath) : null;
                    var update = FrpAuthorityPatchDocument.Merge(existing, detected, options.PatchAuthorities);
                    if (update.Changed)
                    {
                        BackupPatch(options.PatchPath);
                        AtomicFile.WriteAllText(options.PatchPath, update.UpdatedText);
                        patchWritten = true;
                    }
                }
                catch (Exception ex)
                {
                    previous.State = previous.IsVerified ? FrpAuthorityStatus.Pending : FrpAuthorityStatus.Failed;
                    previous.PendingReason = "更新 DSH profile patch 失败，已回滚并保留既有信任。";
                    previous.Error = Sanitize("更新 DSH profile patch 失败：" + ex.Message);
                    previous.UpdatedUtc = now;
                    store.Save(previous);
                    return new(previous, previous.Error!, false, false);
                }
            }

            // 2) 已经是当前已验证入口 → 保持已验证状态，只刷新时间。
            if (string.Equals(detected, previous.VerifiedAuthority, StringComparison.OrdinalIgnoreCase) && previous.VerifiedUtc is not null)
            {
                previous.State = FrpAuthorityStatus.Verified;
                previous.PendingReason = null;
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, $"88frp 入口未变化（{detected}），已验证状态保持。", false, patchWritten);
            }

            // 3) 公网 Origin 只读验证；成功是唯一"已生效"证据。
            if (options.VerifyOriginAsync is null)
            {
                previous.State = FrpAuthorityStatus.Pending;
                previous.PendingReason = "缺少公网 Origin 验证通道，patch 已更新但未验证。";
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, "patch 已更新；未配置验证通道，等待接管方验证。", false, patchWritten);
            }

            var verification = Verify(options, detected, cancellationToken);
            if (verification.Accepted)
            {
                previous.VerifiedAuthority = detected;
                previous.VerifiedUtc = now;
                previous.State = FrpAuthorityStatus.Verified;
                previous.PendingReason = null;
                previous.Error = null;
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, $"公网 origin 验证通过（{detected}），远程只读 RPC 可用。", false, patchWritten);
            }

            // 4) Host 不接受新 Origin：只有没有运行中会话、提供了受控重启且尚未因该入口重启过时才重启一次。
            previous.Error = Sanitize(verification.Error);
            if (verification.ActiveSessions > 0)
            {
                previous.State = FrpAuthorityStatus.Pending;
                previous.PendingReason = $"存在 {verification.ActiveSessions} 个运行中 Harness 会话，延后 Host 重启（不中断编码任务）。";
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, previous.PendingReason!, false, patchWritten);
            }
            if (options.RestartHostAsync is null || !options.RestartAllowed)
            {
                previous.State = FrpAuthorityStatus.Pending;
                previous.PendingReason = options.RestartAllowed
                    ? "当前启动方式没有可托管的 Host 句柄，端口将随下一次 Host 启动生效。"
                    : "当前同步路径不重启 Host，端口将随下一次 Host 启动生效。";
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, previous.PendingReason!, false, patchWritten);
            }
            if (string.Equals(previous.RestartPendingAuthority, detected, StringComparison.OrdinalIgnoreCase))
            {
                // 同一入口不重复重启：本轮只保留 pending 与可读错误，避免反复杀进程造成重启风暴。
                previous.State = FrpAuthorityStatus.Pending;
                previous.PendingReason = "已因该入口执行过受控 Host 重启，等待下一次安全检查窗口。";
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, previous.PendingReason!, false, patchWritten);
            }

            try
            {
                options.RestartHostAsync(detected, cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                previous.RestartPendingAuthority = detected;
                previous.State = FrpAuthorityStatus.Pending;
                previous.PendingReason = "受控 Host 重启失败，旧信任保留，稍后重试。";
                previous.Error = Sanitize("受控 Host 重启失败：" + ex.Message);
                previous.UpdatedUtc = now;
                store.Save(previous);
                return new(previous, previous.Error!, false, patchWritten);
            }

            previous.RestartPendingAuthority = detected;
            previous.State = FrpAuthorityStatus.Pending;
            previous.PendingReason = "已完成一次受控 Host 重启，等待新 origin 验证。";
            previous.UpdatedUtc = now;
            store.Save(previous);
            return new(previous, "新 origin 尚未被接受，已执行一次受控 Host 重启；下次检查验证。", true, patchWritten);
        }
    }

    private FrpOriginVerification Verify(FrpAuthoritySyncOptions options, string authority, CancellationToken cancellationToken)
    {
        try
        {
            var task = options.VerifyOriginAsync!(authority, cancellationToken);
            var completed = task.Wait(options.VerifyTimeout);
            if (!completed)
            {
                return FrpOriginVerification.Reject(
                    $"公网 origin 验证在 {options.VerifyTimeout.TotalSeconds:0.#} 秒内未返回（{authority}）。");
            }
            return task.Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FrpOriginVerification.Reject("公网 origin 验证失败：" + ex.Message);
        }
    }

    /// <summary>写入前备份现有 patch（同目录，带时间戳），失败时由调用方回滚。</summary>
    private void BackupPatch(string patchPath)
    {
        if (!File.Exists(patchPath)) return;
        var stamp = clock().ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = patchPath + ".bak-frp-origin-sync-" + stamp;
        try { File.Copy(patchPath, backup, overwrite: true); }
        catch { /* 备份失败不阻断写入；原子写入本身仍可回滚到磁盘上的原文件 */ }
    }

    /// <summary>脱敏 + 有界长度：错误文本绝不携带配置原文或凭据。</summary>
    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "未知错误。";
        var value = new StringBuilder(text.Length);
        foreach (var ch in text)
            value.Append(ch is '\r' or '\n' or '\t' ? ' ' : ch);
        var trimmed = value.ToString().Trim();
        return trimmed.Length <= 240 ? trimmed : trimmed[..240] + "…";
    }
}

/// <summary>一次同步检查的注入点（全部可替换，便于无网络、无真实 Host 的单测）。</summary>
public sealed class FrpAuthoritySyncOptions
{
    /// <summary>允许本次检查做一次受控 Host 重启（默认不允许；仍需同时提供 RestartHostAsync）。</summary>
    public bool RestartAllowed { get; init; }
    /// <summary>authority 解析器（默认读取 88frp 实例根目录）。</summary>
    public Func<IReadOnlyList<string>> ResolveAuthorities { get; init; } = static () => Array.Empty<string>();
    /// <summary>去抖窗口：候选入口需持续稳定这么久才切换信任。</summary>
    public TimeSpan DebounceWindow { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>已去抖确认的入口（不相等时说明是变化，需要重新走去抖）。</summary>
    public string? DebounceConfirmedAuthority { get; init; }
    /// <summary>受管 profile patch 绝对路径（空表示不动 patch）。</summary>
    public string? PatchPath { get; init; }
    /// <summary>需要与 authority 一致的受管 patch 入口列表；默认两项。</summary>
    public IReadOnlyList<string> PatchAuthorities { get; init; } = FrpAuthorityPatchDocument.ManagedEntries;
    /// <summary>公网 origin 只读验证（null 表示无验证通道）。</summary>
    public Func<string, CancellationToken, Task<FrpOriginVerification>>? VerifyOriginAsync { get; init; }
    /// <summary>验证超时（默认 8 秒）。</summary>
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>受控 Host 重启（仅在没有运行中会话时调用一次）。</summary>
    public Func<string, CancellationToken, Task>? RestartHostAsync { get; init; }
}

/// <summary>
/// DSH profile patch 定位：DSH Home 解析与官方一致（显式路径 &gt; $DSH_HOME &gt; ~/.dsh），
/// profile 目录优先 <c>web</c>（DSH Web Host 使用的 profile），否则取唯一含 patch 的子目录。
/// 只读定位，不做任何遍历仓库或写入。
/// </summary>
public static class DshProfilePatchLocator
{
    public const string PatchFileName = "cordis.patch.yml";

    public static string ResolveDshHome(string? explicitHome = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitHome)) return Path.GetFullPath(explicitHome);
        var envHome = Environment.GetEnvironmentVariable("DSH_HOME");
        return string.IsNullOrWhiteSpace(envHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
            : Path.GetFullPath(envHome);
    }

    public static string ResolveProfilesRoot(string? explicitHome = null)
        => Path.Combine(ResolveDshHome(explicitHome), "profiles");

    /// <summary>优先 web profile；缺失时回退到唯一持有 patch 文件的 profile 目录。</summary>
    public static string? TryResolveDefaultPatchPath(string? explicitHome = null)
    {
        try
        {
            var profiles = ResolveProfilesRoot(explicitHome);
            if (!Directory.Exists(profiles)) return null;
            var web = Path.Combine(profiles, "web", PatchFileName);
            if (File.Exists(web)) return web;
            var candidates = Directory.EnumerateDirectories(profiles, "*", SearchOption.TopDirectoryOnly)
                .Select(directory => Path.Combine(directory, PatchFileName))
                .Where(File.Exists)
                .Take(2)
                .ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// Helper 受管的 DSH profile patch 文本编辑：只改 <c>- id: web-runtime</c> 的
/// <c>config.trustedHosts</c> 与 <c>- id: files-toolkit</c> 的 <c>config.trustedUploadHosts</c>
/// 这两个列表项，保持其余行（注释、其他插件条目、插件开关标记）逐字节不变。
/// </summary>
public static class FrpAuthorityPatchDocument
{
    /// <summary>受管入口的 "插件 id + 配置键" 组合。</summary>
    public static readonly IReadOnlyList<string> ManagedEntries =
        new[] { "web-runtime:trustedHosts", "files-toolkit:trustedUploadHosts" };

    /// <summary>
    /// 把两处受管列表项合并为给定 authority。existing 为 null 时视为空文档（将新建条目）。
    /// 返回结果中包含是否发生变化，未变化时调用方不得写盘。
    /// </summary>
    public static FrpAuthorityPatchUpdate Merge(string? existing, string authority, IReadOnlyList<string>? entries = null)
    {
        var targets = entries ?? ManagedEntries;
        var newline = (existing ?? string.Empty).Contains('\r', StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = existing ?? string.Empty;
        var updated = new List<string>();
        foreach (var target in targets)
        {
            var split = target.Split(':', 2);
            if (split.Length != 2) continue;
            var result = SetManagedEntry(text, split[0], split[1], authority, newline);
            text = result.Text;
            if (result.Replaced) updated.Add(target);
        }
        var changed = !string.Equals(existing ?? string.Empty, text, StringComparison.Ordinal);
        return new(changed, text, updated);
    }

    private sealed record SetResult(string Text, bool Replaced);

    private static SetResult SetManagedEntry(string text, string entryId, string configKey, string authority, string newline)
    {
        var lines = new List<string>();
        if (text.Length > 0) lines.AddRange(text.Split('\n').Select(line => line.TrimEnd('\r')));
        var entryLine = -1;
        var entryIndent = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var parsed = ParseLine(lines[index]);
            if (!parsed.HasContent || !string.Equals(parsed.Key, "id", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(parsed.Value, entryId, StringComparison.OrdinalIgnoreCase)) continue;
            entryLine = index;
            entryIndent = parsed.Indent;
            break;
        }
        if (entryLine < 0)
        {
            // 文档缺少该受管条目（profile patch 尚未包含 DSH 隧道配置）：追加完整受管条目，
            // 不触碰任何已有内容；下次同步即可核对并修正。
            var appended = new List<string>(lines);
            if (appended.Count > 0 && appended[^1].Length != 0) appended.Add(string.Empty);
            appended.Add("- id: " + entryId);
            appended.Add("  config:");
            appended.Add("    " + configKey + ":");
            appended.Add("      - '" + authority + "'");
            return new(Compose(appended, newline, preserveTrailingNewline: true), true);
        }

        var entryEnd = lines.Count;
        for (var index = entryLine + 1; index < lines.Count; index++)
        {
            var parsed = ParseLine(lines[index]);
            if (parsed.HasContent && parsed.Key == "id" && parsed.Indent <= entryIndent) { entryEnd = index; break; }
        }

        var configLine = -1;
        for (var index = entryLine + 1; index < entryEnd; index++)
        {
            var parsed = ParseLine(lines[index]);
            if (!parsed.HasContent) continue;
            if (string.Equals(parsed.Key, "config", StringComparison.OrdinalIgnoreCase))
            {
                configLine = parsed.InlineMap ? -1 : index;
                break;
            }
        }

        if (configLine < 0)
        {
            var insertAt = entryLine + 1;
            lines.Insert(insertAt, Indent(entryIndent + 2) + "config:");
            lines.Insert(insertAt + 1, Indent(entryIndent + 4) + configKey + ":");
            lines.Insert(insertAt + 2, Indent(entryIndent + 6) + "- '" + authority + "'");
            return new(Compose(lines, newline, text.EndsWith(newline, StringComparison.Ordinal)), true);
        }

        var configIndent = IndentOf(lines[configLine]);
        var configEnd = entryEnd;
        for (var index = configLine + 1; index < entryEnd; index++)
        {
            var parsed = ParseLine(lines[index]);
            if (parsed.HasContent && parsed.Indent <= configIndent) { configEnd = index; break; }
        }

        var keyLine = -1;
        for (var index = configLine + 1; index < configEnd; index++)
        {
            var parsed = ParseLine(lines[index]);
            if (!parsed.HasContent) continue;
            if (!string.Equals(parsed.Key, configKey, StringComparison.OrdinalIgnoreCase)) continue;
            keyLine = index;
            break;
        }

        var desired = new List<string> { Indent(configIndent + 2) + "- '" + authority + "'" };
        if (keyLine < 0)
        {
            var insertAt = configEnd;
            while (insertAt > configLine + 1 && ParseLine(lines[insertAt - 1]).IsBlank) insertAt--;
            lines.InsertRange(insertAt, new[] { Indent(configIndent + 1) + configKey + ":" }.Concat(desired));
            return new(Compose(lines, newline, text.EndsWith(newline, StringComparison.Ordinal)), true);
        }

        var parsedKey = ParseLine(lines[keyLine]);
        var keyIndent = parsedKey.Indent;
        var desiredIndent = keyIndent + 2;
        var replacement = new List<string> { lines[keyLine] };
        replacement.AddRange(desired.Select(line => Indent(desiredIndent) + line.TrimStart()));

        // 现有值范围：键行之后到下一个同级键（缩进 <= keyIndent）为止。
        var valueEnd = keyLine + 1;
        while (valueEnd < configEnd)
        {
            var parsed = ParseLine(lines[valueEnd]);
            if (parsed.HasContent) break;
            valueEnd++;
        }
        if (valueEnd < configEnd)
        {
            var valueIndent = IndentOf(lines[valueEnd]);
            var end = valueEnd + 1;
            while (end < configEnd)
            {
                var parsed = ParseLine(lines[end]);
                if (parsed.HasContent && parsed.Indent < valueIndent) break;
                end++;
            }
            lines.RemoveRange(keyLine, end - keyLine);
        }
        else
        {
            lines.RemoveAt(keyLine);
        }
        lines.InsertRange(keyLine, replacement);
        return new(Compose(lines, newline, text.EndsWith(newline, StringComparison.Ordinal)), true);
    }

    private sealed record ParsedLine(bool HasContent, int Indent, string Key, string Value, bool InlineMap, bool IsBlank);

    private static ParsedLine ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return new(false, 0, string.Empty, string.Empty, false, true);
        var content = trimmed.StartsWith("- ") ? trimmed[2..].Trim() : trimmed;
        var separator = content.IndexOf(':');
        if (separator <= 0) return new(false, 0, string.Empty, string.Empty, false, false);
        var key = content[..separator].Trim();
        var value = content[(separator + 1)..];
        var hash = value.IndexOf('#');
        if (hash >= 0) value = value[..hash];
        value = value.Trim().Trim('\'', '"');
        // 只把形如 `key: value` / `key:` 的行当作映射键；`- 'a:b'` 这类列表值不参与。
        if (key.Length == 0 || key.Contains(' ') || key.Contains(':')) return new(false, 0, string.Empty, string.Empty, false, false);
        var inlineMap = value.StartsWith('{') && value.EndsWith('}');
        return new(true, IndentOf(line), key, value, inlineMap, false);
    }

    private static int IndentOf(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
    }

    private static string Indent(int count) => new(' ', count);

    /// <summary>
    /// 把行集拼回文本：<c>Split('\n')</c> 会让以换行结尾的原文在行集末尾留下一个空元素，
    /// 该元素的 join 结果已经包含结尾换行，因此只有"行集末尾非空且原文以换行结尾"时才补一个换行。
    /// 否则每次合并都会多追加一个空行，既让幂等比较永远判定为有变化，也会让 patch 文件无界增长。
    /// </summary>
    private static string Compose(List<string> lines, string newline, bool preserveTrailingNewline)
        => string.Join(newline, lines)
            + (preserveTrailingNewline && lines.Count > 0 && lines[^1].Length != 0 ? newline : string.Empty);
}
