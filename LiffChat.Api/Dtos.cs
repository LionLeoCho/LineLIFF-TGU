namespace LiffChat.Api;

// ---- POST /api/tours/{tourId}/bind ----
public record BindRequest(string FullName, DateOnly BirthDate);

public record BindResponse(
    Guid ParticipantId,
    string DisplayName,
    bool AcceptMemberDm,
    bool GroupChatEnabled,
    bool PushEnabled,
    string FirebaseToken);   // 併發回 custom token（§I-1：不另開端點）

// ---- GET /api/tours/{tourId}/me ----
public record MeResponse(
    bool Bound,
    Guid? ParticipantId,
    string? DisplayName,
    bool? AcceptMemberDm,
    bool? GroupChatEnabled,
    bool? PushEnabled,
    string? FirebaseToken);

// ---- POST /api/rooms/{roomId}/messages ----
public record SendMessageRequest(
    byte Type,                               // 0=text 1=image(URL) 2=location(JSON)
    string Content,
    List<Guid>? MentionParticipantIds);

public record SendMessageResponse(
    Guid MessageId,
    DateTime CreatedAtUtc);

// ---- GET /api/tours/{tourId}/rooms ----
public record RoomListItem(
    Guid RoomId,
    byte Type,                          // 0=group 1=direct
    string Title,                       // group=團名；direct=對方顯示名
    Guid? CounterpartParticipantId,     // direct 用
    byte CounterpartKind,               // 0=客人 1=導領（顯示「團員/導領」標籤）
    string? CounterpartAvatarUrl,
    LastMessageDto? LastMessage,
    int UnreadCount);

public record LastMessageDto(
    string Preview,
    string SenderName,
    DateTime CreatedAtUtc);

// ---- POST /api/ingest/tour-roster（主系統 → LIFF 後端，§F-1）----
public record RosterIngestRequest(
    int Version,                          // §I-2：每團單調遞增版本號，防亂序
    RosterTour Tour,
    List<RosterPassenger> Passengers);    // 整團完整名單（覆蓋式）

public record RosterTour(
    string TourId,
    string TourName,
    DateOnly ReturnDate);

public record RosterPassenger(
    string PassengerId,                   // 比對錨點（穩定唯一）
    string FullName,
    DateOnly BirthDate);

public record RosterIngestResult(
    bool Applied,                         // false = 版本過時被丟棄（仍視為成功）
    int CurrentVersion,
    Guid? GroupRoomId,
    int Added,
    int Updated,
    int Retired);

// ---- POST /api/tours/{tourId}/direct ----
public record OpenDirectRequest(Guid TargetParticipantId);
public record OpenDirectResponse(Guid RoomId, bool Created);

// ---- PATCH /api/tours/{tourId}/me/settings ----
public record UpdateSettingsRequest(bool? AcceptMemberDm, bool? PushEnabled);
public record SettingsResponse(bool AcceptMemberDm, bool PushEnabled);

// ---- POST /api/geocode ----
public record GeocodeRequest(double Lat, double Lng);

// ---- 導領端 ----
public record AnnouncementRequest(string Content, bool Pin);
public record AnnouncementResponse(Guid MessageId, bool Pinned);

public record GroupChatToggleRequest(bool GroupChatEnabled);
public record ScheduleRequest(DateTime EndAtUtc);

public record UnboundPassenger(string PassengerId, string FullName, DateOnly BirthDate);

// §F-2 導領指派（導領系統 → LIFF 後端）
public record LeaderIngestRequest(string TourId, List<LeaderDto> Leaders);
public record LeaderDto(string LeaderAccountId, string DisplayName, string? AvatarUrl, bool Active);
