using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LiffChat.Api.Data;

// 對應工程規格 §B SQL Server Schema。此處只放三支端點會碰到的核心表；
// Announcements / 排程等其餘表沿用同樣風格補上即可。

public class Tour
{
    public string TourId { get; set; } = default!;          // 來自主系統推送
    public string TourName { get; set; } = default!;
    public DateOnly ReturnDate { get; set; }
    public DateTime? EndAtUtc { get; set; }                  // 回程日+1 00:00（當地→UTC）
    public byte Status { get; set; }                          // 0=進行中 1=已結束(唯讀) 2=已取消
    public bool GroupChatEnabled { get; set; } = true;        // 團員互聊・團層級開關
    public DateTime? PurgeAtUtc { get; set; }
    public int RosterVersion { get; set; }                    // §I-2 亂序防護
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class Passenger
{
    public string PassengerId { get; set; } = default!;       // 主系統穩定唯一 ID（比對錨點）
    public string TourId { get; set; } = default!;
    public string FullName { get; set; } = default!;          // 比對 + 顯示
    public DateOnly BirthDate { get; set; }                   // 比對用
    public byte Status { get; set; }                          // 0=在團 1=已退團
    public DateTime UpdatedAtUtc { get; set; }
}

public class Participant
{
    public Guid ParticipantId { get; set; }                   // 聊天室身分主鍵
    public string TourId { get; set; } = default!;
    public byte Kind { get; set; }                            // 0=客人 1=導領
    public string? LineUserId { get; set; }                   // 客人綁定的 LINE userId（可推播）
    public string? LeaderAccountId { get; set; }
    public string DisplayName { get; set; } = default!;
    public string? AvatarUrl { get; set; }
    public bool AcceptMemberDm { get; set; } = true;
    public bool PushEnabled { get; set; } = true;            // 個人推播開關（新訊息推播），預設開
    public byte Status { get; set; }                          // 0=有效 1=退團/停用
    public DateTime CreatedAtUtc { get; set; }
}

public class ParticipantPassenger
{
    public Guid ParticipantId { get; set; }
    public string PassengerId { get; set; } = default!;
    public DateTime BoundAtUtc { get; set; }
}

public class Room
{
    public Guid RoomId { get; set; }
    public string TourId { get; set; } = default!;
    public byte Type { get; set; }                            // 0=group 1=direct
    public DateTime CreatedAtUtc { get; set; }
}

public class RoomMember
{
    public Guid RoomId { get; set; }
    public Guid ParticipantId { get; set; }
    public DateTime? LastReadAtUtc { get; set; }
    public DateTime JoinedAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Message
{
    public Guid MessageId { get; set; }                       // 與 Firestore 一致
    public Guid RoomId { get; set; }
    public string TourId { get; set; } = default!;            // 冗余，便於依團查詢
    public Guid SenderParticipantId { get; set; }
    public string SenderName { get; set; } = default!;        // 冗余快照（退團後仍顯示）
    public string? SenderAvatarUrl { get; set; }
    public byte Type { get; set; }                            // 0=text 1=image 2=location 9=system
    public string Content { get; set; } = default!;
    public bool IsAnnouncement { get; set; }
    public string? MentionParticipantIds { get; set; }        // JSON 陣列
    public DateTime CreatedAtUtc { get; set; }                // .NET 寫入時間為準
}

public class Announcement
{
    public Guid MessageId { get; set; }   // 對應的公告訊息（PK）
    public Guid RoomId { get; set; }      // group room
    public DateTime PinnedAtUtc { get; set; }
    public bool IsActive { get; set; }    // 導領手動撤下設 0；每 room IsActive=1 上限 3
}
