using LiffChat.Api.Auth;
using LiffChat.Api.Services;
using Microsoft.AspNetCore.Authorization;

namespace LiffChat.Api.Endpoints;

public static class LeaderEndpoints
{
    public static void MapLeaderEndpoints(this IEndpointRouteBuilder app)
    {
        // 導領端統一要求 Leader scheme（與客人 LIFF 分開的入口）
        var g = app.MapGroup("/api")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = LeaderAuth.Scheme });

        // C-4：發公告（強制推播 + 可置頂）
        g.MapPost("/tours/{tourId}/announcements", async (
            string tourId, AnnouncementRequest body, HttpContext http, LeaderService leader, CancellationToken ct) =>
        {
            var who = http.User.LeaderAccountId();
            var r = await leader.PostAnnouncementAsync(tourId, who, body.Content, body.Pin, ct);
            return r.Error switch
            {
                LeaderService.LeaderError.None => Results.Ok(new AnnouncementResponse(r.MessageId, r.Pinned)),
                LeaderService.LeaderError.NotLeader => Results.Forbid(),
                LeaderService.LeaderError.NoGroupRoom => Results.NotFound(new { error = "NO_GROUP_ROOM" }),
                LeaderService.LeaderError.TooManyPins => Results.Conflict(new { error = "TOO_MANY_PINS", max = 3 }),
                _ => Results.Problem(),
            };
        });

        // C-4：撤下置頂
        g.MapDelete("/tours/{tourId}/announcements/{messageId:guid}/pin", async (
            string tourId, Guid messageId, HttpContext http, LeaderService leader, CancellationToken ct) =>
        {
            var who = http.User.LeaderAccountId();
            var ok = await leader.UnpinAsync(tourId, who, messageId, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // C-4：團員互聊團層級開關
        g.MapPatch("/tours/{tourId}/settings", async (
            string tourId, GroupChatToggleRequest body, HttpContext http, LeaderService leader, CancellationToken ct) =>
        {
            var who = http.User.LeaderAccountId();
            var ok = await leader.SetGroupChatEnabledAsync(tourId, who, body.GroupChatEnabled, ct);
            return ok ? Results.Ok(new { groupChatEnabled = body.GroupChatEnabled }) : Results.Forbid();
        });

        // C-4：手動改結束日（自動結束兜底）
        g.MapPatch("/tours/{tourId}/schedule", async (
            string tourId, ScheduleRequest body, HttpContext http, LeaderService leader, CancellationToken ct) =>
        {
            var who = http.User.LeaderAccountId();
            var ok = await leader.SetScheduleAsync(tourId, who, body.EndAtUtc, ct);
            return ok ? Results.Ok(new { endAtUtc = body.EndAtUtc }) : Results.Forbid();
        });

        // C-4：未綁定名單（催登入用）
        g.MapGet("/tours/{tourId}/unbound", async (
            string tourId, LeaderService leader, CancellationToken ct) =>
            Results.Ok(await leader.GetUnboundAsync(tourId, ct)));
    }

    // §F-2 導領指派 ingest（系統對系統，與名單 ingest 同類；用共享密鑰）
    public static void MapLeaderIngestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/tour-leaders", async (
            LeaderIngestRequest body, HttpContext http, LeaderIngestService ingest, IConfiguration cfg, CancellationToken ct) =>
        {
            var expected = cfg["Ingest:SharedKey"];
            if (!string.IsNullOrEmpty(expected) && http.Request.Headers[IngestEndpoints.KeyHeader].ToString() != expected)
                return Results.Unauthorized();

            var n = await ingest.IngestAsync(body, ct);
            return Results.Ok(new { affected = n });
        });
    }
}
