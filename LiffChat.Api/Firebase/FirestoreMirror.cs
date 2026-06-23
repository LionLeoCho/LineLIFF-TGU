using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using LiffChat.Api.Data;

namespace LiffChat.Api.Firebase;

// Firestore 為「即時鏡像」。.NET 寫入，前端只訂閱（不可寫 messages）。
// 路徑：rooms/{roomId}/messages/{messageId}（精簡欄位，含冗余 senderName/avatar）。
// 另維護 roomMembers/{roomId}/members/{participantId}，供 security rules 的 isRoomMember 判斷。
public interface IFirestoreMirror
{
    Task WriteMessageAsync(Message m, CancellationToken ct = default);
    Task UpsertMembershipAsync(Guid roomId, Guid participantId, CancellationToken ct = default);
    Task RemoveMembershipAsync(Guid roomId, Guid participantId, CancellationToken ct = default);
    Task WriteAnnouncementsAsync(Guid roomId, IEnumerable<object> pinned, CancellationToken ct = default);
    Task<List<string>> GetOnlineParticipantsAsync(Guid roomId, TimeSpan stale, CancellationToken ct = default);
    Task PurgeRoomsAsync(IEnumerable<Guid> roomIds, CancellationToken ct = default);
}

public class FirestoreMirror : IFirestoreMirror
{
    private readonly FirestoreDb _db;

    public FirestoreMirror(IConfiguration cfg)
    {
        var projectId = cfg["Firebase:ProjectId"]
            ?? throw new InvalidOperationException("缺少設定 Firebase:ProjectId");
        var saPath = cfg["Firebase:ServiceAccountPath"]
            ?? throw new InvalidOperationException("缺少設定 Firebase:ServiceAccountPath");
        // DatabaseId：你的資料庫是具名的 "default"（非 Firestore 預設的 "(default)"），需明確指定。
        // Credential：明確帶 service account，與 FirebaseTokenService 同一把，免設環境變數。
        _db = new FirestoreDbBuilder
        {
            ProjectId = projectId,
            DatabaseId = cfg["Firebase:DatabaseId"] ?? "default",
            Credential = GoogleCredential.FromFile(saPath),
        }.Build();
    }

    public Task WriteMessageAsync(Message m, CancellationToken ct = default)
    {
        var doc = _db.Collection("rooms").Document(m.RoomId.ToString())
                     .Collection("messages").Document(m.MessageId.ToString());

        return doc.SetAsync(new Dictionary<string, object?>
        {
            ["messageId"] = m.MessageId.ToString(),
            ["senderParticipantId"] = m.SenderParticipantId.ToString(),
            ["senderName"] = m.SenderName,
            ["senderAvatar"] = m.SenderAvatarUrl,
            ["type"] = m.Type,
            ["content"] = m.Content,
            ["isAnnouncement"] = m.IsAnnouncement,
            ["mentionParticipantIds"] = m.MentionParticipantIds,
            ["createdAt"] = m.CreatedAtUtc, // Firestore 存為 Timestamp
        }, cancellationToken: ct);
    }

    public Task UpsertMembershipAsync(Guid roomId, Guid participantId, CancellationToken ct = default)
    {
        var doc = _db.Collection("roomMembers").Document(roomId.ToString())
                     .Collection("members").Document(participantId.ToString());
        return doc.SetAsync(new Dictionary<string, object?> { ["joinedAt"] = DateTime.UtcNow },
            cancellationToken: ct);
    }

    // 退團者：從成員對照表移除 → rules 的 isRoomMember 不再放行（讀不到、進不來）
    public Task RemoveMembershipAsync(Guid roomId, Guid participantId, CancellationToken ct = default)
    {
        var doc = _db.Collection("roomMembers").Document(roomId.ToString())
                     .Collection("members").Document(participantId.ToString());
        return doc.DeleteAsync(cancellationToken: ct);
    }

    // 置頂公告：announcements/{roomId} { pinned: [...] }（≤3），前端訂閱顯示置頂區
    public Task WriteAnnouncementsAsync(Guid roomId, IEnumerable<object> pinned, CancellationToken ct = default)
    {
        var doc = _db.Collection("announcements").Document(roomId.ToString());
        return doc.SetAsync(new Dictionary<string, object?> { ["pinned"] = pinned.ToList() },
            cancellationToken: ct);
    }

    // 清除整團 Firestore 鏡像（30 天期滿）：messages/presence/reads 子集合、room 文件、
    // announcements、roomMembers。SQL 不動（永久權威）。
    public async Task PurgeRoomsAsync(IEnumerable<Guid> roomIds, CancellationToken ct = default)
    {
        foreach (var roomId in roomIds)
        {
            var rid = roomId.ToString();
            var roomDoc = _db.Collection("rooms").Document(rid);
            await DeleteCollectionAsync(roomDoc.Collection("messages"), ct);
            await DeleteCollectionAsync(roomDoc.Collection("presence"), ct);
            await DeleteCollectionAsync(roomDoc.Collection("reads"), ct);
            await roomDoc.DeleteAsync(cancellationToken: ct);

            await _db.Collection("announcements").Document(rid).DeleteAsync(cancellationToken: ct);

            var membersDoc = _db.Collection("roomMembers").Document(rid);
            await DeleteCollectionAsync(membersDoc.Collection("members"), ct);
            await membersDoc.DeleteAsync(cancellationToken: ct);
        }
    }

    private async Task DeleteCollectionAsync(CollectionReference col, CancellationToken ct, int batchSize = 400)
    {
        while (true)
        {
            var snap = await col.Limit(batchSize).GetSnapshotAsync(ct);
            if (snap.Count == 0) break;
            var batch = _db.StartBatch();
            foreach (var d in snap.Documents) batch.Delete(d.Reference);
            await batch.CommitAsync(ct);
            if (snap.Count < batchSize) break;
        }
    }

    // 線上判斷：online=true 且 lastActiveAt 在 stale 門檻內（心跳沒斷）→ 視為在線。
    // 供推播「離線才推」使用。
    public async Task<List<string>> GetOnlineParticipantsAsync(Guid roomId, TimeSpan stale, CancellationToken ct = default)
    {
        var snap = await _db.Collection("rooms").Document(roomId.ToString())
                            .Collection("presence").GetSnapshotAsync(ct);
        var cutoff = DateTime.UtcNow - stale;
        var online = new List<string>();
        foreach (var d in snap.Documents)
        {
            var data = d.ToDictionary();
            var isOnline = data.TryGetValue("online", out var o) && o is bool b && b;
            DateTime? last = data.TryGetValue("lastActiveAt", out var la) && la is Timestamp ts
                ? ts.ToDateTime() : null;
            if (isOnline && last is { } t && t >= cutoff) online.Add(d.Id);
        }
        return online;
    }
}
