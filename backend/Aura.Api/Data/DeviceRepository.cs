/* 文件：设备仓储 | File: Device repository */
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal sealed class DeviceRepository
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<DeviceRepository>? _logger;

    public DeviceRepository(PgSqlConnectionFactory connectionFactory, ILogger<DeviceRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection() => _connectionFactory.CreateConnection();

    public async Task<List<DbDevice>> GetDevicesAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbDevice>(
                """
                SELECT device_id AS DeviceId, name AS Name, ip AS Ip, port AS Port,
                       brand AS Brand, protocol AS Protocol, status AS Status, created_at AS CreatedAt
                FROM nvr_device
                ORDER BY device_id DESC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询设备列表失败。");
            return [];
        }
    }

    public async Task<DbDevice?> GetDeviceByIdAsync(long deviceId)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<DbDevice>(
                """
                SELECT device_id AS DeviceId, name AS Name, ip AS Ip, port AS Port,
                       brand AS Brand, protocol AS Protocol, status AS Status, created_at AS CreatedAt
                FROM nvr_device
                WHERE device_id=@DeviceId
                """,
                new { DeviceId = deviceId });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库按ID查询设备失败。deviceId={DeviceId}", deviceId);
            return null;
        }
    }

    public async Task<long?> InsertDeviceAsync(string name, string ip, int port, string brand, string protocol, string status)
    {
        try
        {
            await using var conn = CreateConnection();
            var id = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO nvr_device(name, ip, port, brand, protocol, status, created_at)
                VALUES(@Name, @Ip, @Port, @Brand, @Protocol, @Status, NOW())
                RETURNING device_id
                """,
                new { Name = name, Ip = ip, Port = port, Brand = brand, Protocol = protocol, Status = status });
            return id;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入设备失败。name={Name}, ip={Ip}, port={Port}", name, ip, port);
            return null;
        }
    }

    public async Task<string?> GetDeviceHmacSecretAsync(long deviceId)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<string?>(
                """
                SELECT COALESCE(hmac_secret, '') AS HmacSecret
                FROM nvr_device
                WHERE device_id=@DeviceId
                LIMIT 1
                """,
                new { DeviceId = deviceId });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询设备级 HMAC 密钥失败。deviceId={DeviceId}", deviceId);
            return null;
        }
    }
}
