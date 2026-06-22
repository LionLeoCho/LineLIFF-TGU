using LiffChat.Api.Services;

namespace LiffChat.Api.Endpoints;

// §F-1 外部契約：主系統 → LIFF 後端。系統對系統，不走 LIFF 客人認證。
// 認證：共享密鑰 header（正式可改 mTLS）。設定 Ingest:SharedKey；留空則開發放行。
public static class IngestEndpoints
{
    public const string KeyHeader = "X-Ingest-Key";

    public static void MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/tour-roster", async (
            RosterIngestRequest body,
            HttpContext http,
            RosterIngestService ingest,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            // 共享密鑰檢查
            var expected = cfg["Ingest:SharedKey"];
            if (!string.IsNullOrEmpty(expected))
            {
                var got = http.Request.Headers[KeyHeader].ToString();
                if (got != expected) return Results.Unauthorized();
            }

            var result = await ingest.IngestAsync(body, ct);
            // §I-2：即使版本過時被丟棄也回 200，避免主系統誤判失敗而重送舊包。
            return Results.Ok(result);
        });
    }
}
