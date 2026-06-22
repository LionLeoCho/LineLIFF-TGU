# LiffChat.Api — .NET 8 後端骨架

涵蓋 LIFF 出團聊天室的三支客人端核心端點 + Firebase custom token 簽發。
依工程規格的切割線：權限/綁定/驗證/權威保存留在 .NET，即時推送交給 Firestore。

## 三支端點

| 端點 | 做什麼 |
|---|---|
| `POST /api/tours/{tourId}/bind` | 驗 LIFF token → 取 userId → 比對名單(姓名+生日) → 建立/復用 participant（新蓋舊）→ 進群聊 → 回 `{ participantId, displayName, firebaseToken }` |
| `GET /api/tours/{tourId}/me` | 查此 userId 是否已綁 → 已綁順帶簽 custom token 回傳；未綁回 `{ bound:false }` |
| `POST /api/rooms/{roomId}/messages` | ★核心：驗權 → 寫 SQL（權威）→ 寫 Firestore（鏡像）→ 觸發推播 |

## 認證怎麼運作

客人端帶 `Authorization: Bearer {LIFF access token}`。
`LiffAuthHandler` → `LiffTokenVerifier`：先打 LINE `oauth2/v2.1/verify`
確認 token 屬於我們的 channel 且未過期，再打 `/v2/profile` 取 `userId`，
放進 `ClaimTypes.NameIdentifier`。短期快取避免每請求都打 LINE。

## Custom token（這次的重點）

`FirebaseTokenService.CreateForParticipantAsync()` 用 Admin SDK 簽
**uid = participantId** 的 custom token，併進 `/me` 與 `/bind` 回傳（§I-1，不另開端點）。
前端拿到後 `signInWithCustomToken()` → 才能訂閱 Firestore，
而 security rules 直接用 `request.auth.uid` 比對成員。

## 跑起來前要填（appsettings.json）

```
ConnectionStrings:Sql        SQL Server 連線字串
Liff:ChannelId               LINE Login channel ID（verify 比對用）
Firebase:ProjectId           Firebase 專案 ID
Firebase:ServiceAccountPath  service account JSON 路徑（Admin SDK / Firestore 共用）
```

```bash
dotnet restore
dotnet ef migrations add Init      # 建 schema（需先補齊其餘表）
dotnet ef database update
dotnet run
```

## 刻意留為 stub / TODO

- **PushService**：離線判斷、分級推、聚合視窗未實作（待 presence 門檻定案）。
- **導領端認證**：是另一個 scheme（導領系統 JWT），本骨架只做客人端。
- **群聊 room 誕生**、名單推送 ingest（§F-1）、退團處理、Announcements 表、排程作業：
  沿用相同風格補上。
- `LiffTokenVerifier` 的快取 key 用 `GetHashCode()` 為簡化；正式建議雜湊全字串。
- 唯讀判斷 `MessageService.IsRoomReadonlyAsync` 已涵蓋 §I-4 主要來源，
  換導領的舊導領私訊細節可再補。
