using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Services;

public class BindingService(AppDbContext db, IFirestoreMirror mirror)
{
    public sealed record Result(bool Found, Participant? Participant);

    // 比對名單（姓名+生日）→ 建立/復用 participant → 綁定（新蓋舊）→ 確保在群聊成員內。
    public async Task<Result> ExecuteBindingAsync(string tourId, string lineUserId,
        string fullName, DateOnly birthDate, CancellationToken ct = default)
    {
        // 比對錨點是名單，但實際存綁定是落在 PassengerId（穩定唯一）。
        var passenger = await db.Passengers.FirstOrDefaultAsync(
            p => p.TourId == tourId && p.FullName == fullName &&
                 p.BirthDate == birthDate && p.Status == 0, ct);

        if (passenger is null)
            return new Result(false, null);   // → 404，前端無限重試 + 聯絡導領

        // 找此 userId 在本團是否已有有效 participant
        var participant = await db.Participants.FirstOrDefaultAsync(
            x => x.TourId == tourId && x.LineUserId == lineUserId && x.Status == 0, ct);

        if (participant is null)
        {
            participant = new Participant
            {
                ParticipantId = Guid.NewGuid(),
                TourId = tourId,
                Kind = 0,
                LineUserId = lineUserId,
                DisplayName = passenger.FullName,   // 顯示用主系統真實姓名
                AcceptMemberDm = true,
                Status = 0,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Participants.Add(participant);
        }
        else
        {
            participant.DisplayName = passenger.FullName; // 同步最新姓名
        }

        // 新蓋舊：同一 PassengerId 之前若綁在別的 participant（換手機/換帳號），讓舊綁定失效。
        var oldBindings = await db.ParticipantPassengers
            .Where(b => b.PassengerId == passenger.PassengerId &&
                        b.ParticipantId != participant.ParticipantId)
            .ToListAsync(ct);

        foreach (var ob in oldBindings)
        {
            db.ParticipantPassengers.Remove(ob);
            // 舊 participant 若已無其他綁定 → 停用（本人比照不在這團）
            var stillBound = await db.ParticipantPassengers
                .AnyAsync(b => b.ParticipantId == ob.ParticipantId &&
                               b.PassengerId != passenger.PassengerId, ct);
            if (!stillBound)
            {
                var old = await db.Participants.FindAsync([ob.ParticipantId], ct);
                if (old is not null) old.Status = 1;
            }
        }

        // 建立本 participant 對該 passenger 的綁定（若尚無）
        var hasBinding = await db.ParticipantPassengers.AnyAsync(
            b => b.ParticipantId == participant.ParticipantId &&
                 b.PassengerId == passenger.PassengerId, ct);
        if (!hasBinding)
        {
            db.ParticipantPassengers.Add(new ParticipantPassenger
            {
                ParticipantId = participant.ParticipantId,
                PassengerId = passenger.PassengerId,
                BoundAtUtc = DateTime.UtcNow,
            });
        }

        // 確保進整團群聊（一綁定就能聊）
        await EnsureGroupMembershipAsync(tourId, participant.ParticipantId, ct);

        await db.SaveChangesAsync(ct);
        return new Result(true, participant);
    }

    public Task<Participant?> FindBoundAsync(string tourId, string lineUserId, CancellationToken ct = default) =>
        db.Participants.FirstOrDefaultAsync(
            x => x.TourId == tourId && x.LineUserId == lineUserId && x.Status == 0, ct);

    private async Task EnsureGroupMembershipAsync(string tourId, Guid participantId, CancellationToken ct)
    {
        var groupRoom = await db.Rooms.FirstOrDefaultAsync(
            r => r.TourId == tourId && r.Type == 0, ct);
        if (groupRoom is null) return; // 群聊 room 於團誕生時建立；理論上已存在

        var member = await db.RoomMembers.FindAsync([groupRoom.RoomId, participantId], ct);
        if (member is null)
        {
            db.RoomMembers.Add(new RoomMember
            {
                RoomId = groupRoom.RoomId,
                ParticipantId = participantId,
                JoinedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            // 同步寫 Firestore 成員對照表（供 rules）
            await mirror.UpsertMembershipAsync(groupRoom.RoomId, participantId, ct);
        }
        else if (!member.IsActive)
        {
            member.IsActive = true;
            await mirror.UpsertMembershipAsync(groupRoom.RoomId, participantId, ct);
        }
    }
}
