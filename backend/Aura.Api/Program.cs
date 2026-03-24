using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api.Capture;
using Aura.Api.Capture.Adapters;
using Aura.Api.Data;
using Aura.Api.Ops;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var isDev = builder.Environment.IsDevelopment();
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "Aura.Api";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "Aura.Client";
var jwtExpireMinutes = int.TryParse(builder.Configuration["Jwt:ExpireMinutes"], out var m) ? m : 480;
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (!isDev) throw new InvalidOperationException("JWT Key 未配置（生产环境必须配置）");
    jwtKey = "aura-dev-jwt-key-please-change";
}
if (!isDev && (jwtKey.Contains("aura-dev-jwt-key-please-change", StringComparison.OrdinalIgnoreCase) || jwtKey.Contains("PLEASE_REPLACE", StringComparison.OrdinalIgnoreCase)))
{
    throw new InvalidOperationException("JWT Key 不能使用开发/占位默认值");
}

var globalHmacSecret = builder.Configuration["Security:HmacSecret"];
if (string.IsNullOrWhiteSpace(globalHmacSecret))
{
    if (!isDev) throw new InvalidOperationException("HMAC 密钥未配置（生产环境必须配置）");
    globalHmacSecret = "demo-hmac-secret";
}
if (!isDev && globalHmacSecret.Contains("demo-hmac-secret", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("HMAC 密钥不能使用开发默认值");
}

var captureIpWhitelist = builder.Configuration.GetSection("Security:CaptureIpWhitelist").Get<string[]>();

const int MaxImageBase64Chars = 5_000_000;
const int MaxMetadataJsonChars = 200_000;
const long MaxCaptureRequestBytes = 12L * 1024 * 1024;
var mysqlConn = builder.Configuration.GetConnectionString("MySql") ?? "";
var redisConn = builder.Configuration.GetConnectionString("Redis") ?? "";
var aiBaseUrl = builder.Configuration["Ai:BaseUrl"] ?? "http://127.0.0.1:8000";
var alertWebhookUrl = builder.Configuration["Ops:Alert:WebhookUrl"];
var alertNotifyFilePath = builder.Configuration["Ops:Alert:FilePath"];
var alertHealthFailWindowMinutes = int.TryParse(builder.Configuration["Ops:Alert:HealthFailIfRecentFailureMinutes"], out var alertWindowMinutes)
    ? Math.Max(0, alertWindowMinutes)
    : 10;
var cspPolicy = builder.Configuration["Security:CspPolicy"]
    ?? "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss:;";
if (!isDev)
{
    if (string.IsNullOrWhiteSpace(mysqlConn)
        || mysqlConn.Contains("PLEASE_SET_CONNECTIONSTRING_MYSQL", StringComparison.OrdinalIgnoreCase)
        || mysqlConn.Contains("PLEASE_REPLACE", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("MySQL 连接串未配置或仍为占位值（生产环境必须配置有效连接串）");
    }
    if (mysqlConn.Contains("AllowPublicKeyRetrieval=True", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("生产环境禁止 MySQL 连接串使用 AllowPublicKeyRetrieval=True");
    }
    if (string.IsNullOrWhiteSpace(redisConn)
        || redisConn.Contains("PLEASE_SET_CONNECTIONSTRING_REDIS", StringComparison.OrdinalIgnoreCase)
        || redisConn.Contains("PLEASE_REPLACE", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Redis 连接串未配置或仍为占位值（生产环境必须配置有效连接串）");
    }
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddOpenApi();
builder.Services.AddSingleton<MySqlStore>(sp =>
    new MySqlStore(mysqlConn, sp.GetRequiredService<ILogger<MySqlStore>>()));
builder.Services.AddSingleton<RedisCacheService>(sp =>
    new RedisCacheService(redisConn, sp.GetRequiredService<ILogger<RedisCacheService>>()));
builder.Services.AddSingleton<RetryQueueService>(sp =>
    new RetryQueueService(redisConn, sp.GetRequiredService<ILogger<RetryQueueService>>()));
builder.Services.AddSingleton<IAlertNotifier>(sp =>
    new AlertNotifier(
        new HttpClient(),
        sp.GetRequiredService<ILogger<AlertNotifier>>(),
        alertWebhookUrl,
        alertNotifyFilePath));
builder.Services.AddSingleton<DailyJudgeScheduleState>();
builder.Services.AddHostedService<DailyJudgeHostedService>();
builder.Services.AddSignalR();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // SignalR WebSocket 场景下通常不会带 Authorization 头，改为从 querystring 的 access_token 读取
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrWhiteSpace(context.Token))
                {
                    var cookieToken = context.Request.Cookies["aura_token"];
                    if (!string.IsNullOrWhiteSpace(cookieToken))
                    {
                        context.Token = cookieToken;
                    }
                }

                var path = context.HttpContext.Request.Path;
                if (string.IsNullOrWhiteSpace(context.Token)
                    && path.StartsWithSegments("/hubs/events", StringComparison.OrdinalIgnoreCase))
                {
                    var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        context.Token = accessToken;
                    }
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("超级管理员", policy => policy.RequireRole("super_admin"));
    options.AddPolicy("楼栋管理员", policy => policy.RequireRole("building_admin", "super_admin"));
});

var app = builder.Build();
var store = new AppStore();
var db = app.Services.GetRequiredService<MySqlStore>();
var cache = app.Services.GetRequiredService<RedisCacheService>();
var retryQueue = app.Services.GetRequiredService<RetryQueueService>();
var hikAdapter = new HikvisionIsapiAdapter();
var onvifAdapter = new OnvifAdapter();
var cppSdkAdapter = new CppSdkAdapter();
var aiClient = new AiClient(new HttpClient(), aiBaseUrl);
var alertNotifier = app.Services.GetRequiredService<IAlertNotifier>();
var hubContext = app.Services.GetRequiredService<IHubContext<EventHub>>();
var dailyJudgeState = app.Services.GetRequiredService<DailyJudgeScheduleState>();
var projectRoot = Directory.GetParent(app.Environment.ContentRootPath)?.Parent?.FullName ?? app.Environment.ContentRootPath;
var storageRoot = Path.Combine(projectRoot, "storage");
var frontendRoot = Path.Combine(projectRoot, "frontend");
var captureRetryRootCfg = app.Configuration["Storage:CaptureRetryRoot"]?.Trim();
var captureRetryPreferInlineBase64 = app.Configuration.GetValue("CaptureRetry:PreferInlineBase64", false);
var captureRetryAllowInlineFallback = app.Configuration.GetValue<bool?>("CaptureRetry:AllowInlineBase64Fallback")
    ?? app.Environment.IsDevelopment();
var saveCaptureImageOnSuccess = app.Configuration.GetValue("Storage:SaveCaptureImageOnSuccess", true);
var captureRetryImageFolder = string.IsNullOrWhiteSpace(captureRetryRootCfg)
    ? Path.Combine(storageRoot, "uploads", "capture-retry")
    : Path.GetFullPath(captureRetryRootCfg);
Directory.CreateDirectory(storageRoot);
Directory.CreateDirectory(Path.Combine(storageRoot, "uploads"));
Directory.CreateDirectory(Path.Combine(storageRoot, "uploads", "floors"));
Directory.CreateDirectory(captureRetryImageFolder);
Directory.CreateDirectory(Path.Combine(storageRoot, "outputs"));
if (captureRetryPreferInlineBase64)
{
    Console.WriteLine("已启用 CaptureRetry:PreferInlineBase64：AI 失败时抓拍图仅入 Redis 队列不落盘，适用于多实例无共享卷（请关注队列大小与内存）。");
}
if (!captureRetryAllowInlineFallback)
{
    Console.WriteLine("已禁用 CaptureRetry:AllowInlineBase64Fallback：AI 失败且图片落盘失败时不再写入内联 Base64 队列，避免大对象放大。");
}

// 开发环境：防止删除默认账号后“无法登录”的功能性回退。
// 若 sys_user 为空，则自动创建一个随机强密码的开发管理员账号（仅打印到本机控制台）。
if (app.Environment.IsDevelopment())
{
    try
    {
        var devResetAdminPasswordOnce = app.Configuration.GetValue("Dev:ResetAdminPasswordOnce", false);
        bool TryDisableDevResetPasswordOnceFlag()
        {
            try
            {
                var devConfigPath = Path.Combine(app.Environment.ContentRootPath, "appsettings.Development.json");
                if (!File.Exists(devConfigPath)) return false;

                var json = File.ReadAllText(devConfigPath);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root is null) return false;

                var devNode = root["Dev"] as JsonObject ?? new JsonObject();
                devNode["ResetAdminPasswordOnce"] = false;
                root["Dev"] = devNode;

                File.WriteAllText(
                    devConfigPath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string GenerateDevPassword()
        {
            var bytes = RandomNumberGenerator.GetBytes(18);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        var users = await db.GetUsersAsync();
        if (users.Count == 0)
        {
            var devPassword = GenerateDevPassword();
            var hash = BCrypt.Net.BCrypt.HashPassword(devPassword);
            var insertedId = await db.InsertUserAsync("admin", hash, 1);
            if (insertedId.HasValue)
            {
                Console.WriteLine($"开发环境管理员已自动创建：用户名=admin, 密码={devPassword}");
            }
        }
        else if (devResetAdminPasswordOnce)
        {
            var devPassword = GenerateDevPassword();
            var hash = BCrypt.Net.BCrypt.HashPassword(devPassword);
            var resetOk = await db.UpdateUserPasswordByUserNameAsync("admin", hash);
            if (!resetOk)
            {
                // admin 不存在时补建，避免开发环境因账号状态异常无法登录
                var insertedId = await db.InsertUserAsync("admin", hash, 1);
                if (insertedId.HasValue)
                {
                    Console.WriteLine($"开发环境管理员不存在，已自动重建：用户名=admin, 密码={devPassword}");
                }
            }
            else
            {
                Console.WriteLine($"开发环境管理员密码已一次性重置：用户名=admin, 新密码={devPassword}");
            }

            if (TryDisableDevResetPasswordOnceFlag())
            {
                Console.WriteLine("开发环境一次性重置开关已自动回写为 false。");
            }
            else
            {
                Console.WriteLine("提示：自动回写 Dev:ResetAdminPasswordOnce=false 失败，请手动改回 false。");
            }
        }
    }
    catch
    {
        // 自动创建失败不影响生产逻辑；仅用于开发可用性兜底
    }
}

static string SanitizeRateKeySegment(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "unknown";
    var s = value.Replace(':', '_').Replace('\r', '_').Replace('\n', '_').Trim();
    return s.Length <= 128 ? s : s[..128];
}

/// <param name="explicitDimension">抓拍等匿名接口传入设备 ID 等，避免 NAT 下同 IP 误伤；已登录接口可省略，将按 JWT 主体或 IP 计数。</param>
async Task<IResult?> CheckRateLimitAsync(HttpRequest request, string bucket, long limit, TimeSpan window, string? explicitDimension = null)
{
    if (!cache.Enabled) return null;

    string segment;
    if (!string.IsNullOrWhiteSpace(explicitDimension))
        segment = "d:" + SanitizeRateKeySegment(explicitDimension);
    else
    {
        var user = request.HttpContext.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var sub = user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? user.FindFirstValue(ClaimTypes.Name);
            segment = "u:" + SanitizeRateKeySegment(sub);
        }
        else
        {
            var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            segment = "ip:" + SanitizeRateKeySegment(ip);
        }
    }

    var key = $"aura:rl:{bucket}:{segment}";
    var count = await cache.TryConsumeFixedWindowAsync(key, window, limit);
    if (!count.HasValue) return null;
    if (count.Value > limit)
        return Results.Json(new { code = 42901, msg = "请求过多，请稍后再试" }, statusCode: 429);
    return null;
}

Task BroadcastEventAsync(string eventName, object payload)
{
    // 统一通过角色分组推送，避免无差别 All 广播
    return hubContext.Clients.Groups("role:building_admin", "role:super_admin").SendAsync(eventName, payload);
}

Task NotifyAlertAsync(string alertType, string detail, string source)
{
    return alertNotifier.NotifyAsync(new AlertNotifyMessage(alertType, detail, source, DateTimeOffset.Now));
}

static string? TryExtractPureBase64(string imageBase64)
{
    if (string.IsNullOrWhiteSpace(imageBase64)) return null;

    // 支持 data URL：data:image/jpeg;base64,AAAA...
    var idx = imageBase64.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
    if (idx >= 0)
    {
        var start = idx + "base64,".Length;
        return imageBase64[start..];
    }

    var comma = imageBase64.IndexOf(',');
    if (imageBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
    {
        return imageBase64[(comma + 1)..];
    }

    // 纯 base64：直接使用
    return imageBase64;
}

static string? ToPublicStorageUrl(string? storageRootPath, string? localPath)
{
    if (string.IsNullOrWhiteSpace(localPath) || string.IsNullOrWhiteSpace(storageRootPath))
        return null;

    try
    {
        var fullRoot = Path.GetFullPath(storageRootPath);
        var fullLocal = Path.GetFullPath(localPath);
        if (!fullLocal.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var rel = Path.GetRelativePath(fullRoot, fullLocal).Replace('\\', '/');
        return $"/storage/{rel}";
    }
    catch
    {
        return null;
    }
}

async Task<string?> SaveRetryImageAsync(string imageBase64)
{
    // 多实例无共享盘时配置 PreferInlineBase64，仅走队列内 base64，避免落盘路径不可读
    if (captureRetryPreferInlineBase64) return null;

    var pure = TryExtractPureBase64(imageBase64);
    if (string.IsNullOrWhiteSpace(pure)) return null;

    byte[] bytes;
    try
    {
        bytes = Convert.FromBase64String(pure);
    }
    catch
    {
        return null;
    }

    // 限制落盘文件大小，防止异常 base64 导致磁盘被打爆
    const long MaxImageBytes = 10L * 1024 * 1024;
    if (bytes.Length <= 0 || bytes.Length > MaxImageBytes) return null;

    var folder = captureRetryImageFolder;
    Directory.CreateDirectory(folder);
    var safeName = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}.bin";
    var localPath = Path.Combine(folder, safeName);
    await File.WriteAllBytesAsync(localPath, bytes);
    return localPath;
}

async Task<string?> SaveCaptureArchiveImageAsync(long deviceId, DateTimeOffset captureTime, string imageBase64)
{
    var pure = TryExtractPureBase64(imageBase64);
    if (string.IsNullOrWhiteSpace(pure)) return null;

    byte[] bytes;
    try
    {
        bytes = Convert.FromBase64String(pure);
    }
    catch
    {
        return null;
    }

    const long MaxImageBytes = 10L * 1024 * 1024;
    if (bytes.Length <= 0 || bytes.Length > MaxImageBytes) return null;

    var day = captureTime.ToString("yyyyMMdd");
    var folder = Path.Combine(storageRoot, "uploads", "capture", deviceId.ToString(), day);
    Directory.CreateDirectory(folder);
    var safeName = $"{captureTime:HHmmss}_{Guid.NewGuid():N}.bin";
    var localPath = Path.Combine(folder, safeName);
    await File.WriteAllBytesAsync(localPath, bytes);
    return ToPublicStorageUrl(storageRoot, localPath);
}

async Task<IResult> SaveCaptureAsync(CapturePayload normalized, string source)
{
    string? captureImagePathForDb = null; // 仅在 AI 失败时写入；成功时保持为空即可减少磁盘 IO
    string? retryImagePath = await SaveRetryImageAsync(normalized.ImageBase64); // 优先落盘，主链路 AI 可按文件读取
    string? retryImageBase64ForQueue = null; // 当落盘失败/未启用落盘时兜底使用 base64
    var shouldEnqueueRetry = false;

    var aiResult = !string.IsNullOrWhiteSpace(retryImagePath)
        ? await aiClient.ExtractByPathAsync(retryImagePath, normalized.MetadataJson)
        : await aiClient.ExtractAsync(normalized.ImageBase64, normalized.MetadataJson);
    if (!aiResult.Success)
    {
        captureImagePathForDb = ToPublicStorageUrl(storageRoot, retryImagePath);
        if (!string.IsNullOrWhiteSpace(retryImagePath))
        {
            shouldEnqueueRetry = true;
            retryImageBase64ForQueue = null;
        }
        else if (captureRetryAllowInlineFallback)
        {
            // 仅在显式允许时回退到内联 Base64（默认生产禁用）
            shouldEnqueueRetry = true;
            retryImageBase64ForQueue = normalized.ImageBase64;
        }
        else
        {
            shouldEnqueueRetry = false;
        }
    }
    else if (saveCaptureImageOnSuccess)
    {
        // AI 成功：若已存在落盘文件，直接复用路径；否则按归档策略再落盘
        captureImagePathForDb = ToPublicStorageUrl(storageRoot, retryImagePath)
                                ?? await SaveCaptureArchiveImageAsync(normalized.DeviceId, normalized.CaptureTime, normalized.ImageBase64);
    }
    else
    {
        // AI 成功且不保留图片时，清理临时文件
        if (!string.IsNullOrWhiteSpace(retryImagePath))
        {
            try
            {
                if (File.Exists(retryImagePath)) File.Delete(retryImagePath);
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }
    }

    var metadata = AttachAiResult(normalized.MetadataJson, aiResult);
    var record = new CaptureEntity(
        Interlocked.Increment(ref store.CaptureSeed),
        normalized.DeviceId,
        normalized.ChannelNo,
        normalized.CaptureTime,
        metadata);
    var dbId = await db.InsertCaptureAsync(record.DeviceId, record.ChannelNo, record.CaptureTime, record.MetadataJson, captureImagePathForDb);
    var saved = dbId.HasValue ? record with { CaptureId = dbId.Value } : record;
    if (!dbId.HasValue) store.Captures.Add(saved);
    if (aiResult.Success && aiResult.Feature.Count > 0)
    {
        var vectorId = $"C_{saved.CaptureId}";
        await aiClient.UpsertAsync(vectorId, aiResult.Feature);
    }
    await db.InsertOperationAsync("采集网关", source, $"设备={normalized.DeviceId}, 通道={normalized.ChannelNo}, AI={aiResult.Message}");
    AddOperationLog(store, "采集网关", source, $"设备={normalized.DeviceId}, 通道={normalized.ChannelNo}, AI={aiResult.Message}");
    await BroadcastEventAsync("capture.received", new { saved.CaptureId, saved.DeviceId, saved.ChannelNo, saved.CaptureTime, source });
    if (!aiResult.Success && shouldEnqueueRetry)
    {
        // AI 失败时：将 capture_id 作为幂等锚点，确保重试成功可回写向量与元数据
        await retryQueue.EnqueueAsync(new RetryTask(
            saved.CaptureId,
            normalized.DeviceId,
            normalized.ChannelNo,
            retryImagePath,
            retryImageBase64ForQueue,
            normalized.MetadataJson,
            source,
            0,
            DateTimeOffset.Now));
    }
    else if (!aiResult.Success && !shouldEnqueueRetry)
    {
        await db.InsertOperationAsync("重试任务", "AI重试入队已跳过", $"captureId={saved.CaptureId}, 原因=图片落盘失败且禁止内联Base64回退");
    }
    if (!string.IsNullOrWhiteSpace(normalized.MetadataJson) && normalized.MetadataJson.Contains("异常"))
    {
        var a = new AlertEntity(Interlocked.Increment(ref store.AlertSeed), "异常滞留", $"抓拍记录{saved.CaptureId}命中异常关键词", DateTimeOffset.Now);
        var aid = await db.InsertAlertAsync(a.AlertType, a.Detail);
        if (!aid.HasValue) store.Alerts.Add(a);
        await NotifyAlertAsync(a.AlertType, a.Detail, "抓拍关键词命中");
        await BroadcastEventAsync("alert.created", new { alertType = a.AlertType, detail = a.Detail, at = a.CreatedAt });
    }
    return Results.Ok(new { code = 0, msg = $"{source}接收成功", data = saved });
}

async Task<JudgeRunResult> RunHomeJudgeAsync(DateOnly judgeDate)
{
    await db.DeleteJudgeResultsByDateAsync(judgeDate, "home_room");
    var start = judgeDate.ToDateTime(TimeOnly.MinValue);
    var end = start.AddDays(1);
    var events = await db.GetTrackEventsInRangeAsync(start, end);
    if (events.Count == 0)
    {
        return new JudgeRunResult(judgeDate, "home_room", 0, 0);
    }
    var roisDb = await db.GetRoisAsync();
    var roiMap = roisDb.ToDictionary(x => x.RoiId, x => x.RoomNodeId);
    var saveCount = 0;
    foreach (var g in events.GroupBy(x => x.Vid))
    {
        var roomAgg = g
            .Where(x => roiMap.ContainsKey(x.RoiId))
            .GroupBy(x => roiMap[x.RoiId])
            .Select(x => new
            {
                RoomId = x.Key,
                Count = x.Count(),
                Last = x.Max(e => e.EventTime)
            })
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Last)
            .FirstOrDefault();
        if (roomAgg is null) continue;
        var detail = JsonSerializer.Serialize(new { roomAgg.Count, roomAgg.Last });
        var id = await db.InsertJudgeResultAsync(g.Key, roomAgg.RoomId, "home_room", judgeDate, detail);
        if (id.HasValue) saveCount++;
        else
        {
            store.JudgeResults.Add(new JudgeResultEntity(Interlocked.Increment(ref store.JudgeSeed), g.Key, roomAgg.RoomId, "home_room", judgeDate, detail, DateTimeOffset.Now));
            saveCount++;
        }
    }
    await db.InsertOperationAsync("系统任务", "归寝研判", $"日期={judgeDate:yyyy-MM-dd}, 结果={saveCount}");
    return new JudgeRunResult(judgeDate, "home_room", events.Count, saveCount);
}

async Task<JudgeRunResult> RunGroupRentAndStayJudgeAsync(DateOnly judgeDate, int groupThreshold, int stayMinutes)
{
    await db.DeleteJudgeResultsByDateAsync(judgeDate, "group_rent");
    await db.DeleteJudgeResultsByDateAsync(judgeDate, "abnormal_stay");
    var start = judgeDate.ToDateTime(TimeOnly.MinValue);
    var end = start.AddDays(1);
    var events = await db.GetTrackEventsInRangeAsync(start, end);
    if (events.Count == 0) return new JudgeRunResult(judgeDate, "group_rent+abnormal_stay", 0, 0);
    var roisDb = await db.GetRoisAsync();
    var roiMap = roisDb.ToDictionary(x => x.RoiId, x => x.RoomNodeId);
    var eventWithRoom = events.Where(x => roiMap.ContainsKey(x.RoiId)).Select(x => new { x.Vid, RoomId = roiMap[x.RoiId], x.EventTime }).ToList();
    var saveCount = 0;

    foreach (var room in eventWithRoom.GroupBy(x => x.RoomId))
    {
        var distinctVid = room.Select(x => x.Vid).Distinct().ToArray();
        if (distinctVid.Length >= groupThreshold)
        {
            var detail = JsonSerializer.Serialize(new { distinctVidCount = distinctVid.Length, vids = distinctVid });
            var id = await db.InsertJudgeResultAsync($"ROOM_{room.Key}", room.Key, "group_rent", judgeDate, detail);
            if (id.HasValue) saveCount++;
            var aid = await db.InsertAlertAsync("群租预警", $"房间={room.Key}, 人数={distinctVid.Length}");
            if (!aid.HasValue) store.Alerts.Add(new AlertEntity(Interlocked.Increment(ref store.AlertSeed), "群租预警", $"房间={room.Key}, 人数={distinctVid.Length}", DateTimeOffset.Now));
            await NotifyAlertAsync("群租预警", $"房间={room.Key}, 人数={distinctVid.Length}", "群租研判");
            await BroadcastEventAsync("alert.created", new { alertType = "群租预警", roomId = room.Key, count = distinctVid.Length, date = judgeDate });
        }
    }

    foreach (var personRoom in eventWithRoom.GroupBy(x => new { x.Vid, x.RoomId }))
    {
        var first = personRoom.Min(x => x.EventTime);
        var last = personRoom.Max(x => x.EventTime);
        var minutes = (last - first).TotalMinutes;
        if (minutes >= stayMinutes)
        {
            var detail = JsonSerializer.Serialize(new { stayMinutes = minutes, first, last });
            var id = await db.InsertJudgeResultAsync(personRoom.Key.Vid, personRoom.Key.RoomId, "abnormal_stay", judgeDate, detail);
            if (id.HasValue) saveCount++;
            var aid = await db.InsertAlertAsync("异常滞留", $"VID={personRoom.Key.Vid}, 房间={personRoom.Key.RoomId}, 分钟={Math.Round(minutes, 1)}");
            if (!aid.HasValue) store.Alerts.Add(new AlertEntity(Interlocked.Increment(ref store.AlertSeed), "异常滞留", $"VID={personRoom.Key.Vid}, 房间={personRoom.Key.RoomId}, 分钟={Math.Round(minutes, 1)}", DateTimeOffset.Now));
            await NotifyAlertAsync("异常滞留", $"VID={personRoom.Key.Vid}, 房间={personRoom.Key.RoomId}, 分钟={Math.Round(minutes, 1)}", "滞留研判");
        }
    }
    await db.InsertOperationAsync("系统任务", "群租/滞留研判", $"日期={judgeDate:yyyy-MM-dd}, 结果={saveCount}");
    return new JudgeRunResult(judgeDate, "group_rent+abnormal_stay", events.Count, saveCount);
}

async Task<JudgeRunResult> RunNightAbsenceJudgeAsync(DateOnly judgeDate, int cutoffHour)
{
    await db.DeleteJudgeResultsByDateAsync(judgeDate, "night_absence");
    var homeRows = await db.GetJudgeResultsAsync(judgeDate, "home_room");
    var home = homeRows.Count > 0
        ? homeRows.Select(x => new JudgeResultEntity(x.JudgeId, x.Vid, x.RoomId, x.JudgeType, DateOnly.FromDateTime(x.JudgeDate), x.DetailJson, x.CreatedAt)).ToList()
        : store.JudgeResults.Where(x => x.JudgeDate == judgeDate && x.JudgeType == "home_room").ToList();
    if (home.Count == 0) return new JudgeRunResult(judgeDate, "night_absence", 0, 0);
    var start = judgeDate.ToDateTime(new TimeOnly(cutoffHour, 0));
    var end = start.AddHours(8);
    var eventsDb = await db.GetTrackEventsInRangeAsync(start, end);
    var roisDb = await db.GetRoisAsync();
    var roiMap = roisDb.ToDictionary(x => x.RoiId, x => x.RoomNodeId);
    var events = eventsDb.Where(x => roiMap.ContainsKey(x.RoiId)).ToList();
    var existedPairs = events
        .Select(x => (Vid: x.Vid, RoomId: roiMap[x.RoiId]))
        .ToHashSet();
    var saveCount = 0;
    foreach (var row in home)
    {
        if (existedPairs.Contains((row.Vid, row.RoomId))) continue;
        var detail = JsonSerializer.Serialize(new { cutoffHour, message = "截止时间后未回到归属房间" });
        var id = await db.InsertJudgeResultAsync(row.Vid, row.RoomId, "night_absence", judgeDate, detail);
        if (id.HasValue) saveCount++;
        var aid = await db.InsertAlertAsync("夜不归宿", $"VID={row.Vid}, 房间={row.RoomId}, 日期={judgeDate:yyyy-MM-dd}");
        if (!aid.HasValue) store.Alerts.Add(new AlertEntity(Interlocked.Increment(ref store.AlertSeed), "夜不归宿", $"VID={row.Vid}, 房间={row.RoomId}, 日期={judgeDate:yyyy-MM-dd}", DateTimeOffset.Now));
        await NotifyAlertAsync("夜不归宿", $"VID={row.Vid}, 房间={row.RoomId}, 日期={judgeDate:yyyy-MM-dd}", "夜不归宿研判");
        await BroadcastEventAsync("alert.created", new { alertType = "夜不归宿", vid = row.Vid, roomId = row.RoomId, date = judgeDate });
    }
    await db.InsertOperationAsync("系统任务", "夜不归宿研判", $"日期={judgeDate:yyyy-MM-dd}, 结果={saveCount}");
    return new JudgeRunResult(judgeDate, "night_absence", home.Count, saveCount);
}

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseHttpsRedirection();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = cspPolicy;
    await next();
});
if (Directory.Exists(frontendRoot))
{
    app.Use(async (context, next) =>
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            var path = context.Request.Path.Value ?? "/";
            var isReserved = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith("/storage", StringComparison.OrdinalIgnoreCase);
            if (!isReserved && !Path.HasExtension(path))
            {
                var isLoginPath = path.Equals("/login", StringComparison.OrdinalIgnoreCase)
                                  || path.Equals("/login/", StringComparison.OrdinalIgnoreCase);
                if (!isLoginPath)
                {
                    var token = context.Request.Cookies["aura_token"];
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        var returnUrl = Uri.EscapeDataString(path + context.Request.QueryString);
                        context.Response.Redirect($"/login/?returnUrl={returnUrl}", permanent: false);
                        return;
                    }
                }

                var seg = path.Trim('/');
                if (!string.IsNullOrWhiteSpace(seg))
                {
                    var htmlFile = Path.Combine(frontendRoot, seg, $"{seg}.html");
                    if (File.Exists(htmlFile))
                    {
                        // 统一补齐结尾斜杠，确保页面内 ./css ./js 相对路径正确解析
                        if (!path.EndsWith("/", StringComparison.Ordinal))
                        {
                            context.Response.Redirect($"{path}/", permanent: false);
                            return;
                        }
                        context.Request.Path = $"/{seg}/{seg}.html";
                    }
                }
            }
        }
        await next();
    });
}
if (Directory.Exists(frontendRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(frontendRoot),
        RequestPath = ""
    });
}
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(storageRoot),
    RequestPath = "/storage"
});
app.UseAuthentication();
app.UseAuthorization();
app.MapHub<EventHub>("/hubs/events");

app.MapGet("/", () => Results.Redirect("/index/"));
app.MapGet("/api/health", () => Results.Ok(new { code = 0, msg = "寓瞳中枢服务运行正常", time = DateTimeOffset.Now }));
app.MapGet("/api/ops/readiness", () =>
{
    var now = DateTimeOffset.Now;
    var jwtConfigured = !string.IsNullOrWhiteSpace(jwtKey)
                        && !jwtKey.Contains("PLEASE_", StringComparison.OrdinalIgnoreCase)
                        && !jwtKey.Contains("aura-dev-jwt-key-please-change", StringComparison.OrdinalIgnoreCase);
    var hmacConfigured = !string.IsNullOrWhiteSpace(globalHmacSecret)
                         && !globalHmacSecret.Contains("PLEASE_", StringComparison.OrdinalIgnoreCase)
                         && !globalHmacSecret.Contains("demo-hmac-secret", StringComparison.OrdinalIgnoreCase);
    var mysqlConfigured = !string.IsNullOrWhiteSpace(mysqlConn)
                          && !mysqlConn.Contains("PLEASE_", StringComparison.OrdinalIgnoreCase);
    var redisConfigured = !string.IsNullOrWhiteSpace(redisConn)
                          && !redisConn.Contains("PLEASE_", StringComparison.OrdinalIgnoreCase);
    var aiConfigured = !string.IsNullOrWhiteSpace(aiBaseUrl);
    var alertStats = alertNotifier.GetStats();
    var alertRecentWindowStart = now.AddMinutes(-alertHealthFailWindowMinutes);
    var hasRecentFailure = alertHealthFailWindowMinutes > 0
        && alertStats.LastFailureAt.HasValue
        && alertStats.LastFailureAt.Value >= alertRecentWindowStart;
    var alertNotifyHealthy = !hasRecentFailure;

    var checks = new Dictionary<string, bool>
    {
        ["jwt"] = jwtConfigured,
        ["hmac"] = hmacConfigured,
        ["mysql"] = mysqlConfigured,
        ["redis"] = redisConfigured,
        ["ai"] = aiConfigured,
        ["alertNotify"] = alertNotifyHealthy
    };
    var ready = checks.Values.All(x => x);
    return Results.Ok(new
    {
        code = 0,
        msg = ready ? "就绪检查通过" : "就绪检查未通过",
        data = new
        {
            environment = app.Environment.EnvironmentName,
            ready,
            checks,
            alertNotify = new
            {
                healthFailIfRecentFailureMinutes = alertHealthFailWindowMinutes,
                hasRecentFailure,
                recentWindowStart = alertRecentWindowStart,
                stats = alertStats
            }
        },
        time = now
    });
}).RequireAuthorization("超级管理员");
app.MapPost("/api/ops/alert-notify-test", async (OpsAlertNotifyTestReq req) =>
{
    var alertType = string.IsNullOrWhiteSpace(req.AlertType) ? "运维自检" : req.AlertType.Trim();
    var detail = string.IsNullOrWhiteSpace(req.Detail)
        ? "告警通知通道自检消息"
        : req.Detail.Trim();
    await alertNotifier.NotifyAsync(new AlertNotifyMessage(alertType, detail, "ops.alert-notify-test", DateTimeOffset.Now));
    await db.InsertOperationAsync("系统管理员", "告警通知自检", $"类型={alertType}");
    return Results.Ok(new { code = 0, msg = "告警通知自检已发送", data = new { alertType, detail, at = DateTimeOffset.Now } });
}).RequireAuthorization("超级管理员");
app.MapGet("/api/ops/alert-notify-stats", () =>
{
    var stats = alertNotifier.GetStats();
    return Results.Ok(new
    {
        code = 0,
        msg = "获取告警通知统计成功",
        data = stats,
        time = DateTimeOffset.Now
    });
}).RequireAuthorization("超级管理员");

var auth = app.MapGroup("/api/auth");
auth.MapPost("/login", async (HttpContext http, LoginReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { code = 40001, msg = "用户名或密码不能为空" });

    var dbUser = await db.FindUserAsync(req.UserName);
    if (dbUser is not null)
    {
        if (!BCrypt.Net.BCrypt.Verify(req.Password, dbUser.PasswordHash))
            return Results.BadRequest(new { code = 40003, msg = "用户名或密码错误" });
        var roleDb = ConvertRole(dbUser.RoleName);
        var tokenDb = BuildJwtToken(req.UserName, roleDb, jwtKey, jwtIssuer, jwtAudience, jwtExpireMinutes);
        http.Response.Cookies.Append("aura_token", tokenDb, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddMinutes(jwtExpireMinutes)
        });
        return Results.Ok(new { code = 0, msg = "登录成功", data = new { token = tokenDb, expireAt = DateTimeOffset.Now.AddMinutes(jwtExpireMinutes), userName = req.UserName, role = roleDb } });
    }

    return Results.BadRequest(new { code = 40003, msg = "用户名或密码错误" });
});
auth.MapPost("/logout", (HttpContext http) =>
{
    http.Response.Cookies.Append("aura_token", "", new CookieOptions
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = DateTimeOffset.UnixEpoch
    });
    return Results.Ok(new { code = 0, msg = "已退出登录" });
});
auth.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { code = 0, msg = "查询成功", data = new { userName = user.Identity?.Name ?? "unknown", role = user.FindFirst(ClaimTypes.Role)?.Value ?? "none" } })).RequireAuthorization();

var role = app.MapGroup("/api/role");
role.MapGet("/list", async () =>
{
    var cached = await cache.GetAsync("role:list");
    if (!string.IsNullOrWhiteSpace(cached))
    {
        var cacheRows = JsonSerializer.Deserialize<List<DbRole>>(cached);
        if (cacheRows is { Count: > 0 })
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = cacheRows, from = "redis" });
        }
    }

    var rows = await db.GetRolesAsync();
    if (rows.Count > 0)
    {
        await cache.SetAsync("role:list", JsonSerializer.Serialize(rows), TimeSpan.FromMinutes(5));
        return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
    }
    return Results.Ok(new
    {
        code = 0,
        msg = "查询成功",
        data = store.Roles.OrderByDescending(x => x.RoleId)
    });
}).RequireAuthorization("超级管理员");
role.MapPost("/create", async (RoleCreateReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.RoleName))
    {
        return Results.BadRequest(new { code = 40011, msg = "角色名不能为空" });
    }
    var permissionJson = req.PermissionJson ?? "[]";
    var dbId = await db.InsertRoleAsync(req.RoleName, permissionJson);
    if (dbId.HasValue)
    {
        await db.InsertOperationAsync("系统管理员", "角色创建", $"角色={req.RoleName}");
        return Results.Ok(new
        {
            code = 0,
            msg = "创建成功",
            data = new { roleId = dbId.Value, roleName = req.RoleName, permissionJson }
        });
    }

    var entity = new RoleEntity(Interlocked.Increment(ref store.RoleSeed), req.RoleName, permissionJson);
    store.Roles.Add(entity);
    AddOperationLog(store, "系统管理员", "角色创建", $"角色={req.RoleName}");
    return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
}).RequireAuthorization("超级管理员");

var user = app.MapGroup("/api/user");
user.MapGet("/list", async () =>
{
    var cached = await cache.GetAsync("user:list");
    if (!string.IsNullOrWhiteSpace(cached))
    {
        var cacheRows = JsonSerializer.Deserialize<List<DbUserListItem>>(cached);
        if (cacheRows is { Count: > 0 })
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = cacheRows, from = "redis" });
        }
    }

    var rows = await db.GetUsersAsync();
    if (rows.Count > 0)
    {
        await cache.SetAsync("user:list", JsonSerializer.Serialize(rows), TimeSpan.FromMinutes(5));
        return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
    }
    return Results.Ok(new
    {
        code = 0,
        msg = "查询成功",
        data = store.Users.OrderByDescending(x => x.UserId)
    });
}).RequireAuthorization("超级管理员");

var campus = app.MapGroup("/api/campus");
campus.MapGet("/tree", async () =>
{
    var nodes = await db.GetCampusNodesAsync();
    if (nodes.Count > 0)
    {
        var dict = nodes.ToDictionary(
            x => x.NodeId,
            x => new CampusNodeVm(x.NodeId, x.ParentId, x.LevelType, x.NodeName, []));
        foreach (var item in dict.Values)
        {
            if (item.ParentId.HasValue && dict.TryGetValue(item.ParentId.Value, out var parent))
            {
                parent.Children.Add(item);
            }
        }
        var roots = dict.Values.Where(x => !x.ParentId.HasValue || !dict.ContainsKey(x.ParentId.Value)).ToList();
        return Results.Ok(new { code = 0, msg = "查询成功", data = roots });
    }
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.CampusNodes.OrderBy(x => x.NodeId) });
}).RequireAuthorization("楼栋管理员");
campus.MapPost("/create", async (CampusCreateReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.LevelType) || string.IsNullOrWhiteSpace(req.NodeName))
    {
        return Results.BadRequest(new { code = 40021, msg = "层级类型和节点名称不能为空" });
    }
    var dbId = await db.InsertCampusNodeAsync(req.ParentId, req.LevelType, req.NodeName);
    if (dbId.HasValue)
    {
        await db.InsertOperationAsync("楼栋管理员", "资源节点创建", $"节点={req.NodeName}");
        return Results.Ok(new { code = 0, msg = "创建成功", data = new { nodeId = dbId.Value, req.ParentId, req.LevelType, req.NodeName } });
    }
    var entity = new CampusNodeEntity(Interlocked.Increment(ref store.CampusSeed), req.ParentId, req.LevelType, req.NodeName);
    store.CampusNodes.Add(entity);
    AddOperationLog(store, "楼栋管理员", "资源节点创建", $"节点={req.NodeName}");
    return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
}).RequireAuthorization("楼栋管理员");
campus.MapPost("/update/{nodeId:long}", async (long nodeId, CampusUpdateReq req) =>
{
    var ok = await db.UpdateCampusNodeAsync(nodeId, req.NodeName);
    if (ok)
    {
        await db.InsertOperationAsync("楼栋管理员", "资源节点更新", $"节点ID={nodeId}");
        return Results.Ok(new { code = 0, msg = "更新成功" });
    }
    var entity = store.CampusNodes.FirstOrDefault(x => x.NodeId == nodeId);
    if (entity is null) return Results.NotFound(new { code = 40421, msg = "节点不存在" });
    var updated = entity with { NodeName = req.NodeName };
    store.CampusNodes.Remove(entity);
    store.CampusNodes.Add(updated);
    AddOperationLog(store, "楼栋管理员", "资源节点更新", $"节点ID={nodeId}");
    return Results.Ok(new { code = 0, msg = "更新成功", data = updated });
}).RequireAuthorization("楼栋管理员");
campus.MapPost("/delete/{nodeId:long}", async (long nodeId) =>
{
    var ok = await db.DeleteCampusNodeAsync(nodeId);
    if (ok)
    {
        await db.InsertOperationAsync("楼栋管理员", "资源节点删除", $"节点ID={nodeId}");
        return Results.Ok(new { code = 0, msg = "删除成功" });
    }
    store.CampusNodes.RemoveAll(x => x.NodeId == nodeId || x.ParentId == nodeId);
    AddOperationLog(store, "楼栋管理员", "资源节点删除", $"节点ID={nodeId}");
    return Results.Ok(new { code = 0, msg = "删除成功" });
}).RequireAuthorization("楼栋管理员");

var floor = app.MapGroup("/api/floor");
floor.MapGet("/list", async () =>
{
    var rows = await db.GetFloorsAsync();
    if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.Floors.OrderByDescending(x => x.FloorId) });
}).RequireAuthorization("楼栋管理员");
floor.MapPost("/create", async (FloorCreateReq req) =>
{
    var dbId = await db.InsertFloorAsync(req.NodeId, req.FilePath, req.ScaleRatio);
    if (dbId.HasValue)
    {
        await db.InsertOperationAsync("楼栋管理员", "楼层图创建", $"节点ID={req.NodeId}");
        return Results.Ok(new { code = 0, msg = "创建成功", data = new { floorId = dbId.Value, req.NodeId, req.FilePath, req.ScaleRatio } });
    }
    var entity = new FloorEntity(Interlocked.Increment(ref store.FloorSeed), req.NodeId, req.FilePath, req.ScaleRatio);
    store.Floors.Add(entity);
    AddOperationLog(store, "楼栋管理员", "楼层图创建", $"节点ID={req.NodeId}");
    return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
}).RequireAuthorization("楼栋管理员");
floor.MapPost("/upload", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { code = 40031, msg = "请使用表单上传" });
    }
    var form = await request.ReadFormAsync();
    var file = form.Files["file"];
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { code = 40032, msg = "未找到上传文件" });
    }
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    var allow = new[] { ".png", ".jpg", ".jpeg", ".webp" };
    if (!allow.Contains(ext))
    {
        return Results.BadRequest(new { code = 40033, msg = "仅支持 png/jpg/jpeg/webp" });
    }

    var folder = Path.Combine(storageRoot, "uploads", "floors");
    Directory.CreateDirectory(folder);
    var safeName = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}{ext}";
    var localPath = Path.Combine(folder, safeName);
    await using (var fs = File.Create(localPath))
    {
        await file.CopyToAsync(fs);
    }
    var filePath = $"/storage/uploads/floors/{safeName}";
    await db.InsertOperationAsync("楼栋管理员", "楼层图上传", $"文件={safeName}");
    AddOperationLog(store, "楼栋管理员", "楼层图上传", $"文件={safeName}");
    return Results.Ok(new { code = 0, msg = "上传成功", data = new { filePath, originalName = file.FileName, size = file.Length } });
}).RequireAuthorization("楼栋管理员");

var camera = app.MapGroup("/api/camera");
camera.MapGet("/list", async () =>
{
    var rows = await db.GetCamerasAsync();
    if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.Cameras.OrderByDescending(x => x.CameraId) });
}).RequireAuthorization("楼栋管理员");
camera.MapPost("/create", async (CameraCreateReq req) =>
{
    var dbId = await db.InsertCameraAsync(req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY);
    if (dbId.HasValue)
    {
        await db.InsertOperationAsync("楼栋管理员", "摄像头布点创建", $"楼层ID={req.FloorId}, 设备ID={req.DeviceId}");
        return Results.Ok(new
        {
            code = 0,
            msg = "创建成功",
            data = new { cameraId = dbId.Value, req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY }
        });
    }
    var entity = new CameraEntity(Interlocked.Increment(ref store.CameraSeed), req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY);
    store.Cameras.Add(entity);
    AddOperationLog(store, "楼栋管理员", "摄像头布点创建", $"楼层ID={req.FloorId}, 设备ID={req.DeviceId}");
    return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
}).RequireAuthorization("楼栋管理员");
user.MapPost("/create", async (UserCreateReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrWhiteSpace(req.Password))
    {
        return Results.BadRequest(new { code = 40012, msg = "用户名或密码不能为空" });
    }
    var hash = BCrypt.Net.BCrypt.HashPassword(req.Password);
    var dbId = await db.InsertUserAsync(req.UserName, hash, req.RoleId);
    if (dbId.HasValue)
    {
        await db.InsertOperationAsync("系统管理员", "用户创建", $"用户={req.UserName}, 角色ID={req.RoleId}");
        return Results.Ok(new
        {
            code = 0,
            msg = "创建成功",
            data = new { userId = dbId.Value, userName = req.UserName, roleId = req.RoleId, status = 1 }
        });
    }

    var entity = new UserEntity(
        UserId: Interlocked.Increment(ref store.UserSeed),
        UserName: req.UserName,
        RoleName: req.RoleId == 1 ? "super_admin" : "building_admin",
        Status: 1,
        CreatedAt: DateTimeOffset.Now
    );
    store.Users.Add(entity);
    AddOperationLog(store, "系统管理员", "用户创建", $"用户={req.UserName}, 角色ID={req.RoleId}");
    return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
}).RequireAuthorization("超级管理员");
user.MapPost("/status/{userId:long}", async (long userId, UserStatusReq req) =>
{
    var ok = await db.UpdateUserStatusAsync(userId, req.Status);
    if (ok)
    {
        await db.InsertOperationAsync("系统管理员", "用户状态更新", $"用户ID={userId}, 状态={req.Status}");
        return Results.Ok(new { code = 0, msg = "状态更新成功" });
    }

    var entity = store.Users.FirstOrDefault(x => x.UserId == userId);
    if (entity is null)
    {
        return Results.NotFound(new { code = 40402, msg = "用户不存在" });
    }
    var updated = entity with { Status = req.Status };
    store.Users.Remove(entity);
    store.Users.Add(updated);
    AddOperationLog(store, "系统管理员", "用户状态更新", $"用户ID={userId}, 状态={req.Status}");
    return Results.Ok(new { code = 0, msg = "状态更新成功", data = updated });
}).RequireAuthorization("超级管理员");

var device = app.MapGroup("/api/device");
device.MapGet("/list", async () =>
{
    var cached = await cache.GetAsync("device:list");
    if (!string.IsNullOrWhiteSpace(cached))
    {
        var cacheRows = JsonSerializer.Deserialize<List<DbDevice>>(cached);
        if (cacheRows is { Count: > 0 })
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = cacheRows, from = "redis" });
        }
    }

    var rows = await db.GetDevicesAsync();
    if (rows.Count > 0)
    {
        await cache.SetAsync("device:list", JsonSerializer.Serialize(rows), TimeSpan.FromMinutes(3));
        return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
    }
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.Devices.OrderByDescending(x => x.DeviceId) });
}).RequireAuthorization("楼栋管理员");
device.MapPost("/register", async (DeviceRegisterReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Ip))
        return Results.BadRequest(new { code = 40002, msg = "设备名称和IP不能为空" });
    var entity = new DeviceEntity(Interlocked.Increment(ref store.DeviceSeed), req.Name, req.Ip, req.Port, req.Brand, req.Protocol, "offline", DateTimeOffset.Now);
    var dbId = await db.InsertDeviceAsync(entity.Name, entity.Ip, entity.Port, entity.Brand, entity.Protocol, entity.Status);
    if (dbId.HasValue)
    {
        var savedDb = entity with { DeviceId = dbId.Value };
        await db.InsertOperationAsync("系统管理员", "设备注册", $"设备={savedDb.Name}, IP={savedDb.Ip}");
        if (cache.Enabled) await cache.DeleteAsync("device:list");
        return Results.Ok(new { code = 0, msg = "设备注册成功", data = savedDb });
    }
    store.Devices.Add(entity);
    AddOperationLog(store, "系统管理员", "设备注册", $"设备={entity.Name}, IP={entity.Ip}");
    if (cache.Enabled) await cache.DeleteAsync("device:list");
    return Results.Ok(new { code = 0, msg = "设备注册成功", data = entity });
}).RequireAuthorization("超级管理员");
device.MapPost("/ping/{deviceId:long}", (long deviceId) =>
{
    var entity = store.Devices.FirstOrDefault(x => x.DeviceId == deviceId);
    if (entity is null) return Results.NotFound(new { code = 40401, msg = "设备不存在" });
    var updated = entity with { Status = "online" };
    store.Devices.Remove(entity);
    store.Devices.Add(updated);
    AddOperationLog(store, "系统管理员", "设备心跳", $"设备={updated.Name}上线");
    return Results.Ok(new { code = 0, msg = "设备状态更新成功", data = updated });
}).RequireAuthorization("楼栋管理员");

var capture = app.MapGroup("/api/capture");
capture.MapPost("/push", async (HttpRequest request, JsonElement req) =>
{
    var normalized = hikAdapter.Normalize(req);
    var signature = request.Headers["X-Signature"].ToString();

    if (request.ContentLength.HasValue && request.ContentLength.Value > MaxCaptureRequestBytes)
        return Results.BadRequest(new { code = 40006, msg = "请求体过大" });
    if (string.IsNullOrWhiteSpace(normalized.ImageBase64))
        return Results.BadRequest(new { code = 40007, msg = "图片Base64不能为空" });
    if (normalized.ImageBase64.Length > MaxImageBase64Chars)
        return Results.BadRequest(new { code = 40008, msg = "图片 Base64 过大" });
    if (!string.IsNullOrWhiteSpace(normalized.MetadataJson) && normalized.MetadataJson.Length > MaxMetadataJsonChars)
        return Results.BadRequest(new { code = 40009, msg = "元数据过大" });

    var payload = $"{normalized.DeviceId}|{normalized.ChannelNo}|{normalized.CaptureTime:O}";
    var deviceSecret = await db.GetDeviceHmacSecretAsync(normalized.DeviceId);
    var secretToUse = string.IsNullOrWhiteSpace(deviceSecret)
        ? (isDev ? globalHmacSecret : null)
        : deviceSecret;
    if (string.IsNullOrWhiteSpace(secretToUse)) return Results.Unauthorized();
    if (!VerifyHmac(payload, signature, secretToUse)) return Results.Unauthorized();
    if (!IsIpAllowed(request, captureIpWhitelist)) return Results.BadRequest(new { code = 40004, msg = "来源IP不在白名单中" });

    var rl = await CheckRateLimitAsync(request, "capture.push", 30, TimeSpan.FromMinutes(1), normalized.DeviceId.ToString());
    if (rl is not null) return rl;

    return await SaveCaptureAsync(normalized, "海康ISAPI抓拍");
});
capture.MapPost("/sdk", async (HttpRequest request, JsonElement req) =>
{
    var normalized = cppSdkAdapter.Normalize(req);

    if (request.ContentLength.HasValue && request.ContentLength.Value > MaxCaptureRequestBytes)
        return Results.BadRequest(new { code = 40006, msg = "请求体过大" });
    if (string.IsNullOrWhiteSpace(normalized.ImageBase64))
        return Results.BadRequest(new { code = 40007, msg = "图片Base64不能为空" });
    if (normalized.ImageBase64.Length > MaxImageBase64Chars)
        return Results.BadRequest(new { code = 40008, msg = "图片 Base64 过大" });
    if (!string.IsNullOrWhiteSpace(normalized.MetadataJson) && normalized.MetadataJson.Length > MaxMetadataJsonChars)
        return Results.BadRequest(new { code = 40009, msg = "元数据过大" });

    var signature = request.Headers["X-Signature"].ToString();
    var payload = $"{normalized.DeviceId}|{normalized.ChannelNo}|{normalized.CaptureTime:O}";
    var deviceSecret = await db.GetDeviceHmacSecretAsync(normalized.DeviceId);
    var secretToUse = string.IsNullOrWhiteSpace(deviceSecret)
        ? (isDev ? globalHmacSecret : null)
        : deviceSecret;
    if (string.IsNullOrWhiteSpace(secretToUse)) return Results.Unauthorized();
    if (!VerifyHmac(payload, signature, secretToUse)) return Results.Unauthorized();
    if (!IsIpAllowed(request, captureIpWhitelist)) return Results.BadRequest(new { code = 40004, msg = "来源IP不在白名单中" });

    var rl = await CheckRateLimitAsync(request, "capture.sdk", 30, TimeSpan.FromMinutes(1), normalized.DeviceId.ToString());
    if (rl is not null) return rl;

    return await SaveCaptureAsync(normalized, "C++SDK抓拍");
});
capture.MapPost("/onvif", async (HttpRequest request, JsonElement req) =>
{
    var normalized = onvifAdapter.Normalize(req);

    if (request.ContentLength.HasValue && request.ContentLength.Value > MaxCaptureRequestBytes)
        return Results.BadRequest(new { code = 40006, msg = "请求体过大" });
    if (string.IsNullOrWhiteSpace(normalized.ImageBase64))
        return Results.BadRequest(new { code = 40007, msg = "图片Base64不能为空" });
    if (normalized.ImageBase64.Length > MaxImageBase64Chars)
        return Results.BadRequest(new { code = 40008, msg = "图片 Base64 过大" });
    if (!string.IsNullOrWhiteSpace(normalized.MetadataJson) && normalized.MetadataJson.Length > MaxMetadataJsonChars)
        return Results.BadRequest(new { code = 40009, msg = "元数据过大" });

    var signature = request.Headers["X-Signature"].ToString();
    var payload = $"{normalized.DeviceId}|{normalized.ChannelNo}|{normalized.CaptureTime:O}";
    var deviceSecret = await db.GetDeviceHmacSecretAsync(normalized.DeviceId);
    var secretToUse = string.IsNullOrWhiteSpace(deviceSecret)
        ? (isDev ? globalHmacSecret : null)
        : deviceSecret;
    if (string.IsNullOrWhiteSpace(secretToUse)) return Results.Unauthorized();
    if (!VerifyHmac(payload, signature, secretToUse)) return Results.Unauthorized();
    if (!IsIpAllowed(request, captureIpWhitelist)) return Results.BadRequest(new { code = 40004, msg = "来源IP不在白名单中" });

    var rl = await CheckRateLimitAsync(request, "capture.onvif", 30, TimeSpan.FromMinutes(1), normalized.DeviceId.ToString());
    if (rl is not null) return rl;

    return await SaveCaptureAsync(normalized, "ONVIF抓拍");
});
capture.MapPost("/mock", async (CaptureMockReq req) =>
{
    var record = new CaptureEntity(Interlocked.Increment(ref store.CaptureSeed), req.DeviceId, req.ChannelNo, DateTimeOffset.Now, req.MetadataJson);
    var dbId = await db.InsertCaptureAsync(record.DeviceId, record.ChannelNo, record.CaptureTime, record.MetadataJson, null);
    var saved = dbId.HasValue ? record with { CaptureId = dbId.Value } : record;
    if (!dbId.HasValue) store.Captures.Add(saved);
    await db.InsertOperationAsync("楼栋管理员", "模拟抓拍", $"设备={req.DeviceId}, 通道={req.ChannelNo}");
    AddOperationLog(store, "楼栋管理员", "模拟抓拍", $"设备={req.DeviceId}, 通道={req.ChannelNo}");
    if (!string.IsNullOrWhiteSpace(req.MetadataJson) && req.MetadataJson.Contains("异常"))
    {
        var a = new AlertEntity(Interlocked.Increment(ref store.AlertSeed), "异常滞留", $"模拟抓拍{saved.CaptureId}命中异常关键词", DateTimeOffset.Now);
        var aid = await db.InsertAlertAsync(a.AlertType, a.Detail);
        if (!aid.HasValue) store.Alerts.Add(a);
    }
    return Results.Ok(new { code = 0, msg = "模拟抓拍创建成功", data = saved });
}).RequireAuthorization("楼栋管理员");
var retry = app.MapGroup("/api/retry");
retry.MapGet("/status", async () =>
{
    var count = await retryQueue.LengthAsync();
    return Results.Ok(new { code = 0, msg = "查询成功", data = new { enabled = retryQueue.Enabled, pending = count } });
}).RequireAuthorization("超级管理员");
retry.MapPost("/process", async (HttpRequest request, RetryProcessReq req) =>
{
    var rl = await CheckRateLimitAsync(request, "retry.process", 30, TimeSpan.FromMinutes(1));
    if (rl is not null) return rl;

    const string processLockKey = "aura:lock:retry-process";
    const int processLockMinutes = 30;
    string? lockToken = null;
    if (cache.Enabled)
    {
        lockToken = await cache.TryAcquireLockAsync(processLockKey, TimeSpan.FromMinutes(processLockMinutes));
        if (lockToken is null)
            return Results.Json(new { code = 42902, msg = "重试任务处理正在进行中，请稍后再试（其他实例或会话可能正在执行）" }, statusCode: 429);
    }

    var take = req.Take <= 0 ? 10 : Math.Min(req.Take, 100);
    var success = 0;
    var failed = 0;
    try
    {
        for (var i = 0; i < take; i++)
        {
            var task = await retryQueue.DequeueAsync();
            if (task is null)
            {
                break;
            }

            AiExtractResult ai;
            if (!string.IsNullOrWhiteSpace(task.ImagePath))
            {
                ai = await aiClient.ExtractByPathAsync(task.ImagePath, task.MetadataJson);
                if (!ai.Success && !string.IsNullOrWhiteSpace(task.ImageBase64))
                {
                    ai = await aiClient.ExtractAsync(task.ImageBase64, task.MetadataJson);
                }
            }
            else
            {
                ai = await aiClient.ExtractAsync(task.ImageBase64 ?? "", task.MetadataJson);
            }
            if (ai.Success)
            {
                success++;
                // 将重试的 AI 结果写回抓拍记录：更新 metadata，并补写向量
                var newMetadata = AttachAiResult(task.MetadataJson, ai);
                _ = await db.UpdateCaptureMetadataAsync(task.CaptureId, newMetadata);
                if (ai.Feature.Count > 0)
                {
                    var vectorId = $"C_{task.CaptureId}";
                    await aiClient.UpsertAsync(vectorId, ai.Feature);
                }

                await db.InsertOperationAsync("重试任务", "AI重试成功", $"captureId={task.CaptureId}, 设备={task.DeviceId}, 通道={task.ChannelNo}");

                // 成功后清理落盘的临时图片，避免磁盘无限增长
                if (!string.IsNullOrWhiteSpace(task.ImagePath))
                {
                    try
                    {
                        if (File.Exists(task.ImagePath)) File.Delete(task.ImagePath);
                    }
                    catch
                    {
                        // 删除失败不影响主流程
                    }
                }
                continue;
            }
            failed++;
            if (task.RetryCount < 3)
            {
                await retryQueue.EnqueueAsync(task with { RetryCount = task.RetryCount + 1 });
            }
            else
            {
                // 达到最大重试次数后清理落盘图片，避免磁盘无限增长
                if (!string.IsNullOrWhiteSpace(task.ImagePath))
                {
                    try
                    {
                        if (File.Exists(task.ImagePath)) File.Delete(task.ImagePath);
                    }
                    catch
                    {
                        // 删除失败不影响主流程
                    }
                }
            }
            await db.InsertOperationAsync("重试任务", "AI重试失败", $"设备={task.DeviceId}, 通道={task.ChannelNo}, 原因={ai.Message}");
        }
    }
    finally
    {
        if (lockToken is not null)
            await cache.ReleaseLockAsync(processLockKey, lockToken);
    }

    return Results.Ok(new { code = 0, msg = "处理完成", data = new { take, success, failed } });
}).RequireAuthorization("超级管理员");
capture.MapGet("/list", async (HttpRequest httpReq) =>
{
    const int defaultLimit = 500;
    const int maxLimit = 2000;
    const int maxPageSize = 200;

    if (int.TryParse(httpReq.Query["page"].FirstOrDefault(), out var pageNum) && pageNum > 0)
    {
        var pageSize = int.TryParse(httpReq.Query["pageSize"].FirstOrDefault(), out var ps) ? ps : 20;
        pageSize = Math.Clamp(pageSize, 1, maxPageSize);
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        var fromQ = httpReq.Query["from"].FirstOrDefault();
        var toQ = httpReq.Query["to"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fromQ) && DateTimeOffset.TryParse(fromQ, out var f)) from = f;
        if (!string.IsNullOrWhiteSpace(toQ) && DateTimeOffset.TryParse(toQ, out var t)) to = t;

        var (dbRows, total) = await db.GetCapturesPagedAsync(from, to, pageNum, pageSize);
        if (dbRows.Count > 0)
        {
            var mapped = dbRows.Select(x => new CaptureEntity(x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson));
            return Results.Ok(new { code = 0, msg = "查询成功", data = mapped, pagination = new { total, page = pageNum, pageSize } });
        }

        IEnumerable<CaptureEntity> mem = store.Captures;
        if (from.HasValue) mem = mem.Where(x => x.CaptureTime >= from.Value);
        if (to.HasValue) mem = mem.Where(x => x.CaptureTime <= to.Value);
        var ordered = mem.OrderByDescending(x => x.CaptureId).ToList();
        var memTotal = ordered.Count;
        var slice = ordered.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();
        return Results.Ok(new { code = 0, msg = "查询成功", data = slice, pagination = new { total = memTotal, page = pageNum, pageSize } });
    }

    var limitStr = httpReq.Query["limit"].FirstOrDefault();
    var lim = int.TryParse(limitStr, out var ll) ? ll : defaultLimit;
    lim = Math.Clamp(lim, 1, maxLimit);
    var rows = await db.GetCapturesAsync(lim);
    if (rows.Count > 0)
    {
        var mapped = rows.Select(x => new CaptureEntity(x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson));
        return Results.Ok(new { code = 0, msg = "查询成功", data = mapped });
    }
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.Captures.OrderByDescending(x => x.CaptureId).Take(lim) });
}).RequireAuthorization("楼栋管理员");

var roi = app.MapGroup("/api/roi");
roi.MapGet("/list", async () =>
{
    var rows = await db.GetRoisAsync();
    if (rows.Count > 0)
    {
        var mapped = rows.Select(x => new RoiEntity(x.RoiId, x.CameraId, x.RoomNodeId, x.VerticesJson, x.CreatedAt));
        return Results.Ok(new { code = 0, msg = "查询成功", data = mapped });
    }
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.Rois.OrderByDescending(x => x.RoiId) });
}).RequireAuthorization("楼栋管理员");
roi.MapPost("/save", async (RoiReq req) =>
{
    var entity = new RoiEntity(Interlocked.Increment(ref store.RoiSeed), req.CameraId, req.RoomNodeId, req.VerticesJson, DateTimeOffset.Now);
    var dbId = await db.InsertRoiAsync(req.CameraId, req.RoomNodeId, req.VerticesJson);
    var saved = dbId.HasValue ? entity with { RoiId = dbId.Value } : entity;
    if (!dbId.HasValue) store.Rois.Add(saved);
    AddOperationLog(store, "楼栋管理员", "防区保存", $"相机={req.CameraId}, 房间={req.RoomNodeId}");
    return Results.Ok(new { code = 0, msg = "ROI保存成功", data = saved });
}).RequireAuthorization("楼栋管理员");

var track = app.MapGroup("/api/track");
track.MapGet("/{vid}", async (HttpRequest httpReq, string vid) =>
{
    const int defaultLimit = 500;
    const int maxLimit = 2000;
    var limitStr = httpReq.Query["limit"].FirstOrDefault();
    var lim = int.TryParse(limitStr, out var l) ? l : defaultLimit;
    lim = Math.Clamp(lim, 1, maxLimit);

    var rows = await db.GetTrackEventsAsync(vid, lim);
    if (rows.Count > 0)
    {
        return Results.Ok(new { code = 0, msg = "查询成功", data = new { vid, limit = lim, points = rows.Select(x => new { x.CameraId, x.RoiId, time = x.EventTime }) } });
    }
    var points = store.TrackEvents
        .Where(x => x.Vid == vid)
        .OrderByDescending(x => x.EventTime)
        .Take(lim)
        .Select(x => new { x.CameraId, x.RoiId, time = x.EventTime });
    return Results.Ok(new { code = 0, msg = "查询成功", data = new { vid, limit = lim, points } });
}).RequireAuthorization("楼栋管理员");

var judge = app.MapGroup("/api/judge");
judge.MapPost("/run/home", async (HttpRequest request, JudgeRunReq req) =>
{
    var rl = await CheckRateLimitAsync(request, "judge.run.home", 1, TimeSpan.FromMinutes(10));
    if (rl is not null) return rl;

    var date = string.IsNullOrWhiteSpace(req.Date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(req.Date);
    var ret = await RunHomeJudgeAsync(date);
    await BroadcastEventAsync("judge.updated", ret);
    return Results.Ok(new { code = 0, msg = "归寝研判完成", data = ret });
}).RequireAuthorization("楼栋管理员");
judge.MapPost("/run/abnormal", async (HttpRequest request, JudgeAbnormalReq req) =>
{
    var rl = await CheckRateLimitAsync(request, "judge.run.abnormal", 1, TimeSpan.FromMinutes(10));
    if (rl is not null) return rl;

    var date = string.IsNullOrWhiteSpace(req.Date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(req.Date);
    var threshold = req.GroupThreshold <= 1 ? 2 : req.GroupThreshold;
    var stayMinutes = req.StayMinutes <= 0 ? 120 : req.StayMinutes;
    var ret = await RunGroupRentAndStayJudgeAsync(date, threshold, stayMinutes);
    await BroadcastEventAsync("judge.updated", ret);
    return Results.Ok(new { code = 0, msg = "群租/滞留研判完成", data = ret });
}).RequireAuthorization("楼栋管理员");
judge.MapPost("/run/night", async (HttpRequest request, JudgeNightReq req) =>
{
    var rl = await CheckRateLimitAsync(request, "judge.run.night", 1, TimeSpan.FromMinutes(10));
    if (rl is not null) return rl;

    var date = string.IsNullOrWhiteSpace(req.Date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(req.Date);
    var cutoff = req.CutoffHour is < 0 or > 23 ? 23 : req.CutoffHour;
    var ret = await RunNightAbsenceJudgeAsync(date, cutoff);
    await BroadcastEventAsync("judge.updated", ret);
    return Results.Ok(new { code = 0, msg = "夜不归宿研判完成", data = ret });
}).RequireAuthorization("楼栋管理员");
judge.MapPost("/run/daily", async (HttpRequest request, JudgeNightReq req) =>
{
    var rl = await CheckRateLimitAsync(request, "judge.run.daily", 1, TimeSpan.FromMinutes(10));
    if (rl is not null) return rl;

    var date = string.IsNullOrWhiteSpace(req.Date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(req.Date);
    var cutoff = req.CutoffHour is < 0 or > 23 ? 23 : req.CutoffHour;
    var home = await RunHomeJudgeAsync(date);
    var abnormal = await RunGroupRentAndStayJudgeAsync(date, 2, 120);
    var night = await RunNightAbsenceJudgeAsync(date, cutoff);
    var summary = new[] { home, abnormal, night };
    await BroadcastEventAsync("judge.updated", summary);
    return Results.Ok(new { code = 0, msg = "每日研判完成", data = summary });
}).RequireAuthorization("楼栋管理员");
judge.MapGet("/daily", async (HttpRequest httpReq, string? date) =>
{
    const int defaultLimit = 2000;
    const int maxLimit = 5000;
    var limitStr = httpReq.Query["limit"].FirstOrDefault();
    var lim = int.TryParse(limitStr, out var l) ? l : defaultLimit;
    lim = Math.Clamp(lim, 1, maxLimit);

    var day = string.IsNullOrWhiteSpace(date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(date);
    var rows = await db.GetJudgeResultsAsync(day, null, lim);
    if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows, limit = lim });
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.JudgeResults.Where(x => x.JudgeDate == day).OrderByDescending(x => x.JudgeId).Take(lim), limit = lim });
}).RequireAuthorization("楼栋管理员");

var alert = app.MapGroup("/api/alert");
alert.MapGet("/list", async (HttpRequest httpReq) =>
{
    const int defaultLimit = 500;
    const int maxLimit = 2000;
    var limitStr = httpReq.Query["limit"].FirstOrDefault();
    var lim = int.TryParse(limitStr, out var l) ? l : defaultLimit;
    lim = Math.Clamp(lim, 1, maxLimit);

    var rows = await db.GetAlertsAsync(lim);
    if (rows.Count > 0)
    {
        var mapped = rows.Select(x => new AlertEntity(x.AlertId, x.AlertType, x.Detail, x.CreatedAt));
        return Results.Ok(new { code = 0, msg = "查询成功", data = mapped });
    }
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.Alerts.OrderByDescending(x => x.AlertId).Take(lim) });
}).RequireAuthorization("楼栋管理员");
alert.MapPost("/create", async (CreateAlertReq req) =>
{
    var entity = new AlertEntity(Interlocked.Increment(ref store.AlertSeed), req.AlertType, req.Detail, DateTimeOffset.Now);
    var dbId = await db.InsertAlertAsync(entity.AlertType, entity.Detail);
    var saved = dbId.HasValue ? entity with { AlertId = dbId.Value } : entity;
    if (!dbId.HasValue) store.Alerts.Add(saved);
    await db.InsertOperationAsync("楼栋管理员", "手动告警", $"类型={req.AlertType}");
    AddOperationLog(store, "楼栋管理员", "手动告警", $"类型={req.AlertType}");
    await NotifyAlertAsync(saved.AlertType, saved.Detail, "手动告警");
    await BroadcastEventAsync("alert.created", new { alertType = saved.AlertType, detail = saved.Detail, at = saved.CreatedAt });
    return Results.Ok(new { code = 0, msg = "告警创建成功", data = saved });
}).RequireAuthorization("楼栋管理员");

var stats = app.MapGroup("/api/stats");
stats.MapGet("/overview", async () =>
{
    var captures = await db.GetCapturesAsync();
    var alerts = await db.GetAlertsAsync();
    var devices = await db.GetDevicesAsync();
    var sourceCaptures = captures.Count > 0
        ? captures.Select(x => new { x.DeviceId, CaptureTime = new DateTimeOffset(x.CaptureTime) }).ToList()
        : store.Captures.Select(x => new { x.DeviceId, x.CaptureTime }).ToList();
    var totalCapture = sourceCaptures.Count;
    var totalAlert = alerts.Count > 0 ? alerts.Count : store.Alerts.Count;
    var onlineDevice = devices.Count > 0 ? devices.Count(x => x.Status == "online") : store.Devices.Count(x => x.Status == "online");
    return Results.Ok(new { code = 0, msg = "查询成功", data = new { totalCapture, totalAlert, onlineDevice } });
}).RequireAuthorization("楼栋管理员");
stats.MapGet("/dashboard", async () =>
{
    var captures = await db.GetCapturesAsync();
    var alerts = await db.GetAlertsAsync();
    var sourceCaptures = captures.Count > 0
        ? captures.Select(x => new { x.DeviceId, CaptureTime = new DateTimeOffset(x.CaptureTime) }).ToList()
        : store.Captures.Select(x => new { x.DeviceId, x.CaptureTime }).ToList();
    var sourceAlerts = alerts.Count > 0
        ? alerts.Select(x => new { x.AlertType, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
        : store.Alerts.Select(x => new { x.AlertType, x.CreatedAt }).ToList();

    var today = DateOnly.FromDateTime(DateTime.Now);
    var daily = Enumerable.Range(0, 7)
        .Select(i => today.AddDays(-6 + i))
        .Select(d => new
        {
            day = d.ToString("MM-dd"),
            captureCount = sourceCaptures.Count(x => DateOnly.FromDateTime(x.CaptureTime.DateTime) == d),
            alertCount = sourceAlerts.Count(x => DateOnly.FromDateTime(x.CreatedAt.DateTime) == d)
        }).ToList();
    var byDevice = sourceCaptures
        .GroupBy(x => x.DeviceId)
        .Select(g => new { deviceId = g.Key, count = g.Count() })
        .OrderByDescending(x => x.count)
        .Take(10)
        .ToList();
    var byAlertType = sourceAlerts
        .GroupBy(x => string.IsNullOrWhiteSpace(x.AlertType) ? "unknown" : x.AlertType)
        .Select(g => new { alertType = g.Key, count = g.Count() })
        .OrderByDescending(x => x.count)
        .ToList();
    return Results.Ok(new { code = 0, msg = "查询成功", data = new { daily, byDevice, byAlertType } });
}).RequireAuthorization("楼栋管理员");

var export = app.MapGroup("/api/export");
export.MapGet("/{type}", async (HttpRequest request, string type, string dataset = "capture", int maxRows = 5000) =>
{
    var rl = await CheckRateLimitAsync(request, "export", 5, TimeSpan.FromMinutes(1));
    if (rl is not null) return rl;

    type = type.Trim().ToLowerInvariant();
    dataset = dataset.Trim().ToLowerInvariant();
    if (type is not ("csv" or "xlsx"))
        return Results.BadRequest(new { code = 40061, msg = "仅支持csv/xlsx" });
    if (dataset is not ("capture" or "alert" or "judge"))
        return Results.BadRequest(new { code = 40062, msg = "dataset仅支持capture/alert/judge" });
    if (maxRows <= 0) maxRows = 5000;
    maxRows = Math.Min(maxRows, 20000);

    List<string[]> rows;
    if (dataset == "capture")
    {
        var captures = await db.GetCapturesAsync(maxRows);
        var source = captures.Count > 0
            ? captures.Select(x => new { x.CaptureId, x.DeviceId, x.ChannelNo, CaptureTime = new DateTimeOffset(x.CaptureTime), x.MetadataJson }).ToList()
            : store.Captures.Select(x => new { x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson }).ToList();
        rows =
        [
            ["抓拍ID", "设备ID", "通道号", "抓拍时间", "元数据"],
            ..source.Select(x => new[] { $"{x.CaptureId}", $"{x.DeviceId}", $"{x.ChannelNo}", x.CaptureTime.ToString("yyyy-MM-dd HH:mm:ss"), EscapeCell(x.MetadataJson) })
        ];
    }
    else if (dataset == "alert")
    {
        var alerts = await db.GetAlertsAsync(maxRows);
        var source = alerts.Count > 0
            ? alerts.Select(x => new { x.AlertId, x.AlertType, x.Detail, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
            : store.Alerts.Select(x => new { x.AlertId, x.AlertType, Detail = x.Detail, x.CreatedAt }).ToList();
        rows =
        [
            ["告警ID", "告警类型", "详情", "创建时间"],
            ..source.Select(x => new[] { $"{x.AlertId}", x.AlertType, EscapeCell(x.Detail), x.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") })
        ];
    }
    else
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var judgeRows = await db.GetJudgeResultsAsync(date, null, maxRows);
        var source = judgeRows.Count > 0
            ? judgeRows.Select(x => new { x.JudgeId, x.Vid, x.RoomId, x.JudgeType, x.JudgeDate, x.DetailJson }).ToList()
            : store.JudgeResults.Select(x => new { x.JudgeId, x.Vid, x.RoomId, x.JudgeType, JudgeDate = x.JudgeDate.ToDateTime(TimeOnly.MinValue), x.DetailJson }).ToList();
        rows =
        [
            ["研判ID", "人员虚拟编号", "房间ID", "研判类型", "研判日期", "详情数据"],
            ..source.Select(x => new[] { $"{x.JudgeId}", x.Vid, $"{x.RoomId}", x.JudgeType, x.JudgeDate.ToString("yyyy-MM-dd"), EscapeCell(x.DetailJson) })
        ];
    }

    var ext = type == "xlsx" ? "xlsx" : "csv";
    var titleCn = ExportDatasetTitleCn(dataset);
    var shortId = Guid.NewGuid().ToString("N")[..8];
    var fileName = $"{titleCn}_{DateTimeOffset.Now:yyyyMMddHHmmss}_{shortId}.{ext}";
    var exportDir = Path.Combine(storageRoot, "outputs");
    Directory.CreateDirectory(exportDir);
    var localPath = Path.Combine(exportDir, fileName);
    if (type == "csv")
    {
        /* UTF-8 BOM，便于 Excel 正确识别中文表头 */
        var csvBody = string.Join(Environment.NewLine, rows.Select(r => string.Join(",", r.Select(ToCsvCell))));
        var csv = "\uFEFF" + csvBody;
        await File.WriteAllTextAsync(localPath, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
    else
    {
        // 轻量导出：使用Excel可直接打开的HTML表格格式，扩展名为xlsx
        var htmlRows = string.Join("", rows.Select((r, i) =>
        {
            var cells = string.Join("", r.Select(c => $"<{(i == 0 ? "th" : "td")}>{System.Net.WebUtility.HtmlEncode(c)}</{(i == 0 ? "th" : "td")}>"));
            return $"<tr>{cells}</tr>";
        }));
        var html = $"<html><head><meta charset=\"utf-8\" /></head><body><table border=\"1\">{htmlRows}</table></body></html>";
        await File.WriteAllTextAsync(localPath, html, Encoding.UTF8);
    }
    await db.InsertOperationAsync("楼栋管理员", "报表导出", $"type={type}, dataset={dataset}, file={fileName}");
    var downloadUrl = $"/storage/outputs/{Uri.EscapeDataString(fileName)}";
    return Results.Ok(new { code = 0, msg = "导出文件已生成", data = new { fileName, downloadUrl, type, dataset } });
}).RequireAuthorization("楼栋管理员");

var output = app.MapGroup("/api/output");
output.MapGet("/events", async (DateTimeOffset? from, DateTimeOffset? to, int page = 1, int pageSize = 200) =>
{
    if (page <= 0) page = 1;
    if (pageSize <= 0) pageSize = 200;
    if (pageSize > 1000) pageSize = 1000;
    var (rows, total) = await db.GetCapturesPagedAsync(from, to, page, pageSize);
    var data = rows.Select(x => new { eventType = "capture", captureId = x.CaptureId, x.DeviceId, x.ChannelNo, captureTime = x.CaptureTime, metadata = x.MetadataJson });
    return Results.Ok(new { code = 0, msg = "输出成功", data, pager = new { page, pageSize, total } });
}).RequireAuthorization("超级管理员");
output.MapGet("/persons", async (int minCapture = 1) =>
{
    if (minCapture <= 0) minCapture = 1;
    var rows = await db.GetVirtualPersonsAsync();
    if (rows.Count > 0)
    {
        var dataDb = rows.Where(x => x.CaptureCount >= minCapture).Select(x => new { vid = x.Vid, mainDevice = x.DeviceId, captureCount = x.CaptureCount, x.FirstSeen, x.LastSeen });
        return Results.Ok(new { code = 0, msg = "输出成功", data = dataDb });
    }
    var data = store.Captures
        .GroupBy(x => x.DeviceId)
        .Select((g, i) => new { vid = $"V_DEMO_{i + 1:000}", mainDevice = g.Key, captureCount = g.Count() })
        .Where(x => x.captureCount >= minCapture);
    return Results.Ok(new { code = 0, msg = "输出成功", data });
}).RequireAuthorization("超级管理员");

var vector = app.MapGroup("/api/vector");
vector.MapPost("/extract", async (HttpRequest request, VectorExtractReq req) =>
{
    var rl = await CheckRateLimitAsync(request, "vector.extract", 20, TimeSpan.FromMinutes(1));
    if (rl is not null) return rl;

    if (string.IsNullOrWhiteSpace(req.ImageBase64))
    {
        return Results.BadRequest(new { code = 40051, msg = "图片Base64不能为空" });
    }
    if (req.ImageBase64.Length > MaxImageBase64Chars)
    {
        return Results.BadRequest(new { code = 40053, msg = "图片 Base64 过大" });
    }
    if (!string.IsNullOrWhiteSpace(req.MetadataJson) && req.MetadataJson!.Length > MaxMetadataJsonChars)
    {
        return Results.BadRequest(new { code = 40054, msg = "元数据过大" });
    }
    var ai = await aiClient.ExtractAsync(req.ImageBase64, req.MetadataJson ?? "{}");
    if (!ai.Success)
    {
        return Results.BadRequest(new { code = 40052, msg = ai.Message, data = new { ai.Dim } });
    }
    return Results.Ok(new { code = 0, msg = "提取成功", data = new { ai.Dim, ai.Feature } });
}).RequireAuthorization("楼栋管理员");
vector.MapPost("/search", async (HttpRequest request, VectorSearchReq req) =>
{
    var rl = await CheckRateLimitAsync(request, "vector.search", 60, TimeSpan.FromMinutes(1));
    if (rl is not null) return rl;

    var topK = req.TopK <= 0 ? 10 : Math.Min(req.TopK, 50);
    if (req.Feature is null || req.Feature.Count == 0)
        return Results.BadRequest(new { code = 40071, msg = "特征向量不能为空" });
    // 系统规划维度固定为 512；避免维度不一致导致向量库异常或性能劣化
    if (req.Feature.Count != 512)
        return Results.BadRequest(new { code = 40072, msg = "特征向量维度必须为 512" });
    var rows = await aiClient.SearchAsync(req.Feature, topK);
    return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
}).RequireAuthorization("楼栋管理员");

var space = app.MapGroup("/api/space");
space.MapPost("/collision/check", async (SpaceCollisionReq req) =>
{
    var roisDb = await db.GetRoisAsync();
    var rois = roisDb.Count > 0
        ? roisDb.Select(x => new RoiEntity(x.RoiId, x.CameraId, x.RoomNodeId, x.VerticesJson, x.CreatedAt)).ToList()
        : store.Rois.ToList();
    var matched = ResolveCollision(rois, req.CameraId, req.PosX, req.PosY);
    if (matched.Count == 0)
    {
        return Results.Ok(new { code = 0, msg = "未命中任何防区", data = new { hit = false, roomNodeIds = Array.Empty<long>() } });
    }

    var eventTime = req.EventTime ?? DateTimeOffset.Now;
    var vid = string.IsNullOrWhiteSpace(req.Vid) ? $"V_TMP_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}" : req.Vid.Trim();
    var events = new List<TrackEventEntity>();
    foreach (var item in matched)
    {
        var dbId = await db.InsertTrackEventAsync(vid, req.CameraId, item.RoiId, eventTime);
        var local = new TrackEventEntity(dbId ?? Interlocked.Increment(ref store.TrackEventSeed), vid, req.CameraId, item.RoiId, eventTime);
        if (!dbId.HasValue) store.TrackEvents.Add(local);
        events.Add(local);
    }
    AddOperationLog(store, "空间引擎", "空间碰撞判定", $"camera={req.CameraId}, x={req.PosX}, y={req.PosY}, hit={matched.Count}");
    await BroadcastEventAsync("track.event", new { vid, cameraId = req.CameraId, roiCount = matched.Count, eventTime });
    return Results.Ok(new
    {
        code = 0,
        msg = "碰撞判定完成",
        data = new
        {
            hit = true,
            vid,
            roiIds = matched.Select(x => x.RoiId).Distinct(),
            roomNodeIds = matched.Select(x => x.RoomNodeId).Distinct(),
            eventTime,
            events
        }
    });
}).RequireAuthorization("楼栋管理员");

var cluster = app.MapGroup("/api/cluster");
cluster.MapPost("/run", async (ClusterRunReq req) =>
{
    var gapMinutes = req.GapMinutes <= 0 ? 30 : req.GapMinutes;
    var source = await db.GetCapturesAsync();
    var captures = source.Count > 0
        ? source.Select(x => new CaptureEntity(x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson)).ToList()
        : store.Captures.ToList();
    var groups = captures
        .OrderBy(x => x.CaptureTime)
        .GroupBy(x => x.DeviceId)
        .SelectMany(g =>
        {
            var buckets = new List<List<CaptureEntity>>();
            foreach (var item in g.OrderBy(x => x.CaptureTime))
            {
                if (buckets.Count == 0)
                {
                    buckets.Add([item]);
                    continue;
                }
                var lastBucket = buckets[^1];
                var last = lastBucket[^1];
                if ((item.CaptureTime - last.CaptureTime).TotalMinutes <= gapMinutes)
                {
                    lastBucket.Add(item);
                }
                else
                {
                    buckets.Add([item]);
                }
            }
            return buckets.Select((b, i) => new { DeviceId = g.Key, Index = i + 1, Rows = b });
        })
        .ToList();

    var results = groups.Select(x => new VirtualPersonEntity(
        Vid: $"V_{x.DeviceId}_{x.Index:0000}",
        FirstSeen: x.Rows.Min(r => r.CaptureTime),
        LastSeen: x.Rows.Max(r => r.CaptureTime),
        DeviceId: x.DeviceId,
        CaptureCount: x.Rows.Count)).ToList();

    var cleared = await db.ClearVirtualPersonsAsync();
    if (cleared)
    {
        foreach (var item in results)
        {
            await db.InsertVirtualPersonAsync(item.Vid, item.FirstSeen, item.LastSeen, item.DeviceId, item.CaptureCount);
        }
    }
    else
    {
        store.VirtualPersons.Clear();
        store.VirtualPersons.AddRange(results);
    }

    await db.InsertOperationAsync("系统任务", "聚类执行", $"窗口={gapMinutes}分钟, 生成VID={results.Count}");
    AddOperationLog(store, "系统任务", "聚类执行", $"窗口={gapMinutes}分钟, 生成VID={results.Count}");
    return Results.Ok(new { code = 0, msg = "聚类完成", data = new { count = results.Count, gapMinutes } });
}).RequireAuthorization("超级管理员");
cluster.MapGet("/list", async () =>
{
    var rows = await db.GetVirtualPersonsAsync();
    if (rows.Count > 0)
    {
        return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
    }
    return Results.Ok(new { code = 0, msg = "查询成功", data = store.VirtualPersons.OrderByDescending(x => x.FirstSeen) });
}).RequireAuthorization("楼栋管理员");

var operation = app.MapGroup("/api/operation");
operation.MapGet("/list", async (string? keyword, int page = 1, int pageSize = 20) =>
{
    if (page <= 0) page = 1;
    if (pageSize <= 0) pageSize = 20;
    if (pageSize > 100) pageSize = 100;
    var dbResult = await db.GetOperationsAsync(keyword, page, pageSize);
    if (dbResult.Total > 0 || dbResult.Rows.Count > 0)
        return Results.Ok(new { code = 0, msg = "查询成功", data = dbResult.Rows, pager = new { page, pageSize, total = dbResult.Total } });

    var query = store.Operations.AsEnumerable();
    if (!string.IsNullOrWhiteSpace(keyword))
        query = query.Where(x => x.Action.Contains(keyword, StringComparison.OrdinalIgnoreCase) || x.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase) || x.OperatorName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    var total = query.Count();
    var rows = query.OrderByDescending(x => x.OperationId).Skip((page - 1) * pageSize).Take(pageSize).ToArray();
    return Results.Ok(new { code = 0, msg = "查询成功", data = rows, pager = new { page, pageSize, total } });
}).RequireAuthorization("超级管理员");

dailyJudgeState.RunDailyAsync = async (today) =>
{
    const string lockKey = "aura:lock:daily-judges";
    const int lockMinutes = 60;
    string? lockToken = null;
    try
    {
        if (cache.Enabled)
            lockToken = await cache.TryAcquireLockAsync(lockKey, TimeSpan.FromMinutes(lockMinutes));
        var canRun = cache.Enabled ? lockToken is not null : true;
        if (!canRun) return;

        await RunHomeJudgeAsync(today);
        await RunGroupRentAndStayJudgeAsync(today, 2, 120);
        await RunNightAbsenceJudgeAsync(today, 23);
        await db.InsertOperationAsync("系统任务", "归寝定时任务", $"日期={today:yyyy-MM-dd}");
    }
    catch
    {
        // 定时任务异常不影响主流程
    }
    finally
    {
        if (lockToken is not null)
            await cache.ReleaseLockAsync(lockKey, lockToken);
    }
};

app.Run();

static bool VerifyHmac(string payload, string signature, string secret)
{
    if (string.IsNullOrWhiteSpace(signature))
    {
        return false;
    }

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    var local = Convert.ToHexString(hash).ToLowerInvariant();
    return string.Equals(local, signature.Trim().ToLowerInvariant(), StringComparison.Ordinal);
}

static bool IsIpAllowed(HttpRequest request, string[]? whitelist)
{
    if (whitelist is null || whitelist.Length == 0)
    {
        return true;
    }

    var remoteIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
    if (string.IsNullOrWhiteSpace(remoteIp))
    {
        return false;
    }

    return whitelist.Contains(remoteIp);
}

static string BuildJwtToken(string userName, string role, string key, string issuer, string audience, int expireMinutes)
{
    var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, userName),
        new(ClaimTypes.Name, userName),
        new(ClaimTypes.Role, role),
        new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };

    var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        issuer: issuer,
        audience: audience,
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(expireMinutes),
        signingCredentials: credentials
    );
    return new JwtSecurityTokenHandler().WriteToken(token);
}

static void AddOperationLog(AppStore store, string operatorName, string action, string detail)
{
    store.Operations.Add(new OperationEntity(
        OperationId: Interlocked.Increment(ref store.OperationSeed),
        OperatorName: operatorName,
        Action: action,
        Detail: detail,
        CreatedAt: DateTimeOffset.Now
    ));
}

static string ConvertRole(string? roleName)
{
    if (string.IsNullOrWhiteSpace(roleName)) return "building_admin";
    if (roleName.Contains("超级") || roleName.Equals("super_admin", StringComparison.OrdinalIgnoreCase)) return "super_admin";
    return "building_admin";
}

static string AttachAiResult(string metadataJson, AiExtractResult aiResult)
{
    try
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
        var map = new Dictionary<string, object?>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                map[p.Name] = p.Value.ToString();
            }
        }
        map["ai_success"] = aiResult.Success;
        map["ai_dim"] = aiResult.Dim;
        map["ai_msg"] = aiResult.Message;
        return JsonSerializer.Serialize(map);
    }
    catch
    {
        return JsonSerializer.Serialize(new
        {
            raw = metadataJson,
            ai_success = aiResult.Success,
            ai_dim = aiResult.Dim,
            ai_msg = aiResult.Message
        });
    }
}

static List<RoiEntity> ResolveCollision(List<RoiEntity> rois, long cameraId, decimal posX, decimal posY)
{
    var x = (double)posX;
    var y = (double)posY;
    var result = new List<RoiEntity>();
    foreach (var roi in rois.Where(r => r.CameraId == cameraId))
    {
        var points = ParsePoints(roi.VerticesJson);
        if (points.Count < 3) continue;
        if (IsPointInPolygon(points, x, y))
        {
            result.Add(roi);
        }
    }
    return result;
}

static List<PointVm> ParsePoints(string verticesJson)
{
    try
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(verticesJson) ? "[]" : verticesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var points = new List<PointVm>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var px = item.TryGetProperty("x", out var xEl) ? xEl.GetDouble() : 0d;
            var py = item.TryGetProperty("y", out var yEl) ? yEl.GetDouble() : 0d;
            points.Add(new PointVm(px, py));
        }
        return points;
    }
    catch
    {
        return [];
    }
}

static bool IsPointInPolygon(List<PointVm> polygon, double x, double y)
{
    var inside = false;
    for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
    {
        var xi = polygon[i].X;
        var yi = polygon[i].Y;
        var xj = polygon[j].X;
        var yj = polygon[j].Y;
        var intersect = ((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / ((yj - yi) + double.Epsilon) + xi);
        if (intersect) inside = !inside;
    }
    return inside;
}

static string ExportDatasetTitleCn(string dataset) => dataset switch
{
    "capture" => "抓拍记录",
    "alert" => "告警记录",
    "judge" => "研判记录",
    _ => "数据导出"
};

static string EscapeCell(string? text)
{
    return text?.Replace("\r", " ").Replace("\n", " ") ?? "";
}

static string ToCsvCell(string? text)
{
    var t = text ?? "";
    if (t.Contains('"')) t = t.Replace("\"", "\"\"");
    if (t.Contains(',') || t.Contains('"') || t.Contains('\n') || t.Contains('\r'))
        return $"\"{t}\"";
    return t;
}

internal sealed record LoginReq(string UserName, string Password);
internal sealed record DeviceRegisterReq(string Name, string Ip, int Port, string Brand, string Protocol);
internal sealed record CaptureReq(long DeviceId, int ChannelNo, DateTimeOffset CaptureTime, string ImageBase64, string MetadataJson);
internal sealed record CaptureMockReq(long DeviceId, int ChannelNo, string MetadataJson);
internal sealed record RoiReq(long CameraId, long RoomNodeId, string VerticesJson);
internal sealed record CreateAlertReq(string AlertType, string Detail);
internal sealed record RoleCreateReq(string RoleName, string? PermissionJson);
internal sealed record UserCreateReq(string UserName, string Password, long RoleId);
internal sealed record UserStatusReq(int Status);
internal sealed record CampusCreateReq(long? ParentId, string LevelType, string NodeName);
internal sealed record CampusUpdateReq(string NodeName);
internal sealed record FloorCreateReq(long NodeId, string FilePath, decimal ScaleRatio);
internal sealed record CameraCreateReq(long FloorId, long DeviceId, int ChannelNo, decimal PosX, decimal PosY);
internal sealed record RetryProcessReq(int Take);
internal sealed record ClusterRunReq(int GapMinutes);
internal sealed record VectorExtractReq(string ImageBase64, string? MetadataJson);
internal sealed record VectorSearchReq(List<float> Feature, int TopK);
internal sealed record SpaceCollisionReq(string? Vid, long CameraId, decimal PosX, decimal PosY, DateTimeOffset? EventTime);
internal sealed record JudgeRunReq(string? Date);
internal sealed record JudgeAbnormalReq(string? Date, int GroupThreshold, int StayMinutes);
internal sealed record JudgeNightReq(string? Date, int CutoffHour);
internal sealed record JudgeRunResult(DateOnly JudgeDate, string JudgeType, int SourceCount, int ResultCount);
internal sealed record OpsAlertNotifyTestReq(string? AlertType, string? Detail);
internal sealed record PointVm(double X, double Y);

internal sealed record DeviceEntity(long DeviceId, string Name, string Ip, int Port, string Brand, string Protocol, string Status, DateTimeOffset CreatedAt);
internal sealed record CaptureEntity(long CaptureId, long DeviceId, int ChannelNo, DateTimeOffset CaptureTime, string MetadataJson);
internal sealed record RoiEntity(long RoiId, long CameraId, long RoomNodeId, string VerticesJson, DateTimeOffset CreatedAt);
internal sealed record AlertEntity(long AlertId, string AlertType, string Detail, DateTimeOffset CreatedAt);
internal sealed record OperationEntity(long OperationId, string OperatorName, string Action, string Detail, DateTimeOffset CreatedAt);
internal sealed record RoleEntity(long RoleId, string RoleName, string PermissionJson);
internal sealed record UserEntity(long UserId, string UserName, string RoleName, int Status, DateTimeOffset CreatedAt);
internal sealed record CampusNodeEntity(long NodeId, long? ParentId, string LevelType, string NodeName);
internal sealed record FloorEntity(long FloorId, long NodeId, string FilePath, decimal ScaleRatio);
internal sealed record CameraEntity(long CameraId, long FloorId, long DeviceId, int ChannelNo, decimal PosX, decimal PosY);
internal sealed record TrackEventEntity(long EventId, string Vid, long CameraId, long RoiId, DateTimeOffset EventTime);
internal sealed record JudgeResultEntity(long JudgeId, string Vid, long RoomId, string JudgeType, DateOnly JudgeDate, string DetailJson, DateTimeOffset CreatedAt);
internal sealed record CampusNodeVm(long NodeId, long? ParentId, string LevelType, string NodeName, List<CampusNodeVm> Children);
internal sealed record VirtualPersonEntity(string Vid, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long DeviceId, int CaptureCount);

internal sealed class AppStore
{
    public long DeviceSeed = 1;
    public long CaptureSeed;
    public long RoiSeed;
    public long AlertSeed;
    public long OperationSeed;
    public long RoleSeed = 2;
    public long UserSeed = 1;
    public long CampusSeed = 4;
    public long FloorSeed;
    public long CameraSeed;
    public long TrackEventSeed;
    public long JudgeSeed;
    public List<DeviceEntity> Devices { get; } =
    [
        new(1, "1号NVR", "127.0.0.1", 8000, "hikvision", "isapi", "online", DateTimeOffset.Now)
    ];
    public List<CaptureEntity> Captures { get; } = [];
    public List<RoiEntity> Rois { get; } = [];
    public List<AlertEntity> Alerts { get; } = [];
    public List<OperationEntity> Operations { get; } = [];
    public List<RoleEntity> Roles { get; } =
    [
        new(1, "super_admin", "[\"all\"]"),
        new(2, "building_admin", "[\"device\",\"roi\",\"track\",\"alert\",\"stats\"]")
    ];
    public List<UserEntity> Users { get; } =
    [
        new(1, "admin", "super_admin", 1, DateTimeOffset.Now)
    ];
    public List<CampusNodeEntity> CampusNodes { get; } =
    [
        new(1, null, "campus", "一号园区"),
        new(2, 1, "building", "A栋"),
        new(3, 2, "floor", "1层"),
        new(4, 3, "room", "101室")
    ];
    public List<FloorEntity> Floors { get; } = [];
    public List<CameraEntity> Cameras { get; } = [];
    public List<TrackEventEntity> TrackEvents { get; } = [];
    public List<JudgeResultEntity> JudgeResults { get; } = [];
    public List<VirtualPersonEntity> VirtualPersons { get; } = [];
}

[Authorize(Policy = "楼栋管理员")]
internal sealed class EventHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        if (!string.IsNullOrWhiteSpace(role))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"role:{role}");
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        if (!string.IsNullOrWhiteSpace(role))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"role:{role}");
        }
        await base.OnDisconnectedAsync(exception);
    }
}

internal sealed class DailyJudgeScheduleState
{
    public Func<DateOnly, Task>? RunDailyAsync { get; set; }
}

internal sealed class DailyJudgeHostedService : BackgroundService
{
    private readonly DailyJudgeScheduleState _state;
    private readonly ILogger<DailyJudgeHostedService> _logger;
    private DateOnly? _lastDate;

    public DailyJudgeHostedService(DailyJudgeScheduleState state, ILogger<DailyJudgeHostedService> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var today = DateOnly.FromDateTime(now);
                if (now.Hour == 0 && now.Minute < 5 && _lastDate != today)
                {
                    var run = _state.RunDailyAsync;
                    if (run is not null)
                    {
                        var startedAt = DateTimeOffset.Now;
                        await run(today);
                        _lastDate = today;
                        var costMs = (long)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
                        _logger.LogInformation("每日研判后台任务执行完成。date={Date}, costMs={CostMs}", today, costMs);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "每日研判后台任务执行异常。");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
