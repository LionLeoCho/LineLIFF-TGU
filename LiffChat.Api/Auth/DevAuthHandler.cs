using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LiffChat.Api.Auth;

// 只在 Development 環境掛載（見 Program.cs 環境分流）。
// 用途：內網測試不需要真 LINE token，改用 header 假裝某個 userId，
//       即可用 Postman/curl 打 /bind /me /messages 把後端整條跑通。
// 正式環境不註冊此 scheme，所以不會有後門。
//
// 用法：請求帶  X-Debug-UserId: Utest001
//       未帶則套用預設測試 userId（方便快速呼叫）。
public class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Dev";
    public const string HeaderName = "X-Debug-UserId";
    private const string DefaultUserId = "Udev_default";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers.TryGetValue(HeaderName, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : DefaultUserId;

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, Scheme);

        Logger.LogWarning("[DevAuth] 假登入 userId={UserId}（僅限開發環境）", userId);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
