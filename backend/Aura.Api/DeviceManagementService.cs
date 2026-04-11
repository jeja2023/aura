using System.Text.Json;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Models;

namespace Aura.Api;

internal sealed class DeviceManagementService
{
    private readonly AppStore _store;
    private readonly PgSqlStore _db;
    private readonly RedisCacheService _cache;
    private readonly ILogger<DeviceManagementService> _logger;

    public DeviceManagementService(AppStore store, PgSqlStore db, RedisCacheService cache, ILogger<DeviceManagementService> logger)
    {
        _store = store;
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IResult> GetDevicesAsync()
    {
        var cached = await _cache.GetAsync("device:list");
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var cacheRows = JsonSerializer.Deserialize<List<DbDevice>>(cached);
            if (cacheRows is { Count: > 0 })
            {
                _logger.LogInformation("从缓存中获取设备列表");
                return Results.Ok(new { code = 0, msg = "查询成功", data = cacheRows, from = "redis" });
            }
        }

        var rows = await _db.GetDevicesAsync();
        if (rows.Count > 0)
        {
            await _cache.SetAsync("device:list", JsonSerializer.Serialize(rows), TimeSpan.FromMinutes(3));
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }

        var mockRows = _store.Devices.OrderByDescending(x => x.DeviceId).ToList();
        return Results.Ok(new { code = 0, msg = "查询成功", data = mockRows });
    }

    public async Task<IResult> RegisterDeviceAsync(DeviceRegisterReq req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Ip))
        {
            return Results.BadRequest(new { code = 40002, msg = "设备名称和IP不能为空" });
        }

        var entity = new DeviceEntity(Interlocked.Increment(ref _store.DeviceSeed), req.Name, req.Ip, req.Port, req.Brand, req.Protocol, "offline", DateTimeOffset.Now);
        var dbId = await _db.InsertDeviceAsync(entity.Name, entity.Ip, entity.Port, entity.Brand, entity.Protocol, entity.Status);
        
        if (dbId.HasValue)
        {
            var savedDb = entity with { DeviceId = dbId.Value };
            await _db.InsertOperationAsync("系统管理员", "设备注册", $"设备={savedDb.Name}, IP={savedDb.Ip}");
            if (_cache.Enabled) await _cache.DeleteAsync("device:list");
            _logger.LogInformation("设备注册成功：{DeviceName}, IP: {Ip}", savedDb.Name, savedDb.Ip);
            return Results.Ok(new { code = 0, msg = "设备注册成功", data = savedDb });
        }

        _store.Devices.Add(entity);
        AddOperationLog("系统管理员", "设备注册", $"设备={entity.Name}, IP={entity.Ip}");
        if (_cache.Enabled) await _cache.DeleteAsync("device:list");
        _logger.LogWarning("数据库写入失败，已将设备注册到内存库：{DeviceName}", entity.Name);
        return Results.Ok(new { code = 0, msg = "设备注册成功", data = entity });
    }

    public IResult PingDevice(long deviceId)
    {
        var idx = _store.Devices.FindIndex(x => x.DeviceId == deviceId);
        if (idx < 0)
        {
            _logger.LogWarning("心跳更新失败：设备ID {DeviceId} 不存在", deviceId);
            return Results.NotFound(new { code = 40401, msg = "设备不存在" });
        }

        var entity = _store.Devices[idx];
        var updated = entity with { Status = "online" };
        _store.Devices[idx] = updated;
        AddOperationLog("系统管理员", "设备心跳", $"设备={updated.Name}上线");
        _logger.LogInformation("设备心跳更新：{DeviceName} 上线", updated.Name);
        return Results.Ok(new { code = 0, msg = "设备状态更新成功", data = updated });
    }

    private void AddOperationLog(string operatorName, string action, string detail)
    {
        _store.Operations.Add(new OperationEntity(
            OperationId: Interlocked.Increment(ref _store.OperationSeed),
            OperatorName: operatorName,
            Action: action,
            Detail: detail,
            CreatedAt: DateTimeOffset.Now));
    }
}
