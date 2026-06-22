using LiffChat.Api.Data;
using LiffChat.Api.Firebase;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Services;

// spec §G 兩個每日作業。
public class ScheduleService(AppDbContext db, IFirestoreMirror mirror, ILogger<ScheduleService> logger)
{
    public sealed record JobResult(int Closed, int Purged);

    // 自動結束：EndAtUtc 已過且仍進行中（Status=0）→ Status=1（唯讀回看）。
    public async Task<int> AutoCloseAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var due = await db.Tours
            .Where(t => t.Status == 0 && t.EndAtUtc != null && t.EndAtUtc <= now)
            .ToListAsync(ct);
        foreach (var t in due) { t.Status = 1; t.UpdatedAtUtc = now; }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        if (due.Count > 0) logger.LogInformation("自動結束 {N} 團", due.Count);
        return due.Count;
    }

    // 清除：PurgeAtUtc 已過且已結束/取消 → 清 Firestore 該團資料；SQL 不動。
    // 清完把 PurgeAtUtc 設 null，避免每日重掃（免加欄位的去重）。
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var due = await db.Tours
            .Where(t => t.PurgeAtUtc != null && t.PurgeAtUtc <= now && t.Status != 0)
            .ToListAsync(ct);

        foreach (var t in due)
        {
            var roomIds = await db.Rooms.Where(r => r.TourId == t.TourId)
                .Select(r => r.RoomId).ToListAsync(ct);
            await mirror.PurgeRoomsAsync(roomIds, ct);
            t.PurgeAtUtc = null;   // 標記已清，後續不再選到
            t.UpdatedAtUtc = now;
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        if (due.Count > 0) logger.LogInformation("清除 {N} 團的 Firestore 鏡像", due.Count);
        return due.Count;
    }

    public async Task<JobResult> RunAllAsync(CancellationToken ct = default)
        => new(await AutoCloseAsync(ct), await PurgeAsync(ct));
}

// 定時跑排程（預設每 60 分鐘）。ScheduleService 是 scoped，這裡每次開 scope 解析。
public class SchedulerHostedService(IServiceScopeFactory scopeFactory, IConfiguration cfg, ILogger<SchedulerHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(cfg.GetValue("Schedule:IntervalMinutes", 60));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ScheduleService>();
                await svc.RunAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "排程作業失敗");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
