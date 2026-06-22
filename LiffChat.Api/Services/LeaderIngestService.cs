using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Services;

// §F-2 導領指派（導領系統 → LIFF 後端）。與名單 API 分開（導領走不同入口）。
// 建立/更新導領 participant(Kind=1)、補進群聊；active=false 停用（換人）；允許一團多位導領。
public class LeaderIngestService(AppDbContext db, IFirestoreMirror mirror)
{
    public async Task<int> IngestAsync(LeaderIngestRequest req, CancellationToken ct = default)
    {
        var groupRoom = await db.Rooms.FirstOrDefaultAsync(r => r.TourId == req.TourId && r.Type == 0, ct);
        var now = DateTime.UtcNow;
        int affected = 0;

        foreach (var l in req.Leaders)
        {
            var p = await db.Participants.FirstOrDefaultAsync(
                x => x.TourId == req.TourId && x.Kind == 1 && x.LeaderAccountId == l.LeaderAccountId, ct);

            if (p is null)
            {
                p = new Participant
                {
                    ParticipantId = Guid.NewGuid(),
                    TourId = req.TourId,
                    Kind = 1,
                    LeaderAccountId = l.LeaderAccountId,
                    DisplayName = l.DisplayName,
                    AvatarUrl = l.AvatarUrl,
                    Status = l.Active ? (byte)0 : (byte)1,
                    CreatedAtUtc = now,
                };
                db.Participants.Add(p);
            }
            else
            {
                p.DisplayName = l.DisplayName;
                p.AvatarUrl = l.AvatarUrl;
                p.Status = l.Active ? (byte)0 : (byte)1;
            }
            affected++;

            if (groupRoom is not null)
            {
                var member = await db.RoomMembers.FindAsync([groupRoom.RoomId, p.ParticipantId], ct);
                if (l.Active)
                {
                    if (member is null)
                        db.RoomMembers.Add(new RoomMember { RoomId = groupRoom.RoomId, ParticipantId = p.ParticipantId, JoinedAtUtc = now, IsActive = true });
                    else
                        member.IsActive = true;
                }
                else if (member is not null)
                {
                    member.IsActive = false;   // 換人：停用舊導領
                }
            }
        }

        await db.SaveChangesAsync(ct);

        // 同步 Firestore 成員對照表
        if (groupRoom is not null)
            foreach (var l in req.Leaders)
            {
                var p = await db.Participants.FirstAsync(
                    x => x.TourId == req.TourId && x.Kind == 1 && x.LeaderAccountId == l.LeaderAccountId, ct);
                if (l.Active) await mirror.UpsertMembershipAsync(groupRoom.RoomId, p.ParticipantId, ct);
                else await mirror.RemoveMembershipAsync(groupRoom.RoomId, p.ParticipantId, ct);
            }

        return affected;
    }
}
