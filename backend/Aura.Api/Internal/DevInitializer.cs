/* 文件：开发环境初始化（DevInitializer.cs） | File: Development initializer */
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aura.Api.Data;
using Aura.Api.Models;
using Dapper;

namespace Aura.Api.Internal;

internal static class DevInitializer
{
    public static async Task InitializeDevDataAsync(WebApplication app)
    {
        try
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DevInitializer));
            var usersRepository = app.Services.GetRequiredService<UserAuthRepository>();
            var resetAdminPasswordOnce = app.Configuration.GetValue("Dev:ResetAdminPasswordOnce", false);
            var users = await usersRepository.GetUsersAsync();
            if (users.Count == 0 || resetAdminPasswordOnce)
            {
                var nextPassword = ResolveDevAdminPassword();
                var hash = BCrypt.Net.BCrypt.HashPassword(nextPassword);
                var ok = false;
                if (users.Count == 0)
                {
                    var id = await usersRepository.InsertUserAsync("admin", "系统管理员", hash, 1);
                    ok = id.HasValue;
                }
                else
                {
                    ok = await usersRepository.UpdateUserPasswordByUserNameAsync("admin", hash, mustChangePassword: false);
                }

                if (ok)
                {
                    if (Environment.GetEnvironmentVariable("AURA_ADMIN_PASSWORD") is { Length: > 0 })
                    {
                        logger.LogInformation("开发环境管理员已配置：用户名 admin，密码已使用环境变量 AURA_ADMIN_PASSWORD。");
                    }
                    else
                    {
                        logger.LogInformation("开发环境管理员已配置：用户名 admin，临时密码 {Password}", nextPassword);
                    }

                    if (resetAdminPasswordOnce)
                    {
                        TryDisableDevResetFlag(app);
                    }
                }
            }

            await TrySeedRecentTestDataAsync(app, logger);
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DevInitializer));
            logger.LogError(ex, "开发环境数据初始化失败");
        }
    }

    private static async Task TrySeedRecentTestDataAsync(WebApplication app, ILogger logger)
    {
        // 仅开发环境：补齐“近 7 日”数据，避免统计驾驶舱为空。
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        var seedEnabled = app.Configuration.GetValue("Dev:SeedRecentTestData", true);
        if (!seedEnabled)
        {
            return;
        }

        var store = app.Services.GetRequiredService<AppStore>();
        var pg = app.Services.GetRequiredService<PgSqlConnectionFactory>();

        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        var rangeStart = today.AddDays(-6).ToDateTime(TimeOnly.MinValue);
        var rangeEnd = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var start = new DateTimeOffset(rangeStart);
        var end = new DateTimeOffset(rangeEnd);

        // 1) 优先写入数据库（若数据库可用）；否则写入内存库（用于 AllowInMemoryDataFallback 的场景）
        if (pg.IsConfigured && await CanConnectToPgAsync(pg, logger))
        {
            await SeedPgAsync(app, logger, start, end);
        }
        else
        {
            SeedInMemory(store, logger, start, end);
        }
    }

    private static async Task<bool> CanConnectToPgAsync(PgSqlConnectionFactory pg, ILogger logger)
    {
        try
        {
            await using var conn = pg.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteScalarAsync<int>("SELECT 1");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PgSql 未就绪或连接失败，跳过数据库测试数据写入。");
            return false;
        }
    }

    private static async Task SeedPgAsync(WebApplication app, ILogger logger, DateTimeOffset start, DateTimeOffset end)
    {
        var pg = app.Services.GetRequiredService<PgSqlConnectionFactory>();
        var devicesRepo = app.Services.GetRequiredService<DeviceRepository>();
        var campusRepo = app.Services.GetRequiredService<CampusResourceRepository>();
        var captureRepo = app.Services.GetRequiredService<CaptureRepository>();
        var monitoringRepo = app.Services.GetRequiredService<MonitoringRepository>();
        var now = DateTimeOffset.Now;

        // 幂等：若近 7 日已有一定量数据，则不重复灌入
        await using var conn = pg.CreateConnection();
        await conn.OpenAsync();
        var captureRecent = await conn.ExecuteScalarAsync<long?>(
            "SELECT COUNT(1) FROM capture_record WHERE capture_time >= @Start AND capture_time < @End",
            new { Start = start.ToUniversalTime(), End = end.ToUniversalTime() }) ?? 0;
        var alertRecent = await conn.ExecuteScalarAsync<long?>(
            "SELECT COUNT(1) FROM alert_record WHERE created_at >= @Start AND created_at < @End",
            new { Start = start.ToUniversalTime(), End = end.ToUniversalTime() }) ?? 0;
        if (captureRecent >= 80 && alertRecent >= 20)
        {
            logger.LogInformation("开发环境近 7 日测试数据已存在（抓拍={CaptureCount}, 告警={AlertCount}），跳过重复灌入。", captureRecent, alertRecent);
            return;
        }

        // 设备：确保至少 3 台（用于 Top10 维度）
        var dbDevices = await devicesRepo.GetDevicesAsync();
        var deviceIds = new List<long>();
        if (dbDevices.Count == 0)
        {
            var d1 = await devicesRepo.InsertDeviceAsync("1号NVR", "127.0.0.1", 8000, "hikvision", "isapi", "online");
            var d2 = await devicesRepo.InsertDeviceAsync("2号NVR", "127.0.0.1", 8001, "hikvision", "isapi", "online");
            var d3 = await devicesRepo.InsertDeviceAsync("3号NVR", "127.0.0.1", 8002, "dahua", "isapi", "offline");
            deviceIds.AddRange([d1 ?? 1, d2 ?? 2, d3 ?? 3]);
        }
        else
        {
            deviceIds.AddRange(dbDevices.Select(x => x.DeviceId).Take(5));
            if (deviceIds.Count < 1)
            {
                var d1 = await devicesRepo.InsertDeviceAsync("1号NVR", "127.0.0.1", 8000, "hikvision", "isapi", "online");
                deviceIds.Add(d1 ?? 1);
            }
        }

        // 园区/楼层/摄像头：尽量补齐，便于其他页面（资源树/布点/ROI/轨迹）有可展示的数据
        var campusNodes = await campusRepo.GetCampusNodesAsync();
        long campusId;
        long buildingId;
        long floorNodeId;
        long roomNodeId;
        if (campusNodes.Count == 0)
        {
            campusId = (await campusRepo.InsertCampusNodeAsync(null, "campus", "一号园区")) ?? 1;
            buildingId = (await campusRepo.InsertCampusNodeAsync(campusId, "building", "A栋")) ?? 2;
            floorNodeId = (await campusRepo.InsertCampusNodeAsync(buildingId, "floor", "1层")) ?? 3;
            roomNodeId = (await campusRepo.InsertCampusNodeAsync(floorNodeId, "room", "101室")) ?? 4;
        }
        else
        {
            campusId = campusNodes.FirstOrDefault(x => x.LevelType == "campus")?.NodeId ?? campusNodes.First().NodeId;
            buildingId = campusNodes.FirstOrDefault(x => x.LevelType == "building")?.NodeId
                         ?? campusNodes.FirstOrDefault(x => x.ParentId == campusId)?.NodeId
                         ?? campusId;
            floorNodeId = campusNodes.FirstOrDefault(x => x.LevelType == "floor")?.NodeId
                          ?? campusNodes.FirstOrDefault(x => x.ParentId == buildingId)?.NodeId
                          ?? buildingId;
            roomNodeId = campusNodes.FirstOrDefault(x => x.LevelType == "room")?.NodeId
                         ?? campusNodes.FirstOrDefault(x => x.ParentId == floorNodeId)?.NodeId
                         ?? floorNodeId;
        }

        var floors = await campusRepo.GetFloorsAsync();
        var floorId = floors.FirstOrDefault()?.FloorId
                      ?? (await campusRepo.InsertFloorAsync(floorNodeId, "storage/dev/floor-demo.png", 1.0m))
                      ?? 1;
        var cameras = await campusRepo.GetCamerasAsync();
        long cameraId;
        if (cameras.Count == 0)
        {
            cameraId = (await campusRepo.InsertCameraAsync(floorId, deviceIds[0], 1, 0.35m, 0.42m)) ?? 1;
        }
        else
        {
            cameraId = cameras[0].CameraId;
        }

        var rois = await captureRepo.GetRoisAsync();
        var roiId = rois.FirstOrDefault()?.RoiId
                    ?? (await captureRepo.InsertRoiAsync(cameraId, roomNodeId, "[{\"x\":0.12,\"y\":0.18},{\"x\":0.76,\"y\":0.18},{\"x\":0.76,\"y\":0.72},{\"x\":0.12,\"y\":0.72}]"))
                    ?? 1;

        // 生成近 7 日抓拍：按“天 × 设备”随机分布
        var rand = new Random(unchecked((int)DateTimeOffset.Now.ToUnixTimeSeconds()));
        var totalCapturesInserted = 0;
        for (var day = 0; day < 7; day++)
        {
            var dayStart = start.Date.AddDays(day);
            foreach (var did in deviceIds)
            {
                var baseCount = did == deviceIds[0] ? 28 : did == deviceIds[1] ? 18 : 8;
                var count = Math.Max(1, baseCount + rand.Next(-6, 7));
                for (var i = 0; i < count; i++)
                {
                    var t = new DateTimeOffset(dayStart.AddHours(8).AddMinutes(rand.Next(0, 720))).ToOffset(now.Offset);
                    var channelNo = 1 + rand.Next(0, 2);
                    var meta = JsonSerializer.Serialize(new
                    {
                        source = "dev-seed",
                        deviceId = did,
                        channelNo,
                        score = Math.Round(rand.NextDouble() * 0.4 + 0.55, 3),
                        vid = $"V{rand.Next(1000, 9999)}"
                    });
                    var id = await captureRepo.InsertCaptureAsync(did, channelNo, t, meta, imagePath: null);
                    if (id.HasValue) totalCapturesInserted++;
                }
            }
        }

        // 生成近 7 日告警：覆盖多类型，并打散 created_at
        var alertTypes = new[] { "夜不归宿", "异常滞留", "群租预警", "陌生人", "重点人员" };
        var totalAlertsInserted = 0;
        for (var day = 0; day < 7; day++)
        {
            var dayStart = start.Date.AddDays(day);
            var alertCount = Math.Max(2, 6 + rand.Next(-2, 5));
            for (var i = 0; i < alertCount; i++)
            {
                var t = new DateTimeOffset(dayStart.AddHours(9).AddMinutes(rand.Next(0, 660))).ToOffset(now.Offset);
                var type = alertTypes[rand.Next(0, alertTypes.Length)];
                var detail = $"dev-seed：类型={type}，房间={roomNodeId}，时间={t:yyyy-MM-dd HH:mm}";
                var id = await monitoringRepo.InsertAlertWithTimeAsync(type, detail, t);
                if (id.HasValue) totalAlertsInserted++;
            }
        }

        // 轨迹事件：用于轨迹回放/以图搜轨等页面的基础数据（不追求真实，仅用于联调）
        var trackInserted = 0;
        for (var day = 0; day < 5; day++)
        {
            var dayStart = start.Date.AddDays(Math.Max(0, day + 1));
            for (var i = 0; i < 12; i++)
            {
                var t = new DateTimeOffset(dayStart.AddHours(10).AddMinutes(rand.Next(0, 600))).ToOffset(now.Offset);
                var vid = $"V{1000 + rand.Next(0, 40)}";
                var eid = await captureRepo.InsertTrackEventAsync(vid, cameraId, roiId, t);
                if (eid.HasValue) trackInserted++;
            }
        }

        // 研判结果：近 3 天补几条，便于研判/告警联动页面调试
        var judgeInserted = 0;
        for (var d = 0; d < 3; d++)
        {
            var date = DateOnly.FromDateTime(DateTimeOffset.Now.AddDays(-d).DateTime);
            var vid = $"V{2000 + d}";
            var detailJson = JsonSerializer.Serialize(new { source = "dev-seed", roomId = roomNodeId, date = date.ToString("yyyy-MM-dd") });
            var jid = await monitoringRepo.InsertJudgeResultAsync(vid, roomNodeId, "归寝研判", date, detailJson);
            if (jid.HasValue) judgeInserted++;
        }

        // 虚拟人员：用于“虚拟人员/聚类”类页面的基础展示
        try
        {
            await monitoringRepo.ClearVirtualPersonsAsync();
            for (var i = 0; i < 10; i++)
            {
                var firstSeen = DateTimeOffset.Now.AddDays(-rand.Next(0, 6)).AddMinutes(-rand.Next(0, 600));
                var lastSeen = firstSeen.AddMinutes(rand.Next(5, 180));
                await monitoringRepo.InsertVirtualPersonAsync($"V{3000 + i}", firstSeen, lastSeen, deviceIds[rand.Next(0, deviceIds.Count)], rand.Next(3, 22));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "写入虚拟人员测试数据失败（可忽略）。");
        }

        logger.LogInformation(
            "已补齐开发环境近 7 日测试数据：抓拍新增={CaptureCount}，告警新增={AlertCount}，轨迹新增={TrackCount}，研判新增={JudgeCount}。",
            totalCapturesInserted,
            totalAlertsInserted,
            trackInserted,
            judgeInserted);
    }

    private static void SeedInMemory(AppStore store, ILogger logger, DateTimeOffset start, DateTimeOffset end)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        var existingCapture = store.Captures.Count(x => x.CaptureTime >= start && x.CaptureTime < end);
        var existingAlert = store.Alerts.Count(x => x.CreatedAt >= start && x.CreatedAt < end);
        if (existingCapture >= 80 && existingAlert >= 20)
        {
            logger.LogInformation("内存库近 7 日测试数据已存在（抓拍={CaptureCount}, 告警={AlertCount}），跳过重复灌入。", existingCapture, existingAlert);
            return;
        }

        if (store.Devices.Count == 0)
        {
            store.Devices.Add(new DeviceEntity(1, "1号NVR", "127.0.0.1", 8000, "hikvision", "isapi", "online", now));
        }
        if (store.Devices.Count < 3)
        {
            var nextId = store.Devices.Max(x => x.DeviceId) + 1;
            store.Devices.Add(new DeviceEntity(nextId, "2号NVR", "127.0.0.1", 8001, "hikvision", "isapi", "online", now));
            store.Devices.Add(new DeviceEntity(nextId + 1, "3号NVR", "127.0.0.1", 8002, "dahua", "isapi", "offline", now));
        }

        var deviceIds = store.Devices.Select(x => x.DeviceId).Take(5).ToList();
        var rand = new Random(unchecked((int)DateTimeOffset.Now.ToUnixTimeSeconds()));

        var totalCapturesInserted = 0;
        for (var day = 0; day < 7; day++)
        {
            var dayStart = start.Date.AddDays(day);
            foreach (var did in deviceIds)
            {
                var baseCount = did == deviceIds[0] ? 28 : did == deviceIds[1] ? 18 : 8;
                var count = Math.Max(1, baseCount + rand.Next(-6, 7));
                for (var i = 0; i < count; i++)
                {
                    var t = new DateTimeOffset(dayStart.AddHours(8).AddMinutes(rand.Next(0, 720))).ToOffset(now.Offset);
                    var channelNo = 1 + rand.Next(0, 2);
                    var meta = JsonSerializer.Serialize(new
                    {
                        source = "dev-seed",
                        deviceId = did,
                        channelNo,
                        score = Math.Round(rand.NextDouble() * 0.4 + 0.55, 3),
                        vid = $"V{rand.Next(1000, 9999)}"
                    });
                    store.Captures.Add(new CaptureEntity(
                        CaptureId: Interlocked.Increment(ref store.CaptureSeed),
                        DeviceId: did,
                        ChannelNo: channelNo,
                        CaptureTime: t,
                        MetadataJson: meta,
                        ImagePath: null));
                    totalCapturesInserted++;
                }
            }
        }

        var alertTypes = new[] { "夜不归宿", "异常滞留", "群租预警", "陌生人", "重点人员" };
        var totalAlertsInserted = 0;
        for (var day = 0; day < 7; day++)
        {
            var dayStart = start.Date.AddDays(day);
            var alertCount = Math.Max(2, 6 + rand.Next(-2, 5));
            for (var i = 0; i < alertCount; i++)
            {
                var t = new DateTimeOffset(dayStart.AddHours(9).AddMinutes(rand.Next(0, 660))).ToOffset(now.Offset);
                var type = alertTypes[rand.Next(0, alertTypes.Length)];
                store.Alerts.Add(new AlertEntity(
                    AlertId: Interlocked.Increment(ref store.AlertSeed),
                    AlertType: type,
                    Detail: $"dev-seed：类型={type}，时间={t:yyyy-MM-dd HH:mm}",
                    CreatedAt: t));
                totalAlertsInserted++;
            }
        }

        logger.LogInformation("已补齐内存库近 7 日测试数据：抓拍新增={CaptureCount}，告警新增={AlertCount}。", totalCapturesInserted, totalAlertsInserted);
    }

    private static string ResolveDevAdminPassword()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("AURA_ADMIN_PASSWORD") ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_+=";
        var all = upper + lower + digits + symbols;
        var chars = new[]
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        }.ToList();

        while (chars.Count < 16)
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars.ToArray());
    }

    private static void TryDisableDevResetFlag(WebApplication app)
    {
        try
        {
            var path = Path.Combine(app.Environment.ContentRootPath, "appsettings.Development.json");
            if (!File.Exists(path)) return;
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node?["Dev"] is JsonObject dev)
            {
                dev["ResetAdminPasswordOnce"] = false;
                File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // 开发环境下忽略回写失败，避免影响主流程
        }
    }
}
