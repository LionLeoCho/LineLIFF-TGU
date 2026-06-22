using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LiffChat.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Tour> Tours => Set<Tour>();
    public DbSet<Passenger> Passengers => Set<Passenger>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<ParticipantPassenger> ParticipantPassengers => Set<ParticipantPassenger>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Announcement> Announcements => Set<Announcement>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tour>().HasKey(x => x.TourId);
        b.Entity<Passenger>().HasKey(x => x.PassengerId);
        b.Entity<Passenger>().HasIndex(x => x.TourId);

        b.Entity<Participant>().HasKey(x => x.ParticipantId);
        // 客人 participant 唯一性：同團下一個有效 LineUserId 對一個 participant
        b.Entity<Participant>()
            .HasIndex(x => new { x.TourId, x.LineUserId })
            .HasFilter("[LineUserId] IS NOT NULL AND [Status] = 0")
            .IsUnique();

        b.Entity<ParticipantPassenger>().HasKey(x => new { x.ParticipantId, x.PassengerId });

        b.Entity<Room>().HasKey(x => x.RoomId);
        b.Entity<Room>().HasIndex(x => x.TourId);

        b.Entity<RoomMember>().HasKey(x => new { x.RoomId, x.ParticipantId });

        b.Entity<Message>().HasKey(x => x.MessageId);
        b.Entity<Message>().HasIndex(x => new { x.RoomId, x.CreatedAtUtc }); // 歷史分頁

        b.Entity<Announcement>().HasKey(x => x.MessageId);
        b.Entity<Announcement>().HasIndex(x => new { x.RoomId, x.IsActive });
    }

    // SQL Server 的 DATETIME2 不保存 DateTimeKind，讀回會是 Unspecified。
    // 統一在讀取時標記為 Utc，避免寫 Firestore（要求 Utc）時拋例外。
    protected override void ConfigureConventions(ModelConfigurationBuilder cfg)
    {
        cfg.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        cfg.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }
}

public class UtcDateTimeConverter()
    : ValueConverter<DateTime, DateTime>(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public class NullableUtcDateTimeConverter()
    : ValueConverter<DateTime?, DateTime?>(
        v => v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
