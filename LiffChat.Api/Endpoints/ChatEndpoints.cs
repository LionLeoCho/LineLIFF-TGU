using LiffChat.Api.Auth;
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

            var token = await firebase.CreateForParticipantAsync(result.Participant.ParticipantId, ct);
            return Results.Ok(new BindResponse(
                result.Participant.ParticipantId, result.Participant.DisplayName,
                result.Participant.AcceptMemberDm, token));
        });

        // ---- C-1：查自己（順帶簽 custom token，§I-1）----
        g.MapGet("/tours/{tourId}/me", async (
            string tourId,
            HttpContext http,
            BindingService binding,
            FirebaseTokenService firebase,
            CancellationToken ct) =>
        {
            var userId = http.User.LineUserId();
            var participant = await binding.FindBoundAsync(tourId, userId, ct);

            if (participant is null)
                return Results.Ok(new MeResponse(false, null, null, null, null));

            var token = await firebase.CreateForParticipantAsync(participant.ParticipantId, ct);
            return Results.Ok(new MeResponse(
                true, participant.ParticipantId, participant.DisplayName,
                participant.AcceptMemberDm, token));
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
            var v = await members.UpdateAcceptMemberDmAsync(tourId, userId, body.AcceptMemberDm, ct);
            return v is null
                ? Results.NotFound(new { error = "NOT_BOUND" })
                : Results.Ok(new SettingsResponse(v.Value));
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
