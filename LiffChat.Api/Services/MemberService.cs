using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Services;

public class MemberService(AppDbContext db, IFirestoreMirror mirror)
{
    public enum DirectError { None, NotBound, TargetNotFound, SelfDm, GroupChatDisabled, TargetRejects }
    public sealed record DirectResult(DirectError Error, Guid RoomId = default, bool Created = false);

    // 發起一對一。三條件 AND（僅團員↔團員適用條件 2、3；私訊導領不受 2、3 限制）：
    //  1) 同團（硬性，兩人都在此 tour）
    //  2) 團層級開關開啟（Tour.GroupChatEnabled）
    //  3) 對方個人開關允許（target.AcceptMemberDm）
    public async Task<DirectResult> OpenDirectAsync(
        string tourId, string lineUserId, Guid targetParticipantId, CancellationToken ct = default)
    {
        var me = await db.Participants.FirstOrDefaultAsync(
            p => p.TourId == tourId && p.LineUserId == lineUserId && p.Status == 0, ct);
        if (me is null) return new(DirectError.NotBound);

        if (me.ParticipantId == targetParticipantId) return new(DirectError.SelfDm);

        var target = await db.Participants.FirstOrDefaultAsync(
            p => p.ParticipantId == targetParticipantId && p.TourId == tourId && p.Status == 0, ct);
        if (target is null) return new(DirectError.TargetNotFound);   // 含「不同團」（硬性邊界）

        // 團員↔團員才套用條件 2、3；對方是導領則直接放行
        if (target.Kind == 0)
        {
            var tour = await db.Tours.FindAsync([tourId], ct);
            if (tour is null || !tour.GroupChatEnabled) return new(DirectError.GroupChatDisabled);
            if (!target.AcceptMemberDm) return new(DirectError.TargetRejects);
        }

        // 既有 direct room（同團、含此兩人）→ 直接回；否則建立
        var existing = await FindDirectRoomAsync(tourId, me.ParticipantId, target.ParticipantId, ct);
        if (existing is Guid roomId) return new(DirectError.None, roomId, false);

        var now = DateTime.UtcNow;
        var room = new Room { RoomId = Guid.NewGuid(), TourId = tourId, Type = 1, CreatedAtUtc = now };
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember { RoomId = room.RoomId, ParticipantId = me.ParticipantId, JoinedAtUtc = now, IsActive = true });
        db.RoomMembers.Add(new RoomMember { RoomId = room.RoomId, ParticipantId = target.ParticipantId, JoinedAtUtc = now, IsActive = true });
        await db.SaveChangesAsync(ct);

        // Firestore 成員對照表（供 rules）
        await mirror.UpsertMembershipAsync(room.RoomId, me.ParticipantId, ct);
        await mirror.UpsertMembershipAsync(room.RoomId, target.ParticipantId, ct);

        return new(DirectError.None, room.RoomId, true);
    }

    // direct room 唯一性：同團、含此兩位成員（direct 恆兩人，命中即該 pair 房）
    private async Task<Guid?> FindDirectRoomAsync(string tourId, Guid a, Guid b, CancellationToken ct)
    {
        var roomId = await db.Rooms
            .Where(r => r.TourId == tourId && r.Type == 1)
            .Where(r => db.RoomMembers.Any(m => m.RoomId == r.RoomId && m.ParticipantId == a)
                     && db.RoomMembers.Any(m => m.RoomId == r.RoomId && m.ParticipantId == b))
            .Select(r => (Guid?)r.RoomId)
            .FirstOrDefaultAsync(ct);
        return roomId;
    }

    // 個人私訊開關
    public async Task<bool?> UpdateAcceptMemberDmAsync(
        string tourId, string lineUserId, bool value, CancellationToken ct = default)
    {
        var me = await db.Participants.FirstOrDefaultAsync(
            p => p.TourId == tourId && p.LineUserId == lineUserId && p.Status == 0, ct);
        if (me is null) return null;

        me.AcceptMemberDm = value;
        await db.SaveChangesAsync(ct);
        return me.AcceptMemberDm;
    }

    public sealed record MemberItem(
        Guid ParticipantId, string DisplayName, string? AvatarUrl, int Kind, bool CanDm);

    // 列出同團其他有效成員（供「找團員私訊」選人）。
    // canDm：對方可否被我私訊 —— 導領永遠可；團員需團層級開關開 + 對方個人開關開。
    // 回 null = 我未綁定。
    public async Task<List<MemberItem>?> ListMembersAsync(
        string tourId, string lineUserId, CancellationToken ct = default)
    {
        var me = await db.Participants.FirstOrDefaultAsync(
            p => p.TourId == tourId && p.LineUserId == lineUserId && p.Status == 0, ct);
        if (me is null) return null;

        var tour = await db.Tours.FindAsync([tourId], ct);
        var groupOn = tour?.GroupChatEnabled ?? false;

        var others = await db.Participants
            .Where(p => p.TourId == tourId && p.Status == 0 && p.ParticipantId != me.ParticipantId)
            .OrderBy(p => p.Kind == 1 ? 0 : 1)          // 導領排前面
            .ThenBy(p => p.DisplayName)
            .ToListAsync(ct);

        return others.Select(p => new MemberItem(
            p.ParticipantId, p.DisplayName, p.AvatarUrl, p.Kind,
            CanDm: p.Kind == 1 || (groupOn && p.AcceptMemberDm)   // 導領恆可；團員看兩道開關
        )).ToList();
    }
}
