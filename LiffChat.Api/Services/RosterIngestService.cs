using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Services;

// §F-1 名單推送 + §I-2 版本號防亂序 + §8.5 變動/退團處理。
// 整團覆蓋：同 PassengerId 更新、缺 ID 標退團（保留歷史）、新 ID 新增。
public class RosterIngestService(AppDbContext db, IFirestoreMirror mirror, IConfiguration cfg)
{
    public async Task<RosterIngestResult> IngestAsync(RosterIngestRequest req, CancellationToken ct = default)
    {
        var tour = await db.Tours.FindAsync([req.Tour.TourId], ct);

        // §I-2：版本檢查。新版本 ≤ 已存 → 丟棄（舊包亂序/重複），但回「成功」語意。
        var storedVersion = tour?.RosterVersion ?? 0;
        if (tour is not null && req.Version <= storedVersion)
        {
            return new RosterIngestResult(false, storedVersion,
                await GroupRoomIdAsync(req.Tour.TourId, ct), 0, 0, 0);
        }

        var now = DateTime.UtcNow;
        var endAt = ComputeEndAtUtc(req.Tour.ReturnDate);

        // 團：不存在則建立（觸發群聊 room 誕生）
        Guid? groupRoomId;
        if (tour is null)
        {
            tour = new Tour
            {
                TourId = req.Tour.TourId,
                CreatedAtUtc = now,
                Status = 0,
                GroupChatEnabled = true,
            };
            db.Tours.Add(tour);
            groupRoomId = await EnsureGroupRoomAsync(req.Tour.TourId, now, ct);
        }
        else
        {
            groupRoomId = await EnsureGroupRoomAsync(req.Tour.TourId, now, ct);
        }

        tour.TourName = req.Tour.TourName;
        tour.ReturnDate = req.Tour.ReturnDate;
        tour.EndAtUtc = endAt;
        tour.PurgeAtUtc = endAt.AddDays(30);
        tour.RosterVersion = req.Version;
        tour.UpdatedAtUtc = now;

        // 既有名單
        var existing = await db.Passengers
            .Where(p => p.TourId == req.Tour.TourId)
            .ToListAsync(ct);
        var existingById = existing.ToDictionary(p => p.PassengerId);
        var incomingIds = req.Passengers.Select(p => p.PassengerId).ToHashSet();

        int added = 0, updated = 0, retired = 0;

        // 同 ID 更新 / 新 ID 新增
        foreach (var inc in req.Passengers)
        {
            if (existingById.TryGetValue(inc.PassengerId, out var p))
            {
                // 資料更新（改名、生日更正）：保住既有綁定，只更新底層資料；若曾退團則復團
                p.FullName = inc.FullName;
                p.BirthDate = inc.BirthDate;
                if (p.Status == 1) p.Status = 0;   // 復團
                p.UpdatedAtUtc = now;
                updated++;
            }
            else
            {
                db.Passengers.Add(new Passenger
                {
                    PassengerId = inc.PassengerId,
                    TourId = req.Tour.TourId,
                    FullName = inc.FullName,
                    BirthDate = inc.BirthDate,
                    Status = 0,
                    UpdatedAtUtc = now,
                });
                added++;
            }
        }

        // 缺 ID → 退團（標記非刪除，保留歷史）
        foreach (var gone in existing.Where(p => !incomingIds.Contains(p.PassengerId) && p.Status == 0))
        {
            gone.Status = 1;
            gone.UpdatedAtUtc = now;
            retired++;
            await RetireParticipantIfOrphanedAsync(req.Tour.TourId, gone.PassengerId, ct);
        }

        await db.SaveChangesAsync(ct);

        return new RosterIngestResult(true, req.Version, groupRoomId, added, updated, retired);
    }

    // 退團乘客所屬 participant 若已無任何在團綁定 → 停用、移出群聊、其私訊轉唯讀（靠 participant.Status）
    private async Task RetireParticipantIfOrphanedAsync(string tourId, string passengerId, CancellationToken ct)
    {
        var binding = await db.ParticipantPassengers
            .FirstOrDefaultAsync(b => b.PassengerId == passengerId, ct);
        if (binding is null) return;   // 該乘客還沒人綁定，無 participant 要處理

        var participantId = binding.ParticipantId;

        // 此 participant 是否還綁著其他「在團」乘客？（一人代訂全家，可能還有人在團）
        var stillActive = await db.ParticipantPassengers
            .Where(b => b.ParticipantId == participantId && b.PassengerId != passengerId)
            .Join(db.Passengers, b => b.PassengerId, p => p.PassengerId, (b, p) => p.Status)
            .AnyAsync(s => s == 0, ct);
        if (stillActive) return;       // 還有家人在團 → participant 維持有效

        var participant = await db.Participants.FindAsync([participantId], ct);
        if (participant is null || participant.Status == 1) return;

        participant.Status = 1;        // 停用 → 與其私訊轉唯讀（IsRoomReadonlyAsync 會擋）

        // 移出所有 room 成員清單 + 同步移除 Firestore 對照表（rules 不再放行）
        var memberships = await db.RoomMembers
            .Where(m => m.ParticipantId == participantId && m.IsActive)
            .ToListAsync(ct);
        foreach (var m in memberships)
        {
            m.IsActive = false;
            await mirror.RemoveMembershipAsync(m.RoomId, participantId, ct);
        }
    }

    private async Task<Guid> EnsureGroupRoomAsync(string tourId, DateTime now, CancellationToken ct)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.TourId == tourId && r.Type == 0, ct);
        if (room is not null) return room.RoomId;

        room = new Room { RoomId = Guid.NewGuid(), TourId = tourId, Type = 0, CreatedAtUtc = now };
        db.Rooms.Add(room);
        return room.RoomId;
    }

    private Task<Guid?> GroupRoomIdAsync(string tourId, CancellationToken ct) =>
        db.Rooms.Where(r => r.TourId == tourId && r.Type == 0)
                .Select(r => (Guid?)r.RoomId).FirstOrDefaultAsync(ct);

    // 回程日+1 的「當地 00:00」轉 UTC。時區可設定，預設台北。
    private DateTime ComputeEndAtUtc(DateOnly returnDate)
    {
        var tzId = cfg["Tour:LocalTimeZoneId"] ?? "Taipei Standard Time";
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        var localMidnight = returnDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);
    }
}
