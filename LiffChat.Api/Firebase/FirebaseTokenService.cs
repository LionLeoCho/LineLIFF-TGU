using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;

namespace LiffChat.Api.Firebase;

// 規格 §I-1：客人用 LINE 登入，Firebase 不認得，需 .NET 用 Admin SDK 簽一張 custom token 當橋樑。
// uid = participantId（客人/導領一致），讓 Firestore security rules 直接以 request.auth.uid 比對成員。
// 前端拿到後 signInWithCustomToken() → 才能訂閱 Firestore。
//
// 簽發時機：併進 GET /me 與 POST /bind 回傳（不另開端點）。
public class FirebaseTokenService
{
    public FirebaseTokenService(IConfiguration cfg)
    {
        // 全程式僅初始化一次 FirebaseApp
        if (FirebaseApp.DefaultInstance is null)
        {
            var saPath = cfg["Firebase:ServiceAccountPath"]
                ?? throw new InvalidOperationException("缺少設定 Firebase:ServiceAccountPath（service account JSON 路徑）");

            FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromFile(saPath),
                ProjectId = cfg["Firebase:ProjectId"],
            });
        }
    }

    // 為某個 participant 簽 custom token；uid 即 participantId。
    public Task<string> CreateForParticipantAsync(Guid participantId, CancellationToken ct = default)
    {
        // 可選：塞 additional claims（如 tourId）供 rules 進一步判斷；首版用成員對照表即可，先不放。
        return FirebaseAuth.DefaultInstance.CreateCustomTokenAsync(
            participantId.ToString(), cancellationToken: ct);
    }
}
