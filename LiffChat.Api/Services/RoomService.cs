using LiffChat.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Services;

public class RoomService(AppDbContext db)
{
    // 我所屬（有效成員）的 room 清單 + 每室未讀數。
    // 未讀 = 該室中 CreatedAt > 我的 LastReadAt 且非我自己發的訊息數。
    // 排序：整團群聊置頂，其餘依最後訊息時間新到舊。
    public async Task<List<RoomListItem>> GetMyRoomsAsync(
        string tourId, string lineUserId, CancellationToken ct = default)
    {
        var me = await db.Participants.FirstOrDefaultAsync(
            p => p.TourId == tourId && p.LineUserId == lineUserId && p.Status == 0, ct);
        if (me is null) return new();   // 未綁定 → 空清單

        var myMemberships = await db.RoomMembers
            .Where(m => m.ParticipantId == me.ParticipantId && m.IsActive)
            .ToListAsync(ct);
        var roomIds = myMemberships.Select(m => m.RoomId).ToList();

        var rooms = await db.Rooms.Where(r => roomIds.Contains(r.RoomId)).ToListAsync(ct);
        var tour = await db.Tours.FindAsync([tourId], ct);

        var items = new List<RoomListItem>();
        foreach (var room in rooms)
        {
            var myMember = myMemberships.First(m => m.RoomId == room.RoomId);
            var lastRead = myMember.LastReadAtUtc ?? DateTime.MinValue;

            var last = await db.Messages
                .Where(x => x.RoomId == room.RoomId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);

            var unread = await db.Messages.CountAsync(
                x => x.RoomId == room.RoomId
                  && x.CreatedAtUtc > lastRead
                  && x.SenderParticipantId != me.ParticipantId, ct);

            string title;
            byte counterpartKind = 0;
            Guid? counterpartId = null;
            string? avatar = null;

            if (room.Type == 0)
            {
                title = tour?.TourName ?? "整團群聊";
            }
            else
            {
                // direct：找另一位成員當標題/對象
                var other = await db.RoomMembers
                    .Where(m => m.RoomId == room.RoomId && m.ParticipantId != me.ParticipantId)
                    .Join(db.Participants, m => m.ParticipantId, p => p.ParticipantId, (m, p) => p)
                    .FirstOrDefaultAsync(ct);
                title = other?.DisplayName ?? "(對方)";
                counterpartKind = other?.Kind ?? 0;
                counterpartId = other?.ParticipantId;
                avatar = other?.AvatarUrl;
            }

            items.Add(new RoomListItem(
                room.RoomId, room.Type, title,
                counterpartId, counterpartKind, avatar,
                last is null ? null : new LastMessageDto(Preview(last), last.SenderName, last.CreatedAtUtc),
                unread));
        }

        return items
            .OrderByDescending(i => i.Type == 0)                              // 群聊置頂
            .ThenByDescending(i => i.LastMessage?.CreatedAtUtc ?? DateTime.MinValue)
            .ToList();
    }

    // 更新已讀位置（進房/讀取時呼叫）→ 未讀數歸零的依據。
    // 註：規格 §A-3 已讀位置主要由前端直寫 Firestore；此處為 SQL 端權威回寫，供 GET /rooms 計算未讀。
    public async Task<bool> MarkReadAsync(Guid roomId, string lineUserId, CancellationToken ct = default)
    {
        var room = await db.Rooms.FindAsync([roomId], ct);
        if (room is null) return false;

        var me = await db.Participants.FirstOrDefaultAsync(
            p => p.TourId == room.TourId && p.LineUserId == lineUserId && p.Status == 0, ct);
        if (me is null) return false;

        var member = await db.RoomMembers.FindAsync([roomId, me.ParticipantId], ct);
        if (member is null) return false;

        member.LastReadAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Preview(Message m) => m.Type switch
    {
        1 => "[圖片]",
        2 => "[位置]",
        9 => "[系統訊息]",
        _ => m.Content.Length > 30 ? m.Content[..30] + "…" : m.Content,
    };
}
