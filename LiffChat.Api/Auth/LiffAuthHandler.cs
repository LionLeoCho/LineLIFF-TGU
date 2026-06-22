using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LiffChat.Api.Auth;

// 客人端認證 scheme：Authorization: Bearer {LIFF access token}
// 驗過後把 LINE userId 放進 ClaimTypes.NameIdentifier，端點用 User 取得身分。
// 導領端是另一個 scheme（導領系統 session/JWT），不在此骨架範圍。
public class LiffAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LiffTokenVerifier verifier)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Liff";
    public const string AccessTokenClaim = "liff_access_token";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return AuthenticateResult.NoResult();

        var raw = header.ToString();
        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Authorization header 需為 Bearer");

        var token = raw["Bearer ".Length..].Trim();
        var userId = await verifier.VerifyAndGetUserIdAsync(token, Context.RequestAborted);
        if (userId is null)
            return AuthenticateResult.Fail("LIFF token 無效或過期");

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),  // = LINE userId
                new Claim(AccessTokenClaim, token),
            },
            Scheme);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string LineUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException("缺少 LINE userId claim");
}
