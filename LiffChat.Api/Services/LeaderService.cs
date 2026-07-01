using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Services;

public class LeaderService(AppDbContext db, IFirestoreMirror mirror, IPushService push)
{
    public enum LeaderError { None, NotLeader, NoGroupRoom, TooManyPins, NotFound }
    public sealed record AnnounceResult(LeaderError Error, Guid MessageId = default, bool Pinned = false);

    private const int MaxPins = 3;

    // 發公告：群聊發一則 IsAnnouncement=true（強制推播），可選置頂（≤3）。
    public async Task<AnnounceResult> PostAnnouncementAsync(
        string tourId, string leaderAccountId, string content, bool pin, CancellationToken ct = default)
    {
        var leader = await ResolveLeaderAsync(tourId, leaderAccountId, ct);
        if (leader is null) return new(LeaderError.NotLeader);

        var groupRoom = await db.Rooms.FirstOrDefaultAsync(r => r.TourId == tourId && r.Type == 0, ct);
        if (groupRoom is null) return new(LeaderError.NoGroupRoom);

        if (pin)
        {
            var activePins = await db.Announcements.CountAsync(a => a.RoomId == groupRoom.RoomId && a.IsActive, ct);
            if (activePins >= MaxPins) return new(LeaderError.TooManyPins);
        }

        var now = DateTime.UtcNow;
        var msg = new Message
        {
            MessageId = Guid.NewGuid(),
            RoomId = groupRoom.RoomId,
            TourId = tourId,
            SenderParticipantId = leader.ParticipantId,
            SenderName = leader.DisplayName,
            SenderAvatarUrl = leader.AvatarUrl,
            Type = 0,
            Content = content,
            IsAnnouncement = true,           // 強制推播的依據
            CreatedAtUtc = now,
        };
        db.Messages.Add(msg);

        if (pin)
            db.Announcements.Add(new Announcement
            {
                MessageId = msg.MessageId, RoomId = groupRoom.RoomId, PinnedAtUtc = now, IsActive = true,
            });

        await db.SaveChangesAsync(ct);

        await mirror.WriteMessageAsync(msg, ct);          // 鏡像（即時顯示）
        if (pin) await SyncPinnedAsync(groupRoom.RoomId, ct);
        await push.OnNewMessageAsync(msg, ct);            // 公告一定推

        return new(LeaderError.None, msg.MessageId, pin);
    }

    // 撤下置頂
    public async Task<bool> UnpinAsync(string tourId, string leaderAccountId, Guid messageId, CancellationToken ct = default)
    {
        var leader = await ResolveLeaderAsync(tourId, leaderAccountId, ct);
        if (leader is null) return false;

        var ann = await db.Announcements.FirstOrDefaultAsync(a => a.MessageId == messageId && a.IsActive, ct);
        if (ann is null) return false;

        ann.IsActive = false;
        await db.SaveChangesAsync(ct);
        await SyncPinnedAsync(ann.RoomId, ct);
        return true;
    }

    // 團層級開關（開/關團員互聊）。關閉時既有團員私訊一起凍結（靠讀取時 IsRoomReadonlyAsync 判斷，重開即解凍）。
    public async Task<bool> SetGroupChatEnabledAsync(string tourId, string leaderAccountId, bool enabled, CancellationToken ct = default)
    {
        var leader = await ResolveLeaderAsync(tourId, leaderAccountId, ct);
        if (leader is null) return false;

        var tour = await db.Tours.FindAsync([tourId], ct);
        if (tour is null) return false;

        tour.GroupChatEnabled = enabled;
        tour.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // 手動改結束日（自動結束的兜底）。連動更新 PurgeAt。
    public async Task<bool> SetScheduleAsync(string tourId, string leaderAccountId, DateTime endAtUtc, CancellationToken ct = default)
    {
        var leader = await ResolveLeaderAsync(tourId, leaderAccountId, ct);
        if (leader is null) return false;

        var tour = await db.Tours.FindAsync([tourId], ct);
        if (tour is null) return false;

        tour.EndAtUtc = endAtUtc;
        tour.PurgeAtUtc = endAtUtc.AddDays(30);
        tour.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // 未綁定名單（導領催登入用）：在團、且尚無任何綁定的乘客。
    public async Task<List<UnboundPassenger>> GetUnboundAsync(string tourId, CancellationToken ct = default)
    {
        return await db.Passengers
            .Where(p => p.TourId == tourId && p.Status == 0)
            .Where(p => !db.ParticipantPassengers.Any(b => b.PassengerId == p.PassengerId))
            .Select(p => new UnboundPassenger(p.PassengerId, p.FullName, p.BirthDate))
            .ToListAsync(ct);
    }

    private Task<Participant?> ResolveLeaderAsync(string tourId, string leaderAccountId, CancellationToken ct) =>
        db.Participants.FirstOrDefaultAsync(
            p => p.TourId == tourId && p.Kind == 1 && p.LeaderAccountId == leaderAccountId && p.Status == 0, ct);

    // 導領 LIFF 登入（做法三）：比對這團有此導領 → 把 LINE userId 記到該導領 participant。
    // 回該 participant（給前端拿 participantId/displayName + 後端簽 Firebase token）；查無回 null。
    public async Task<Participant?> LeaderLoginAsync(
        string tourId, string leaderAccountId, string lineUserId, CancellationToken ct = default)
    {
        var leader = await ResolveLeaderAsync(tourId, leaderAccountId, ct);
        if (leader is null) return null;

        if (leader.LineUserId != lineUserId)
        {
            // 同團一個 userId 只能對一個 participant（唯一索引）。
            // 若此 userId 已綁在別的 participant（例如曾以客人身分綁定），先解除，再綁到導領。
            var others = await db.Participants
                .Where(p => p.TourId == tourId && p.LineUserId == lineUserId
                            && p.ParticipantId != leader.ParticipantId)
                .ToListAsync(ct);
            foreach (var o in others) o.LineUserId = null;
            if (others.Count > 0) await db.SaveChangesAsync(ct);   // 先清掉，避免同批 insert 撞唯一鍵

            leader.LineUserId = lineUserId;
            await db.SaveChangesAsync(ct);
        }
        return leader;
    }

    // 重算該 room 目前有效置頂 → 寫進 Firestore announcements/{roomId}
    private async Task SyncPinnedAsync(Guid roomId, CancellationToken ct)
    {
        var rows = await db.Announcements
            .Where(a => a.RoomId == roomId && a.IsActive)
            .OrderBy(a => a.PinnedAtUtc)
            .Join(db.Messages, a => a.MessageId, m => m.MessageId, (a, m) => new
            {
                m.MessageId, m.Content, m.SenderName, a.PinnedAtUtc,
            })
            .Take(MaxPins)
            .ToListAsync(ct);

        var pinned = rows.Select(r => (object)new Dictionary<string, object?>
        {
            ["messageId"] = r.MessageId.ToString(),
            ["content"] = r.Content,
            ["senderName"] = r.SenderName,
            // 從 SQL 讀回的 DateTime.Kind 是 Unspecified；Firestore 要求 Utc，明確標記。
            ["pinnedAt"] = DateTime.SpecifyKind(r.PinnedAtUtc, DateTimeKind.Utc),
        }).ToList();

        await mirror.WriteAnnouncementsAsync(roomId, pinned, ct);
    }
}
