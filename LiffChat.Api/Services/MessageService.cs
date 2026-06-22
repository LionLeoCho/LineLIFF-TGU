using System.Text.Json;
using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Services;

public class MessageService(
    AppDbContext db,
    IFirestoreMirror mirror,
    IPushService push)
{
    public enum SendError { None, NotMember, Forbidden, Readonly }
    public sealed record Result(SendError Error, Message? Message);

    // ★核心寫入：驗權 → 寫 SQL（權威）→ 寫 Firestore（鏡像）→ 觸發推播。
    // 訊息一律經此；Firestore 前端不可直寫（rules 最後防線）。
    public async Task<Result> SendAsync(Guid roomId, string lineUserId,
        SendMessageRequest req, CancellationToken ct = default)
    {
        var room = await db.Rooms.FindAsync([roomId], ct);
        if (room is null) return new Result(SendError.NotMember, null);

        // 解析發話 participant（本團 + 此 userId + 有效）
        var sender = await db.Participants.FirstOrDefaultAsync(
            p => p.TourId == room.TourId && p.LineUserId == lineUserId && p.Status == 0, ct);
        if (sender is null) return new Result(SendError.Forbidden, null);

        // 必須是該 room 的有效成員
        var member = await db.RoomMembers.FindAsync([roomId, sender.ParticipantId], ct);
        if (member is null || !member.IsActive) return new Result(SendError.NotMember, null);

        // 唯讀彙整判斷（§I-4）：團結束 / 團員互聊關閉 / 對方退團 / 對方停用導領
        if (await IsRoomReadonlyAsync(room, sender, ct))
            return new Result(SendError.Readonly, null);

        var now = DateTime.UtcNow;
        var msg = new Message
        {
            MessageId = Guid.NewGuid(),
            RoomId = roomId,
            TourId = room.TourId,
            SenderParticipantId = sender.ParticipantId,
            SenderName = sender.DisplayName,       // 冗余快照
            SenderAvatarUrl = sender.AvatarUrl,
            Type = req.Type,
            Content = req.Content,
            IsAnnouncement = false,                // 公告走導領專屬端點
            MentionParticipantIds = (req.MentionParticipantIds is { Count: > 0 })
                ? JsonSerializer.Serialize(req.MentionParticipantIds) : null,
            CreatedAtUtc = now,
        };

        // ① SQL 權威保存
        db.Messages.Add(msg);
        await db.SaveChangesAsync(ct);

        // ② Firestore 鏡像（前端訂閱即時顯示）
        await mirror.WriteMessageAsync(msg, ct);

        // ③ 推播（離線才推 + 聚合；分級規則在 PushService 內）
        await push.OnNewMessageAsync(msg, ct);

        return new Result(SendError.None, msg);
    }

    private async Task<bool> IsRoomReadonlyAsync(Room room, Participant me, CancellationToken ct)
    {
        var tour = await db.Tours.FindAsync([room.TourId], ct);
        if (tour is null) return true;
        if (tour.Status != 0) return true;                       // 團已結束/取消

        if (room.Type == 1) // direct
        {
            // 團員↔團員私訊：團層級開關關閉 → 凍結
            var others = await db.RoomMembers
                .Where(m => m.RoomId == room.RoomId && m.ParticipantId != me.ParticipantId)
                .Select(m => m.ParticipantId).ToListAsync(ct);
            var counterpart = await db.Participants
                .FirstOrDefaultAsync(p => others.Contains(p.ParticipantId), ct);

            if (counterpart is not null)
            {
                if (counterpart.Status != 0) return true;        // 對方退團/停用導領 → 唯讀
                if (counterpart.Kind == 0 && !tour.GroupChatEnabled) return true; // 團員互聊關閉凍結
            }
        }
        return false;
    }
}
