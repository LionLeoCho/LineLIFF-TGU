using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace LiffChat.Api.Auth;

// 把客人端送來的 LIFF access token 在伺服器端驗證並解出 LINE userId。
// 流程：① verify（確認 token 屬於我們的 channel、未過期）→ ② /v2/profile 取 userId。
// 註：規格 §J 待定「access token vs ID token」。此處走 access token；
//     若改用 ID token，改打 POST oauth2/v2.1/verify 帶 id_token，取回傳 sub 即 userId。
public class LiffTokenVerifier(HttpClient http, IMemoryCache cache, IConfiguration cfg)
{
    private readonly string _channelId = cfg["Liff:ChannelId"]
        ?? throw new InvalidOperationException("缺少設定 Liff:ChannelId（LINE Login channel ID）");

    public async Task<string?> VerifyAndGetUserIdAsync(string accessToken, CancellationToken ct = default)
    {
        // 短期快取：同一 token 重複請求不必每次打 LINE
        if (cache.TryGetValue(CacheKey(accessToken), out string? cachedUserId))
            return cachedUserId;

        // ① 驗 token 有效性 + 歸屬 channel
        var verify = await http.GetFromJsonAsync<VerifyResult>(
            $"https://api.line.me/oauth2/v2.1/verify?access_token={Uri.EscapeDataString(accessToken)}", ct);
        if (verify is null || verify.ClientId != _channelId || verify.ExpiresIn <= 0)
            return null;

        // ② 取 profile → userId
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.line.me/v2/profile");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;

        var profile = await res.Content.ReadFromJsonAsync<LineProfile>(cancellationToken: ct);
        var userId = profile?.UserId;
        if (userId is null) return null;

        // 快取 token→userId 一小段時間（小於 token 剩餘壽命）
        cache.Set(CacheKey(accessToken), userId,
            TimeSpan.FromMinutes(Math.Min(5, Math.Max(1, verify.ExpiresIn / 60))));
        return userId;
    }

    private static string CacheKey(string token) => "liff:" + token.GetHashCode();

    private sealed class VerifyResult
    {
        [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    }

    private sealed class LineProfile
    {
        [JsonPropertyName("userId")] public string UserId { get; set; } = "";
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
        [JsonPropertyName("pictureUrl")] public string? PictureUrl { get; set; }
    }
}
