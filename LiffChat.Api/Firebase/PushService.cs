using System.Collections.Concurrent;
using System.Text.Json;
using LiffChat.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LiffChat.Api.Firebase;

// 推播由 .NET 後端發起（Messaging API）。規格 §5：
//  - 離線才推 + 多則聚合（同 room 短時間連發只推一次）
//  - 群聊分級：公告一定推 / 被@推（離線才推）/ 一般閒聊不推
//  - 私訊離線才推；內容含預覽；深連結帶 roomId
public interface IPushService
{
    Task OnNewMessageAsync(Message m, CancellationToken ct = default);
}

// 分級邏輯版：查 presence → 套規則 → 決定推給誰。
// 先以 log 模擬「會推給誰」，尚未接 LINE Messaging API（見 TODO）。
public class PushService(
    IServiceScopeFactory scopeFactory,
    IFirestoreMirror mirror,
    IConfiguration cfg,
    ILogger<PushService> logger) : IPushService
{
    private readonly TimeSpan _stale = TimeSpan.FromSeconds(cfg.GetValue("Push:StaleThresholdSeconds", 60));
    private readonly TimeSpan _aggWindow = TimeSpan.FromSeconds(cfg.GetValue("Push:AggregateWindowSeconds", 30));
    private readonly string _deepLinkBase = cfg["Push:DeepLinkBase"] ?? "https://liff.line.me/REPLACE_LIFF_ID";

    // 聚合：記每 (room,recipient) 最後推播時間，窗口內只推一次。
    private readonly ConcurrentDictionary<string, DateTime> _lastPush = new();

    public async Task OnNewMessageAsync(Message m, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var room = await db.Rooms.FindAsync([m.RoomId], ct);
        if (room is null) return;

        // 候選收件人：該房有效成員、排除發話人、只有客人且有 LineUserId 能推（導領走自家系統，不經 OA）
        var memberIds = await db.RoomMembers
            .Where(x => x.RoomId == m.RoomId && x.IsActive && x.ParticipantId != m.SenderParticipantId)
            .Select(x => x.ParticipantId).ToListAsync(ct);
        if (memberIds.Count == 0) return;

        var recipients = await db.Participants
            .Where(p => memberIds.Contains(p.ParticipantId) && p.Status == 0 && p.Kind == 0 && p.LineUserId != null)
            .ToListAsync(ct);
        if (recipients.Count == 0) return;

        // ── 分級：決定「哪些訊息該推、推給誰」──
        List<Participant> targets;
        var ignoreOnline = false;

        if (room.Type == 0) // 群聊
        {
            if (m.IsAnnouncement)
            {
                targets = recipients;        // 公告 → 一定推（不看在線與否）
                ignoreOnline = true;
            }
            else if (!string.IsNullOrEmpty(m.MentionParticipantIds))
            {
                var mentioned = ParseGuids(m.MentionParticipantIds);
                targets = recipients.Where(r => mentioned.Contains(r.ParticipantId)).ToList(); // 被@ → 推（離線才推）
            }
            else
            {
                return;                      // 一般群聊閒聊 → 不推（只即時顯示）
            }
        }
        else // 私訊 → 離線才推
        {
            targets = recipients;
        }
        if (targets.Count == 0) return;

        // ── 離線過濾（公告除外）──
        if (!ignoreOnline)
        {
            var online = (await mirror.GetOnlineParticipantsAsync(m.RoomId, _stale, ct)).ToHashSet();
            targets = targets.Where(t => !online.Contains(t.ParticipantId.ToString())).ToList();
        }
        if (targets.Count == 0) return;

        // ── 聚合：同房短時間內對同一人只推一次 ──
        var now = DateTime.UtcNow;
        var toPush = new List<Participant>();
        foreach (var t in targets)
        {
            var key = $"{m.RoomId}:{t.ParticipantId}";
            if (_lastPush.TryGetValue(key, out var last) && now - last < _aggWindow) continue;
            _lastPush[key] = now;
            toPush.Add(t);
        }
        if (toPush.Count == 0)
        {
            logger.LogInformation("[push] room={Room} 聚合視窗內，略過", m.RoomId);
            return;
        }

        var preview = Preview(m);
        var deeplink = $"{_deepLinkBase}?tourId={room.TourId}&roomId={m.RoomId}";
        var line = scope.ServiceProvider.GetRequiredService<LineMessagingClient>();
        var text = $"{preview}\n{deeplink}";

        // 填了 token → 真的呼叫 LINE Messaging API push；沒填 → 維持 log 模擬。
        foreach (var t in toPush)
        {
            logger.LogInformation("[push→{User}]（{Reason}）{Preview} link={Link}",
                t.LineUserId, Reason(room, m), preview, deeplink);
            if (line.Enabled)
                await line.PushTextAsync(t.LineUserId!, text, ct);
        }
    }

    private static string Reason(Room room, Message m) =>
        room.Type == 0 ? (m.IsAnnouncement ? "公告·一定推" : "群聊@·離線推") : "私訊·離線推";

    private static string Preview(Message m) => m.Type switch
    {
        1 => "[圖片]",
        2 => "[位置]",
        9 => "[系統訊息]",
        _ => m.Content.Length > 40 ? m.Content[..40] + "…" : m.Content,
    };

    private static HashSet<Guid> ParseGuids(string json)
    {
        try
        {
            var arr = JsonSerializer.Deserialize<List<Guid>>(json);
            return arr is null ? new() : arr.ToHashSet();
        }
        catch { return new(); }
    }
}
