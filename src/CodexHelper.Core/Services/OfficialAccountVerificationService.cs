using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

/// <summary>
/// Reads the usage data exposed to an already authenticated Codex account.
/// It never writes tokens to disk or logs response bodies.
/// </summary>
public sealed class OfficialAccountVerificationService
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    private readonly OfficialAccountService accounts;
    private readonly Func<HttpClient> createClient;

    public OfficialAccountVerificationService(OfficialAccountService accounts, Func<HttpClient>? createClient = null)
    {
        this.accounts = accounts;
        this.createClient = createClient ?? (() => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
    }

    public async Task<OfficialAccountUsage> VerifyAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var auth = accounts.ExportDecryptedProfileForBundle(profileId);
        try
        {
            var (accessToken, accountId) = ReadCredentials(auth);
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("该账号文件缺少访问令牌，请在 Codex 中重新登录后再保存账号。");
            using var client = createClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (!string.IsNullOrWhiteSpace(accountId)) request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
            request.Headers.TryAddWithoutValidation("openai-beta", "codex-1");
            request.Headers.TryAddWithoutValidation("originator", "Codex Desktop");
            request.Headers.TryAddWithoutValidation("oai-language", "zh-CN");
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            try
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    accounts.UpdateVerification(profileId, false, "登录已失效或官方拒绝验证；请在 Codex 中重新登录。");
                    accounts.RecordHealthHistory(profileId, false, "登录已失效或官方拒绝验证");
                    throw new InvalidOperationException("账号未通过官方验证（HTTP " + (int)response.StatusCode + "）。请重新登录后再试。");
                }
                if (!response.IsSuccessStatusCode)
                {
                    accounts.UpdateVerification(profileId, false, "额度服务暂时不可用（HTTP " + (int)response.StatusCode + "）。");
                    accounts.RecordHealthHistory(profileId, false, "额度服务暂时不可用（HTTP " + (int)response.StatusCode + "）");
                    throw new InvalidOperationException("无法读取账号额度（HTTP " + (int)response.StatusCode + "）。");
                }
                var usage = ParseUsage(body);
                accounts.UpdateVerification(profileId, true, "官方账号可用", usage);
                accounts.RecordHealthHistory(profileId, true, usage.Summary, usage);
                return usage;
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
        catch (HttpRequestException ex)
        {
            accounts.UpdateVerification(profileId, false, "无法连接官方额度服务，请检查网络后重试。");
            accounts.RecordHealthHistory(profileId, false, "无法连接官方额度服务");
            throw new InvalidOperationException("无法连接官方额度服务。", ex);
        }
        finally { CryptographicOperations.ZeroMemory(auth); }
    }

    public static OfficialAccountUsage ParseUsage(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var rateLimit = root.TryGetProperty("rate_limit", out var value) ? value : default;
        var primary = rateLimit.ValueKind == JsonValueKind.Object && rateLimit.TryGetProperty("primary_window", out value) ? value : default;
        var secondary = rateLimit.ValueKind == JsonValueKind.Object && rateLimit.TryGetProperty("secondary_window", out value) ? value : default;
        var primaryUsed = ReadDecimal(primary, "used_percent");
        var secondaryUsed = ReadDecimal(secondary, "used_percent");
        var primaryReset = ReadReset(primary);
        var secondaryReset = ReadReset(secondary);
        var plan = root.TryGetProperty("plan_type", out var planValue) && planValue.ValueKind == JsonValueKind.String ? planValue.GetString() ?? string.Empty : string.Empty;
        var parts = new List<string>();
        if (primaryUsed is not null) parts.Add("短周期已用 " + primaryUsed.Value.ToString("0.#") + "%");
        if (secondaryUsed is not null) parts.Add("长周期已用 " + secondaryUsed.Value.ToString("0.#") + "%");
        return new OfficialAccountUsage(parts.Count == 0 ? "官方已验证，未返回可展示额度" : string.Join("；", parts), plan, primaryUsed, secondaryUsed, primaryReset, secondaryReset);
    }

    private static (string AccessToken, string AccountId) ReadCredentials(ReadOnlySpan<byte> auth)
    {
        using var document = JsonDocument.Parse(auth.ToArray());
        var root = document.RootElement;
        var tokens = root.TryGetProperty("tokens", out var value) && value.ValueKind == JsonValueKind.Object ? value : root;
        var access = ReadString(tokens, "access_token");
        var account = ReadString(root, "account_id");
        if (string.IsNullOrWhiteSpace(account)) account = ReadString(tokens, "account_id");
        return (access, account);
    }

    private static decimal? ReadDecimal(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.TryGetDecimal(out var number) ? number : null;

    private static DateTime? ReadReset(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (value.TryGetProperty("reset_at", out var raw) && raw.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(raw.GetString(), out var parsed)) return parsed.UtcDateTime;
        return value.TryGetProperty("reset_after_seconds", out raw) && raw.TryGetDouble(out var seconds) ? DateTime.UtcNow.AddSeconds(seconds) : null;
    }

    private static string ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? string.Empty : string.Empty;
}
