using Dapper;
using MySqlConnector;

namespace Aura.Api.Data;

internal sealed class MySqlStore
{
    private readonly string _connectionString;

    public MySqlStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    private MySqlConnection CreateConnection() => new(_connectionString);

    public async Task<DbUser?> FindUserAsync(string userName)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<DbUser>(
                """
                SELECT u.user_name AS UserName, u.password_hash AS PasswordHash, r.role_name AS RoleName
                FROM sys_user u
                LEFT JOIN sys_role r ON u.role_id = r.role_id
                WHERE u.user_name = @userName AND u.status = 1
                LIMIT 1
                """,
                new { userName });
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<DbDevice>> GetDevicesAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbDevice>(
                """
                SELECT device_id AS DeviceId, name AS Name, ip AS Ip, port AS Port,
                       brand AS Brand, protocol AS `Protocol`, status AS Status, created_at AS CreatedAt
                FROM nvr_device
                ORDER BY device_id DESC
                """);
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertDeviceAsync(string name, string ip, int port, string brand, string protocol, string status)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO nvr_device(name, ip, port, brand, protocol, status, created_at)
                VALUES(@Name, @Ip, @Port, @Brand, @Protocol, @Status, NOW())
                """,
                new { Name = name, Ip = ip, Port = port, Brand = brand, Protocol = protocol, Status = status });
            var id = await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
            return id;
        }
        catch
        {
            return null;
        }
    }

    public async Task<long?> InsertCaptureAsync(long deviceId, int channelNo, DateTimeOffset captureTime, string metadataJson)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO capture_record(device_id, channel_no, capture_time, metadata_json, created_at)
                VALUES(@DeviceId, @ChannelNo, @CaptureTime, @MetadataJson, NOW())
                """,
                new { DeviceId = deviceId, ChannelNo = channelNo, CaptureTime = captureTime, MetadataJson = metadataJson });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<DbCapture>> GetCapturesAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbCapture>(
                """
                SELECT capture_id AS CaptureId, device_id AS DeviceId, channel_no AS ChannelNo,
                       capture_time AS CaptureTime, COALESCE(CAST(metadata_json AS CHAR), '{}') AS MetadataJson
                FROM capture_record
                ORDER BY capture_id DESC
                LIMIT 500
                """);
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertAlertAsync(string alertType, string detail)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO alert_record(alert_type, detail_json, created_at)
                VALUES(@AlertType, @Detail, NOW())
                """,
                new { AlertType = alertType, Detail = detail });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<DbAlert>> GetAlertsAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbAlert>(
                """
                SELECT alert_id AS AlertId, alert_type AS AlertType,
                       COALESCE(CAST(detail_json AS CHAR), '') AS Detail, created_at AS CreatedAt
                FROM alert_record
                ORDER BY alert_id DESC
                LIMIT 500
                """);
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertOperationAsync(string operatorName, string action, string detail)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO log_operation(operator_name, action_type, action_detail, created_at)
                VALUES(@OperatorName, @Action, @Detail, NOW())
                """,
                new { OperatorName = operatorName, Action = action, Detail = detail });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<(List<DbOperation> Rows, int Total)> GetOperationsAsync(string? keyword, int page, int pageSize)
    {
        try
        {
            await using var conn = CreateConnection();
            var filter = string.IsNullOrWhiteSpace(keyword) ? "" : " WHERE operator_name LIKE @kw OR action_type LIKE @kw OR action_detail LIKE @kw ";
            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(1) FROM log_operation {filter}",
                new { kw = $"%{keyword}%" });
            var rows = await conn.QueryAsync<DbOperation>(
                $"""
                SELECT op_id AS OperationId, operator_name AS OperatorName, action_type AS Action,
                       action_detail AS Detail, created_at AS CreatedAt
                FROM log_operation
                {filter}
                ORDER BY op_id DESC
                LIMIT @offset, @pageSize
                """,
                new { kw = $"%{keyword}%", offset = (page - 1) * pageSize, pageSize });
            return (rows.ToList(), total);
        }
        catch
        {
            return ([], 0);
        }
    }

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
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertCampusNodeAsync(long? parentId, string levelType, string nodeName)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO dict_campus(parent_id, level_type, node_name, created_at)
                VALUES(@ParentId, @LevelType, @NodeName, NOW())
                """,
                new { ParentId = parentId, LevelType = levelType, NodeName = nodeName });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
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
        catch
        {
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
        catch
        {
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
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertFloorAsync(long nodeId, string filePath, decimal scaleRatio)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO map_floor(node_id, file_path, scale_ratio, created_at)
                VALUES(@NodeId, @FilePath, @ScaleRatio, NOW())
                """,
                new { NodeId = nodeId, FilePath = filePath, ScaleRatio = scaleRatio });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
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
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertCameraAsync(long floorId, long deviceId, int channelNo, decimal posX, decimal posY)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO map_camera(floor_id, device_id, channel_no, pos_x, pos_y, created_at)
                VALUES(@FloorId, @DeviceId, @ChannelNo, @PosX, @PosY, NOW())
                """,
                new { FloorId = floorId, DeviceId = deviceId, ChannelNo = channelNo, PosX = posX, PosY = posY });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<DbRoi>> GetRoisAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbRoi>(
                """
                SELECT roi_id AS RoiId, camera_id AS CameraId, room_node_id AS RoomNodeId,
                       COALESCE(CAST(vertices_json AS CHAR), '[]') AS VerticesJson, created_at AS CreatedAt
                FROM map_roi
                ORDER BY roi_id DESC
                """);
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertRoiAsync(long cameraId, long roomNodeId, string verticesJson)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO map_roi(camera_id, room_node_id, vertices_json, created_at)
                VALUES(@CameraId, @RoomNodeId, CAST(@VerticesJson AS JSON), NOW())
                """,
                new { CameraId = cameraId, RoomNodeId = roomNodeId, VerticesJson = verticesJson });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<long?> InsertTrackEventAsync(string vid, long cameraId, long roiId, DateTimeOffset eventTime)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO track_event(vid, camera_id, roi_id, event_time, created_at)
                VALUES(@Vid, @CameraId, @RoiId, @EventTime, NOW())
                """,
                new { Vid = vid, CameraId = cameraId, RoiId = roiId, EventTime = eventTime });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<DbTrackEvent>> GetTrackEventsAsync(string? vid)
    {
        try
        {
            await using var conn = CreateConnection();
            var where = string.IsNullOrWhiteSpace(vid) ? "" : " WHERE vid = @Vid ";
            var rows = await conn.QueryAsync<DbTrackEvent>(
                $"""
                SELECT event_id AS EventId, vid AS Vid, camera_id AS CameraId,
                       roi_id AS RoiId, event_time AS EventTime
                FROM track_event
                {where}
                ORDER BY event_id DESC
                LIMIT 1000
                """,
                new { Vid = vid });
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertJudgeResultAsync(string vid, long roomId, string judgeType, DateOnly judgeDate, string detailJson)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO judge_result(vid, room_id, judge_type, judge_date, detail_json, created_at)
                VALUES(@Vid, @RoomId, @JudgeType, @JudgeDate, CAST(@DetailJson AS JSON), NOW())
                """,
                new { Vid = vid, RoomId = roomId, JudgeType = judgeType, JudgeDate = judgeDate.ToDateTime(TimeOnly.MinValue), DetailJson = detailJson });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DeleteJudgeResultsByDateAsync(DateOnly judgeDate, string? judgeType)
    {
        try
        {
            await using var conn = CreateConnection();
            var whereType = string.IsNullOrWhiteSpace(judgeType) ? "" : " AND judge_type=@JudgeType ";
            var affected = await conn.ExecuteAsync(
                $"DELETE FROM judge_result WHERE judge_date=@JudgeDate {whereType}",
                new { JudgeDate = judgeDate.ToDateTime(TimeOnly.MinValue), JudgeType = judgeType });
            return affected >= 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<DbJudgeResult>> GetJudgeResultsAsync(DateOnly? judgeDate, string? judgeType)
    {
        try
        {
            await using var conn = CreateConnection();
            var where = " WHERE 1=1 ";
            if (judgeDate.HasValue) where += " AND judge_date=@JudgeDate ";
            if (!string.IsNullOrWhiteSpace(judgeType)) where += " AND judge_type=@JudgeType ";
            var sql = """
                SELECT judge_id AS JudgeId, vid AS Vid, room_id AS RoomId, judge_type AS JudgeType,
                       judge_date AS JudgeDate, COALESCE(CAST(detail_json AS CHAR), '{}') AS DetailJson, created_at AS CreatedAt
                FROM judge_result
                """
                + where +
                """
                ORDER BY judge_id DESC
                LIMIT 2000
                """;
            var rows = await conn.QueryAsync<DbJudgeResult>(
                sql,
                new
                {
                    JudgeDate = judgeDate?.ToDateTime(TimeOnly.MinValue),
                    JudgeType = judgeType
                });
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<DbRole>> GetRolesAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbRole>(
                """
                SELECT role_id AS RoleId, role_name AS RoleName, COALESCE(CAST(permission_json AS CHAR), '[]') AS PermissionJson
                FROM sys_role
                ORDER BY role_id DESC
                """);
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertRoleAsync(string roleName, string permissionJson)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO sys_role(role_name, permission_json, created_at)
                VALUES(@RoleName, @PermissionJson, NOW())
                """,
                new { RoleName = roleName, PermissionJson = permissionJson });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<DbUserListItem>> GetUsersAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbUserListItem>(
                """
                SELECT u.user_id AS UserId, u.user_name AS UserName, u.status AS `Status`,
                       r.role_name AS RoleName, u.created_at AS CreatedAt
                FROM sys_user u
                LEFT JOIN sys_role r ON u.role_id = r.role_id
                ORDER BY u.user_id DESC
                """);
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<long?> InsertUserAsync(string userName, string passwordHash, long roleId)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO sys_user(user_name, password_hash, role_id, status, created_at)
                VALUES(@UserName, @PasswordHash, @RoleId, 1, NOW())
                """,
                new { UserName = userName, PasswordHash = passwordHash, RoleId = roleId });
            return await conn.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> UpdateUserStatusAsync(long userId, int status)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                "UPDATE sys_user SET status=@Status WHERE user_id=@UserId",
                new { Status = status, UserId = userId });
            return affected > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ClearVirtualPersonsAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync("DELETE FROM virtual_person");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task InsertVirtualPersonAsync(string vid, DateTimeOffset firstSeen, DateTimeOffset lastSeen, long deviceId, int captureCount)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO virtual_person(v_id, first_seen, last_seen, device_id, capture_count, created_at)
                VALUES(@Vid, @FirstSeen, @LastSeen, @DeviceId, @CaptureCount, NOW())
                """,
                new { Vid = vid, FirstSeen = firstSeen, LastSeen = lastSeen, DeviceId = deviceId, CaptureCount = captureCount });
        }
        catch
        {
            // 忽略，保持上层可继续执行
        }
    }

    public async Task<List<DbVirtualPerson>> GetVirtualPersonsAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbVirtualPerson>(
                """
                SELECT v_id AS Vid, first_seen AS FirstSeen, last_seen AS LastSeen,
                       device_id AS DeviceId, capture_count AS CaptureCount
                FROM virtual_person
                ORDER BY created_at DESC
                """);
            return rows.ToList();
        }
        catch
        {
            return [];
        }
    }
}

internal sealed record DbUser(string UserName, string PasswordHash, string? RoleName);
internal sealed record DbDevice(long DeviceId, string Name, string Ip, int Port, string Brand, string Protocol, string Status, DateTime CreatedAt);
internal sealed record DbCapture(long CaptureId, long DeviceId, int ChannelNo, DateTimeOffset CaptureTime, string MetadataJson);
internal sealed record DbAlert(long AlertId, string AlertType, string Detail, DateTimeOffset CreatedAt);
internal sealed record DbOperation(long OperationId, string OperatorName, string Action, string Detail, DateTimeOffset CreatedAt);
internal sealed record DbRole(long RoleId, string RoleName, string PermissionJson);
internal sealed record DbUserListItem(long UserId, string UserName, int Status, string? RoleName, DateTimeOffset CreatedAt);
internal sealed record DbCampusNode(long NodeId, long? ParentId, string LevelType, string NodeName);
internal sealed record DbFloor(long FloorId, long NodeId, string FilePath, decimal ScaleRatio);
internal sealed record DbCamera(long CameraId, long FloorId, long DeviceId, int ChannelNo, decimal PosX, decimal PosY);
internal sealed record DbRoi(long RoiId, long CameraId, long RoomNodeId, string VerticesJson, DateTimeOffset CreatedAt);
internal sealed record DbTrackEvent(long EventId, string Vid, long CameraId, long RoiId, DateTimeOffset EventTime);
internal sealed record DbJudgeResult(long JudgeId, string Vid, long RoomId, string JudgeType, DateTime JudgeDate, string DetailJson, DateTimeOffset CreatedAt);
internal sealed record DbVirtualPerson(string Vid, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long DeviceId, int CaptureCount);
