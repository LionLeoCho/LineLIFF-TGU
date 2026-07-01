using LiffChat.Api.Auth;
using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using LiffChat.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LiffChat.Api.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // 全部要求 Liff scheme 認證（已驗 token → User 帶 LINE userId）
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- C-1：綁定 ----
        g.MapPost("/tours/{tourId}/bind", async (
            string tourId,
            BindRequest body,
            HttpContext http,
            BindingService binding,
            FirebaseTokenService firebase,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var result = await binding.ExecuteBindingAsync(tourId, userId, body.FullName, body.BirthDate, ct);

            if (!result.Found || result.Participant is null)
                return Results.NotFound(new
                {
                    error = "ROSTER_NO_MATCH",
                    message = "名單中找不到符合的資料，請確認姓名與生日，或聯絡導領協助。",
                    contactLeader = true,   // 前端顯示「聯絡導領」出口
                });

            var tour = await db.Tours.FindAsync([tourId], ct);
            var token = await firebase.CreateForParticipantAsync(result.Participant.ParticipantId, ct);
            return Results.Ok(new BindResponse(
                result.Participant.ParticipantId, result.Participant.DisplayName,
                result.Participant.AcceptMemberDm, tour?.GroupChatEnabled ?? true,
                result.Participant.PushEnabled, token));
        });

        // ---- C-1：查自己（順帶簽 custom token，§I-1）----
        g.MapGet("/tours/{tourId}/me", async (
            string tourId,
            HttpContext http,
            BindingService binding,
            FirebaseTokenService firebase,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var participant = await binding.FindBoundAsync(tourId, userId, ct);

            if (participant is null)
                return Results.Ok(new MeResponse(false, null, null, null, null, null, null));

            var tour = await db.Tours.FindAsync([tourId], ct);
            var token = await firebase.CreateForParticipantAsync(participant.ParticipantId, ct);
            return Results.Ok(new MeResponse(
                true, participant.ParticipantId, participant.DisplayName,
                participant.AcceptMemberDm, tour?.GroupChatEnabled ?? true,
                participant.PushEnabled, token));
        });

        // ---- C-2：我的聊天室列表 + 未讀數 ----
        g.MapGet("/tours/{tourId}/rooms", async (
            string tourId,
            HttpContext http,
            RoomService rooms,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            return Results.Ok(await rooms.GetMyRoomsAsync(tourId, userId, ct));
        });

        // ---- C-2：更新已讀位置（未讀歸零）----
        g.MapPost("/rooms/{roomId:guid}/read", async (
            Guid roomId,
            HttpContext http,
            RoomService rooms,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var ok = await rooms.MarkReadAsync(roomId, userId, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // ---- C-5：圖片上傳（前端壓縮後上傳 → 寫實體目錄 → 回 CDN URL）----
        // 前端用 multipart/form-data 傳壓縮後的圖；後端寫到 Storage:LocalPath\{tourId}\{guid}.jpg
        g.MapPost("/rooms/{roomId:guid}/images", async (
            Guid roomId,
            HttpRequest req,
            HttpContext http,
            AppDbContext db,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "EXPECT_MULTIPART" });
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "NO_FILE" });
            if (file.Length > 8 * 1024 * 1024) return Results.BadRequest(new { error = "TOO_LARGE" }); // 壓縮後仍 >8MB 擋掉

            // 找出此 room 屬於哪團（決定子目錄）
            var room = await db.Rooms.FindAsync([roomId], ct);
            if (room is null) return Results.NotFound(new { error = "ROOM_NOT_FOUND" });

            var localBase = cfg["Storage:LocalPath"];
            var urlBase = cfg["Storage:PublicUrlBase"];
            if (string.IsNullOrWhiteSpace(localBase) || string.IsNullOrWhiteSpace(urlBase))
                return Results.Problem("Storage 未設定（Storage:LocalPath / PublicUrlBase）");

            var fileName = $"{Guid.NewGuid():N}.jpg";
            var dir = Path.Combine(localBase, room.TourId);
            Directory.CreateDirectory(dir);
            var fullPath = Path.Combine(dir, fileName);

            await using (var fs = File.Create(fullPath))
                await file.CopyToAsync(fs, ct);

            // 組對外 URL：PublicUrlBase/{tourId}/{fileName}
            var url = $"{urlBase.TrimEnd('/')}/{room.TourId}/{fileName}";
            return Results.Ok(new { url });
        }).DisableAntiforgery();

        // ---- 導領 LIFF 登入（做法三）：用 LINE userId 比對團導領 → 綁定 → 回身分 + Firebase token ----
        // 認證走預設（LINE 使用者）scheme：只需證明是某 LINE 帳號，再比對 leaderAccountId。
        g.MapPost("/tours/{tourId}/leader-login", async (
            string tourId,
            LeaderLoginRequest body,
            HttpContext http,
            LeaderService leaders,
            FirebaseTokenService firebase,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var leader = await leaders.LeaderLoginAsync(tourId, body.LeaderAccountId, userId, ct);
            if (leader is null)
                return Results.NotFound(new { error = "NOT_A_LEADER", message = "此團查無該導領帳號" });

            var token = await firebase.CreateForParticipantAsync(leader.ParticipantId, ct);
            return Results.Ok(new LeaderLoginResponse(leader.ParticipantId, leader.DisplayName, token));
        });

        // ---- C-1：解除綁定（登出）----
        g.MapPost("/tours/{tourId}/me/unbind", async (
            string tourId,
            HttpContext http,
            BindingService binding,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var ok = await binding.UnbindAsync(tourId, userId, ct);
            return Results.Ok(new { unbound = ok });
        });

        // ---- C-2：反查地址（位置訊息用，key 留後端）----
        g.MapPost("/geocode", async (
            GeocodeRequest body,
            GeocodingService geo,
            CancellationToken ct) =>
        {
            var address = await geo.ReverseAsync(body.Lat, body.Lng, ct);
            return Results.Ok(new { address });
        });

        // ---- C-2：列同團團員（找團員私訊用）----
        g.MapGet("/tours/{tourId}/members", async (
            string tourId,
            HttpContext http,
            MemberService members,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var list = await members.ListMembersAsync(tourId, userId, ct);
            return list is null
                ? Results.NotFound(new { error = "NOT_BOUND" })
                : Results.Ok(list);
        });

        // ---- C-2：發起一對一（三條件驗證）----
        g.MapPost("/tours/{tourId}/direct", async (
            string tourId,
            OpenDirectRequest body,
            HttpContext http,
            MemberService members,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var r = await members.OpenDirectAsync(tourId, userId, body.TargetParticipantId, ct);

            return r.Error switch
            {
                MemberService.DirectError.None => Results.Ok(new OpenDirectResponse(r.RoomId, r.Created)),
                MemberService.DirectError.NotBound => Results.NotFound(new { error = "NOT_BOUND" }),
                MemberService.DirectError.TargetNotFound => Results.NotFound(new { error = "TARGET_NOT_FOUND" }),
                MemberService.DirectError.SelfDm => Results.BadRequest(new { error = "SELF_DM" }),
                MemberService.DirectError.GroupChatDisabled => Results.Json(new { error = "GROUP_CHAT_DISABLED" }, statusCode: 403),
                MemberService.DirectError.TargetRejects => Results.Json(new { error = "TARGET_REJECTS_DM" }, statusCode: 403),
                _ => Results.Forbid(),
            };
        });

        // ---- C-3：個人私訊開關 ----
        g.MapPatch("/tours/{tourId}/me/settings", async (
            string tourId,
            UpdateSettingsRequest body,
            HttpContext http,
            MemberService members,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var v = await members.UpdateSettingsAsync(tourId, userId, body.AcceptMemberDm, body.PushEnabled, ct);
            return v is null
                ? Results.NotFound(new { error = "NOT_BOUND" })
                : Results.Ok(new SettingsResponse(v.Value.AcceptMemberDm, v.Value.PushEnabled));
        });

        // ---- C-2：發訊息（★核心）----
        g.MapPost("/rooms/{roomId:guid}/messages", async (
            Guid roomId,
            SendMessageRequest body,
            HttpContext http,
            MessageService messages,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var result = await messages.SendAsync(roomId, userId, body, ct);

            return result.Error switch
            {
                MessageService.SendError.None =>
                    Results.Ok(new SendMessageResponse(result.Message!.MessageId, result.Message.CreatedAtUtc)),
                MessageService.SendError.NotMember => Results.NotFound(new { error = "NOT_A_MEMBER" }),
                MessageService.SendError.Readonly => Results.Conflict(new { error = "ROOM_READONLY" }),
                _ => Results.Forbid(),
            };
        });
    }
}
