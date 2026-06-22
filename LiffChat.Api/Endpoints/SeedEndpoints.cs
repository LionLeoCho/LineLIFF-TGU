using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using LiffChat.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Endpoints;

// 只在 Development 掛載（見 Program.cs）。一鍵塞測試資料，省去手動進 SQL。
// 內網佈好後：POST /api/dev/seed  → 回傳可直接拿去測的 tourId / roomId。
// 冪等：重複呼叫不會爆；?reset=true 會先清掉該團重建。
public static class SeedEndpoints
{
    public const string TourId = "demo-tour";

    public static void MapSeedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/dev/seed", async (
            AppDbContext db,
            IFirestoreMirror mirror,
            CancellationToken ct,
            bool reset = false) =>
        {
            if (reset)
            {
                await db.Messages.Where(x => x.TourId == TourId).ExecuteDeleteAsync(ct);
                var roomIds = await db.Rooms.Where(r => r.TourId == TourId)
                    .Select(r => r.RoomId).ToListAsync(ct);
                await db.RoomMembers.Where(m => roomIds.Contains(m.RoomId)).ExecuteDeleteAsync(ct);
                await db.Rooms.Where(r => r.TourId == TourId).ExecuteDeleteAsync(ct);
                await db.ParticipantPassengers
                    .Where(b => db.Participants.Any(p => p.ParticipantId == b.ParticipantId && p.TourId == TourId))
                    .ExecuteDeleteAsync(ct);
                await db.Participants.Where(p => p.TourId == TourId).ExecuteDeleteAsync(ct);
                await db.Passengers.Where(p => p.TourId == TourId).ExecuteDeleteAsync(ct);
                await db.Tours.Where(t => t.TourId == TourId).ExecuteDeleteAsync(ct);
            }

            var now = DateTime.UtcNow;

            // 團（不存在才建）
            var tour = await db.Tours.FindAsync([TourId], ct);
            if (tour is null)
            {
                var returnDate = DateOnly.FromDateTime(now.AddDays(5));
                var endAt = returnDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                tour = new Tour
                {
                    TourId = TourId,
                    TourName = "東京五日遊",
                    ReturnDate = returnDate,
                    EndAtUtc = endAt,
                    PurgeAtUtc = endAt.AddDays(30),
                    Status = 0,
                    GroupChatEnabled = true,
                    RosterVersion = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                db.Tours.Add(tour);
            }

            // 名單（對齊前端 demo 可用資料）
            var roster = new (string Id, string Name, string Birth)[]
            {
                ("P001", "陳小華", "1990-03-15"),
                ("P002", "王大明", "1985-07-22"),
                ("P003", "林美麗", "1992-11-08"),
            };
            foreach (var r in roster)
            {
                if (await db.Passengers.FindAsync([r.Id], ct) is null)
                {
                    db.Passengers.Add(new Passenger
                    {
                        PassengerId = r.Id,
                        TourId = TourId,
                        FullName = r.Name,
                        BirthDate = DateOnly.Parse(r.Birth),
                        Status = 0,
                        UpdatedAtUtc = now,
                    });
                }
            }

            // 整團群聊 room（不存在才建）
            var groupRoom = await db.Rooms.FirstOrDefaultAsync(
                r => r.TourId == TourId && r.Type == 0, ct);
            if (groupRoom is null)
            {
                groupRoom = new Room
                {
                    RoomId = Guid.NewGuid(),
                    TourId = TourId,
                    Type = 0,
                    CreatedAtUtc = now,
                };
                db.Rooms.Add(groupRoom);
            }

            // 導領 participant（讓群聊有導領、之後可測私訊導領）
            var leader = await db.Participants.FirstOrDefaultAsync(
                p => p.TourId == TourId && p.Kind == 1, ct);
            if (leader is null)
            {
                leader = new Participant
                {
                    ParticipantId = Guid.NewGuid(),
                    TourId = TourId,
                    Kind = 1,
                    LeaderAccountId = "L-demo",
                    DisplayName = "導遊 小林",
                    AcceptMemberDm = true,
                    Status = 0,
                    CreatedAtUtc = now,
                };
                db.Participants.Add(leader);
                db.RoomMembers.Add(new RoomMember
                {
                    RoomId = groupRoom.RoomId,
                    ParticipantId = leader.ParticipantId,
                    JoinedAtUtc = now,
                    IsActive = true,
                });
            }

            await db.SaveChangesAsync(ct);

            // 導領成員也寫進 Firestore 對照表（供 rules）
            await mirror.UpsertMembershipAsync(groupRoom.RoomId, leader.ParticipantId, ct);

            return Results.Ok(new
            {
                tourId = TourId,
                groupRoomId = groupRoom.RoomId,
                leaderParticipantId = leader.ParticipantId,
                passengers = roster.Select(r => new { r.Id, r.Name, r.Birth }),
                hint = "用 X-Debug-UserId 打 /bind（陳小華/1990-03-15）取得 participantId，再用 groupRoomId 發訊。",
            });
        });

        // 手動觸發排程作業（自動結束 + 清除），方便測試不用等到時間到
        app.MapPost("/api/dev/run-jobs", async (ScheduleService schedule, CancellationToken ct) =>
        {
            var r = await schedule.RunAllAsync(ct);
            return Results.Ok(new { closed = r.Closed, purged = r.Purged });
        });

        // 查某 room 目前線上的 participant（驗 presence；門檻 60 秒）
        app.MapGet("/api/dev/presence/{roomId:guid}", async (
            Guid roomId, IFirestoreMirror mirror, CancellationToken ct) =>
        {
            var online = await mirror.GetOnlineParticipantsAsync(roomId, TimeSpan.FromSeconds(60), ct);
            return Results.Ok(new { roomId, online });
        });
    }
}
