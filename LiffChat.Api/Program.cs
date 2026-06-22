using LiffChat.Api.Auth;
using LiffChat.Api.Data;
using LiffChat.Api.Endpoints;
using LiffChat.Api.Firebase;
using LiffChat.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- 資料層：SQL Server 權威庫 ---
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Sql")));

// --- 認證：可用 Auth:Mode 覆寫（"Dev" 假登入 / "Liff" 真 token）；未設則依環境 ---
// 真機測試：保持 Development（CORS 全開、dev 端點都在），只把 Auth:Mode 設 "Liff"。
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<LiffTokenVerifier>();

var authMode = builder.Configuration["Auth:Mode"]
    ?? (builder.Environment.IsDevelopment() ? "Dev" : "Liff");
var customerScheme = authMode == "Dev" ? DevAuthHandler.Scheme : LiffAuthHandler.Scheme;

var authBuilder = builder.Services.AddAuthentication(customerScheme);
if (authMode == "Dev")
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.Scheme, _ => { });
else
    authBuilder.AddScheme<AuthenticationSchemeOptions, LiffAuthHandler>(LiffAuthHandler.Scheme, _ => { });

// 導領 scheme：開發用假登入，正式用 token stub（與客人端分開）
if (builder.Environment.IsDevelopment())
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevLeaderAuthHandler>(LeaderAuth.Scheme, _ => { });
else
    authBuilder.AddScheme<AuthenticationSchemeOptions, LeaderTokenAuthHandler>(LeaderAuth.Scheme, _ => { });

builder.Services.AddAuthorization();

// --- CORS ---
// 開發：全開（含 file:// 的 "null" 來源），方便本機/內網開網頁測。
// 正式：只允許設定檔指定的前端來源（Cors:AllowedOrigins，逗號分隔）。
const string CorsPolicy = "frontend";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
{
    if (builder.Environment.IsDevelopment())
    {
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    }
    else
    {
        var origins = (builder.Configuration["Cors:AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    }
}));

// --- Firebase / Firestore ---
builder.Services.AddSingleton<FirebaseTokenService>();
builder.Services.AddSingleton<IFirestoreMirror, FirestoreMirror>();
builder.Services.AddSingleton<IPushService, PushService>();
builder.Services.AddHttpClient<LineMessagingClient>();

// --- 領域服務 ---
builder.Services.AddScoped<BindingService>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<RosterIngestService>();
builder.Services.AddScoped<MemberService>();
builder.Services.AddScoped<LeaderService>();
builder.Services.AddScoped<LeaderIngestService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddHostedService<SchedulerHostedService>();

var app = builder.Build();

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapChatEndpoints();
app.MapLeaderEndpoints();
app.MapIngestEndpoints();
app.MapLeaderIngestEndpoints();
app.MapGet("/health", () => Results.Ok("ok"));

// 開發/內網測試專用：一鍵塞測試資料
if (app.Environment.IsDevelopment())
{
    app.MapSeedEndpoints();
}

app.Run();
