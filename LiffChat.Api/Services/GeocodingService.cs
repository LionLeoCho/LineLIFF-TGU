using System.Text.Json;

namespace LiffChat.Api.Services;

// 反查地址（經緯度 → 文字地址），呼叫 Google Geocoding API。
// 設定 Geocoding:ApiKey；留空 → 不反查（只存座標，address 空字串）。
// key 只在後端使用，不曝露給前端。
public class GeocodingService(HttpClient http, IConfiguration cfg, ILogger<GeocodingService> logger)
{
    private readonly string? _key = cfg["Geocoding:ApiKey"];
    private readonly string _lang = cfg["Geocoding:Language"] ?? "zh-TW";
    public bool Enabled => !string.IsNullOrWhiteSpace(_key);

    public async Task<string> ReverseAsync(double lat, double lng, CancellationToken ct = default)
    {
        if (!Enabled) return "";
        try
        {
            var url = $"https://maps.googleapis.com/maps/api/geocode/json" +
                      $"?latlng={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"{lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&language={_lang}&key={_key}";

            using var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Geocoding HTTP {Status}", (int)res.StatusCode);
                return "";
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();
            if (status != "OK")
            {
                logger.LogWarning("Geocoding status {Status}", status);
                return "";
            }
            var results = root.GetProperty("results");
            if (results.GetArrayLength() == 0) return "";
            // 取第一筆 formatted_address（最完整）
            return results[0].GetProperty("formatted_address").GetString() ?? "";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Geocoding 反查失敗");
            return "";
        }
    }
}
