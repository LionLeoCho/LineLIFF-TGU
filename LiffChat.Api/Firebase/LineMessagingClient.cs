using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace LiffChat.Api.Firebase;

// LINE Messaging API push 客戶端。
// 設定 Line:ChannelAccessToken（OA 的 Messaging API channel，long-lived token）。
// 留空 → Enabled=false，PushService 會維持 log 模擬，不真的呼叫。
public class LineMessagingClient(HttpClient http, IConfiguration cfg, ILogger<LineMessagingClient> logger)
{
    private readonly string? _token = cfg["Line:ChannelAccessToken"];
    public bool Enabled => !string.IsNullOrWhiteSpace(_token);

    // 對單一 userId 推一則文字（首版純文字，內含預覽 + 深連結）。
    public async Task<bool> PushTextAsync(string userId, string text, CancellationToken ct = default)
    {
        if (!Enabled) return false;

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Content = JsonContent.Create(new
        {
            to = userId,
            messages = new[] { new { type = "text", text } },
        });

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            // 常見：使用者沒加 OA 好友(403)、userId 無效(400)、token 失效(401)
            var body = await res.Content.ReadAsStringAsync(ct);
            logger.LogWarning("LINE push 失敗 status={Status} user={User} body={Body}",
                (int)res.StatusCode, userId, body);
            return false;
        }
        return true;
    }
}
