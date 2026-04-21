/* 文件：园区资源仓储 | File: Campus resource repository */
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal sealed class CampusResourceRepository
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<CampusResourceRepository>? _logger;

    public CampusResourceRepository(PgSqlConnectionFactory connectionFactory, ILogger<CampusResourceRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection() => _connectionFactory.CreateConnection();

    public async Task<List<DbCampusNode>> GetCampusNodesAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbCampusNode>(
                """
                SELECT node_id AS NodeId, parent_id AS ParentId, level_type AS LevelType, node_name AS NodeName
                FROM dict_campus
                ORDER BY node_id ASC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询园区资源树失败。");
            return [];
        }
    }

    public async Task<long?> InsertCampusNodeAsync(long? parentId, string levelType, string nodeName)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO dict_campus(parent_id, level_type, node_name, created_at)
                VALUES(@ParentId, @LevelType, @NodeName, NOW())
                RETURNING node_id
                """,
                new { ParentId = parentId, LevelType = levelType, NodeName = nodeName });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入园区节点失败。levelType={LevelType}, nodeName={NodeName}", levelType, nodeName);
            return null;
        }
    }

    public async Task<bool> UpdateCampusNodeAsync(long nodeId, string nodeName)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                "UPDATE dict_campus SET node_name=@NodeName WHERE node_id=@NodeId",
                new { NodeName = nodeName, NodeId = nodeId });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库更新园区节点失败。nodeId={NodeId}", nodeId);
            return false;
        }
    }

    public async Task<bool> DeleteCampusNodeAsync(long nodeId)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                "DELETE FROM dict_campus WHERE node_id=@NodeId OR parent_id=@NodeId",
                new { NodeId = nodeId });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库删除园区节点失败。nodeId={NodeId}", nodeId);
            return false;
        }
    }

    public async Task<List<DbFloor>> GetFloorsAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbFloor>(
                """
                SELECT floor_id AS FloorId, node_id AS NodeId, file_path AS FilePath, scale_ratio AS ScaleRatio
                FROM map_floor
                ORDER BY floor_id DESC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询楼层列表失败。");
            return [];
        }
    }

    public async Task<long?> InsertFloorAsync(long nodeId, string filePath, decimal scaleRatio)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO map_floor(node_id, file_path, scale_ratio, created_at)
                VALUES(@NodeId, @FilePath, @ScaleRatio, NOW())
                RETURNING floor_id
                """,
                new { NodeId = nodeId, FilePath = filePath, ScaleRatio = scaleRatio });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入楼层失败。nodeId={NodeId}", nodeId);
            return null;
        }
    }

    public async Task<List<DbCamera>> GetCamerasAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbCamera>(
                """
                SELECT camera_id AS CameraId, floor_id AS FloorId, device_id AS DeviceId, channel_no AS ChannelNo,
                       pos_x AS PosX, pos_y AS PosY
                FROM map_camera
                ORDER BY camera_id DESC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询摄像头列表失败。");
            return [];
        }
    }

    public async Task<List<DbCamera>> GetCamerasByDeviceIdAsync(long deviceId)
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbCamera>(
                """
                SELECT camera_id AS CameraId, floor_id AS FloorId, device_id AS DeviceId, channel_no AS ChannelNo,
                       pos_x AS PosX, pos_y AS PosY
                FROM map_camera
                WHERE device_id = @DeviceId
                ORDER BY camera_id DESC
                """,
                new { DeviceId = deviceId });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库按设备查询摄像头列表失败。deviceId={DeviceId}", deviceId);
            return [];
        }
    }

    public async Task<long?> InsertCameraAsync(long floorId, long deviceId, int channelNo, decimal posX, decimal posY)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO map_camera(floor_id, device_id, channel_no, pos_x, pos_y, created_at)
                VALUES(@FloorId, @DeviceId, @ChannelNo, @PosX, @PosY, NOW())
                RETURNING camera_id
                """,
                new { FloorId = floorId, DeviceId = deviceId, ChannelNo = channelNo, PosX = posX, PosY = posY });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入摄像头失败。floorId={FloorId}, deviceId={DeviceId}, channelNo={ChannelNo}", floorId, deviceId, channelNo);
            return null;
        }
    }
}
