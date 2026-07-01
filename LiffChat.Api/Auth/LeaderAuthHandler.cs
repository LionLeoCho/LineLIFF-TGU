using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LiffChat.Api.Auth;

// 導領端走「不同入口」：獨立的認證 scheme（與客人 LIFF 分開）。
// 身分主體是 leaderAccountId（導領系統帳號），放進 ClaimTypes.NameIdentifier。
public static class LeaderAuth
{
    public const string Scheme = "Leader";
    public const string AccountClaim = ClaimTypes.NameIdentifier;
}

public static class LeaderClaims
{
    public static string LeaderAccountId(this ClaimsPrincipal user) =>
        user.FindFirstValue(LeaderAuth.AccountClaim)
        ?? throw new UnauthorizedAccessException("缺少 leaderAccountId claim");
}

// Development：假登入。header X-Debug-LeaderId（預設 L-demo，對應 seed 建的導領）。
public class DevLeaderAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string HeaderName = "X-Debug-LeaderId";
    private const string DefaultLeader = "L-demo";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var leaderId = Request.Headers.TryGetValue(HeaderName, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString() : DefaultLeader;

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(LeaderAuth.AccountClaim, leaderId),
            new Claim(ClaimTypes.Role, "leader"),
        }, LeaderAuth.Scheme);

        Logger.LogWarning("[DevLeaderAuth] 假導領登入 leaderAccountId={Id}（僅開發環境）", leaderId);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), LeaderAuth.Scheme)));
    }
}

// 正式：導領系統 JWT/session 驗證（骨架 stub）。
// TODO：依導領系統實際簽發方式驗證 Bearer token，解出 leaderAccountId。
public class LeaderTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // TODO: 取 Authorization: Bearer，向導領系統驗證 / 驗 JWT 簽章，取 leaderAccountId。
        return Task.FromResult(AuthenticateResult.Fail(
            "尚未接導領系統 JWT 驗證（LeaderTokenAuthHandler 待實作）"));
    }
}

// 導領走 LIFF（做法三）：Bearer = LIFF token → 驗出 LINE userId →
// 查哪個導領 participant 綁了這個 userId（leader-login 時記上的）→ 認證為該導領。
// 前提：導領已先打過 /leader-login 把 userId 綁到其 leader participant。
public class LiffLeaderAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LiffTokenVerifier verifier)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
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

        // userId → 導領 participant（Kind==1 且 LineUserId==userId）
        var db = Context.RequestServices.GetRequiredService<Data.AppDbContext>();
        var leader = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.Participants,
                p => p.Kind == 1 && p.LineUserId == userId && p.Status == 0, Context.RequestAborted);
        if (leader is null)
            return AuthenticateResult.Fail("此 LINE 帳號尚未綁定為導領（請先 leader-login）");

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(LeaderAuth.AccountClaim, leader.LeaderAccountId!),
            new Claim(ClaimTypes.Role, "leader"),
        }, LeaderAuth.Scheme);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), LeaderAuth.Scheme));
    }
}
