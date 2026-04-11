/* 文件：PostgreSQL存储服务（PgSqlStore） | File: PostgreSQL Store Service */
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal sealed class PgSqlStore
{
    private readonly string _connectionString;
    private readonly ILogger<PgSqlStore>? _logger;

    public PgSqlStore(string connectionString, ILogger<PgSqlStore>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection() => new(_connectionString);

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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询用户失败。userName={UserName}", userName);
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

    public async Task<long?> InsertCaptureAsync(long deviceId, int channelNo, DateTimeOffset captureTime, string metadataJson, string? imagePath)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO capture_record(device_id, channel_no, capture_time, image_path, metadata_json, created_at)
                VALUES(@DeviceId, @ChannelNo, @CaptureTime, @ImagePath, CAST(@MetadataJson AS jsonb), NOW())
                RETURNING capture_id
                """,
                new { DeviceId = deviceId, ChannelNo = channelNo, CaptureTime = captureTime, ImagePath = imagePath, MetadataJson = metadataJson });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入抓拍记录失败。deviceId={DeviceId}, channelNo={ChannelNo}", deviceId, channelNo);
            return null;
        }
    }

    public async Task<bool> UpdateCaptureMetadataAsync(long captureId, string metadataJson)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE capture_record
                SET metadata_json = CAST(@MetadataJson AS jsonb)
                WHERE capture_id=@CaptureId
                """,
                new { CaptureId = captureId, MetadataJson = metadataJson });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库更新抓拍元数据失败。captureId={CaptureId}", captureId);
            return false;
        }
    }

    public async Task<List<DbCapture>> GetCapturesAsync(int limit = 500)
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbCapture>(
                """
                SELECT capture_id AS CaptureId, device_id AS DeviceId, channel_no AS ChannelNo,
                       capture_time AS CaptureTime, COALESCE(CAST(metadata_json AS TEXT), '') AS MetadataJson,
                       image_path AS ImagePath
                FROM capture_record
                ORDER BY capture_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询抓拍列表失败。limit={Limit}", limit);
            return [];
        }
    }

    public async Task<long?> InsertAlertAsync(string alertType, string detail)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO alert_record(alert_type, detail_json, created_at)
                VALUES(@AlertType, to_jsonb(@Detail::text), NOW())
                RETURNING alert_id
                """,
                new { AlertType = alertType, Detail = detail });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入告警失败。alertType={AlertType}", alertType);
            return null;
        }
    }

    public async Task<List<DbAlert>> GetAlertsAsync(int limit = 500)
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbAlert>(
                """
                SELECT alert_id AS AlertId, alert_type AS AlertType,
                       COALESCE(
                         CASE
                           WHEN jsonb_typeof(detail_json) = 'string' THEN trim(both '"' from detail_json::text)
                           ELSE CAST(detail_json AS TEXT)
                         END,
                         ''
                       ) AS Detail,
                       created_at AS CreatedAt
                FROM alert_record
                ORDER BY alert_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询告警列表失败。limit={Limit}", limit);
            return [];
        }
    }

    public async Task<long?> InsertOperationAsync(string operatorName, string action, string detail)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO log_operation(operator_name, action_type, action_detail, created_at)
                VALUES(@OperatorName, @Action, @Detail, NOW())
                RETURNING op_id
                """,
                new { OperatorName = operatorName, Action = action, Detail = detail });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入操作日志失败。operator={OperatorName}, action={Action}", operatorName, action);
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
                LIMIT @pageSize OFFSET @offset
                """,
                new { kw = $"%{keyword}%", offset = (page - 1) * pageSize, pageSize });
            return (rows.ToList(), total);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询操作日志失败。keyword={Keyword}, page={Page}, pageSize={PageSize}", keyword, page, pageSize);
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

    public async Task<List<DbRoi>> GetRoisAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbRoi>(
                """
                SELECT roi_id AS RoiId, camera_id AS CameraId, room_node_id AS RoomNodeId,
                       COALESCE(CAST(vertices_json AS TEXT), '[]') AS VerticesJson, created_at AS CreatedAt
                FROM map_roi
                ORDER BY roi_id DESC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询 ROI 列表失败。");
            return [];
        }
    }

    public async Task<long?> InsertRoiAsync(long cameraId, long roomNodeId, string verticesJson)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO map_roi(camera_id, room_node_id, vertices_json, created_at)
                VALUES(@CameraId, @RoomNodeId, CAST(@VerticesJson AS jsonb), NOW())
                RETURNING roi_id
                """,
                new { CameraId = cameraId, RoomNodeId = roomNodeId, VerticesJson = verticesJson });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入 ROI 失败。cameraId={CameraId}, roomNodeId={RoomNodeId}", cameraId, roomNodeId);
            return null;
        }
    }

    public async Task<long?> InsertTrackEventAsync(string vid, long cameraId, long roiId, DateTimeOffset eventTime)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO track_event(vid, camera_id, roi_id, event_time, created_at)
                VALUES(@Vid, @CameraId, @RoiId, @EventTime, NOW())
                RETURNING event_id
                """,
                new { Vid = vid, CameraId = cameraId, RoiId = roiId, EventTime = eventTime });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入轨迹事件失败。vid={Vid}, cameraId={CameraId}, roiId={RoiId}", vid, cameraId, roiId);
            return null;
        }
    }

    public async Task<List<DbTrackEvent>> GetTrackEventsAsync(string? vid, int limit = 500, int maxLimit = 2000)
    {
        if (limit <= 0) limit = 500;
        if (limit > maxLimit) limit = maxLimit;

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
                LIMIT @Limit
                """,
                new { Vid = vid, Limit = limit });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询轨迹失败。vid={Vid}, limit={Limit}", vid, limit);
            return [];
        }
    }

    public async Task<long?> InsertJudgeResultAsync(string vid, long roomId, string judgeType, DateOnly judgeDate, string detailJson)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO judge_result(vid, room_id, judge_type, judge_date, detail_json, created_at)
                VALUES(@Vid, @RoomId, @JudgeType, @JudgeDate, CAST(@DetailJson AS jsonb), NOW())
                RETURNING judge_id
                """,
                new { Vid = vid, RoomId = roomId, JudgeType = judgeType, JudgeDate = judgeDate.ToDateTime(TimeOnly.MinValue), DetailJson = detailJson });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入研判结果失败。vid={Vid}, roomId={RoomId}, judgeType={JudgeType}", vid, roomId, judgeType);
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库删除研判结果失败。judgeDate={JudgeDate}, judgeType={JudgeType}", judgeDate, judgeType);
            return false;
        }
    }

    public async Task<List<DbJudgeResult>> GetJudgeResultsAsync(DateOnly? judgeDate, string? judgeType, int maxRows = 2000)
    {
        try
        {
            await using var conn = CreateConnection();
            var where = " WHERE 1=1 ";
            if (judgeDate.HasValue) where += " AND judge_date=@JudgeDate ";
            if (!string.IsNullOrWhiteSpace(judgeType)) where += " AND judge_type=@JudgeType ";
            var sql = """
                SELECT judge_id AS JudgeId, vid AS Vid, room_id AS RoomId, judge_type AS JudgeType,
                       judge_date AS JudgeDate, COALESCE(CAST(detail_json AS TEXT), '') AS DetailJson, created_at AS CreatedAt
                FROM judge_result
                """
                + where +
                """
                ORDER BY judge_id DESC
                LIMIT @MaxRows
                """;
            var rows = await conn.QueryAsync<DbJudgeResult>(
                sql,
                new
                {
                    JudgeDate = judgeDate?.ToDateTime(TimeOnly.MinValue),
                    JudgeType = judgeType,
                    MaxRows = maxRows
                });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询研判结果失败。judgeDate={JudgeDate}, judgeType={JudgeType}, maxRows={MaxRows}", judgeDate, judgeType, maxRows);
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
                SELECT role_id AS RoleId, role_name AS RoleName, COALESCE(CAST(permission_json AS TEXT), '[]') AS PermissionJson
                FROM sys_role
                ORDER BY role_id DESC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询角色列表失败。");
            return [];
        }
    }

    public async Task<long?> InsertRoleAsync(string roleName, string permissionJson)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO sys_role(role_name, permission_json, created_at)
                VALUES(@RoleName, CAST(@PermissionJson AS jsonb), NOW())
                RETURNING role_id
                """,
                new { RoleName = roleName, PermissionJson = permissionJson });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入角色失败。roleName={RoleName}", roleName);
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
                SELECT u.user_id AS UserId, u.user_name AS UserName, CAST(u.status AS BIGINT) AS Status,
                       r.role_name AS RoleName, u.created_at AS CreatedAt
                FROM sys_user u
                LEFT JOIN sys_role r ON u.role_id = r.role_id
                ORDER BY u.user_id DESC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询用户列表失败。");
            return [];
        }
    }

    public async Task<long?> InsertUserAsync(string userName, string passwordHash, long roleId)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO sys_user(user_name, password_hash, role_id, status, created_at)
                VALUES(@UserName, @PasswordHash, @RoleId, 1, NOW())
                RETURNING user_id
                """,
                new { UserName = userName, PasswordHash = passwordHash, RoleId = roleId });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入用户失败。userName={UserName}, roleId={RoleId}", userName, roleId);
            return null;
        }
    }

    public async Task<bool> UpdateUserPasswordByUserNameAsync(string userName, string passwordHash)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE sys_user
                SET password_hash=@PasswordHash
                WHERE user_name=@UserName
                """,
                new { UserName = userName, PasswordHash = passwordHash });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库重置用户密码失败。userName={UserName}", userName);
            return false;
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库更新用户状态失败。userId={UserId}, status={Status}", userId, status);
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库清空虚拟人员失败。");
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
        catch (Exception ex)
        {
            // 忽略，保持上层可继续执行
            _logger?.LogWarning(ex, "数据库写入虚拟人员失败。vid={Vid}, deviceId={DeviceId}", vid, deviceId);
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询虚拟人员列表失败。");
            return [];
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

    public async Task<List<DbTrackEvent>> GetTrackEventsInRangeAsync(DateTimeOffset start, DateTimeOffset end, int maxRows = 200000)
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbTrackEvent>(
                """
                SELECT event_id AS EventId, vid AS Vid, camera_id AS CameraId,
                       roi_id AS RoiId, event_time AS EventTime
                FROM track_event
                WHERE event_time >= @Start AND event_time < @End
                ORDER BY event_id DESC
                LIMIT @MaxRows
                """,
                new { Start = start, End = end, MaxRows = maxRows });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库按时间范围查询轨迹失败。start={Start}, end={End}, maxRows={MaxRows}", start, end, maxRows);
            return [];
        }
    }

    public async Task<(List<DbCapture> Rows, int Total)> GetCapturesPagedAsync(DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 20;

        try
        {
            await using var conn = CreateConnection();
            var where = " WHERE 1=1 ";
            if (from.HasValue) where += " AND capture_time >= @From ";
            if (to.HasValue) where += " AND capture_time <= @To ";

            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(1) FROM capture_record {where}", new { From = from, To = to });

            var offset = (page - 1) * pageSize;
            var rows = await conn.QueryAsync<DbCapture>(
                $"""
                SELECT capture_id AS CaptureId, device_id AS DeviceId, channel_no AS ChannelNo,
                       capture_time AS CaptureTime, COALESCE(CAST(metadata_json AS TEXT), '') AS MetadataJson,
                       image_path AS ImagePath
                FROM capture_record
                {where}
                ORDER BY capture_time DESC, capture_id DESC
                LIMIT @PageSize OFFSET @Offset
                """,
                new { From = from, To = to, Offset = offset, PageSize = pageSize });

            return (rows.ToList(), total);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库分页查询抓拍失败。from={From}, to={To}, page={Page}, pageSize={PageSize}", from, to, page, pageSize);
            return ([], 0);
        }
    }
}

internal sealed record DbUser(string UserName, string PasswordHash, string? RoleName);
internal sealed record DbDevice(long DeviceId, string Name, string Ip, int Port, string Brand, string Protocol, string Status, DateTime CreatedAt);
internal sealed record DbCapture(long CaptureId, long DeviceId, int ChannelNo, DateTime CaptureTime, string MetadataJson, string? ImagePath = null);
internal sealed record DbAlert(long AlertId, string AlertType, string Detail, DateTime CreatedAt);
internal sealed record DbOperation(long OperationId, string OperatorName, string Action, string Detail, DateTimeOffset CreatedAt);
internal sealed record DbRole(long RoleId, string RoleName, string PermissionJson);
internal sealed record DbUserListItem(long UserId, string UserName, long Status, string? RoleName, DateTime CreatedAt);
internal sealed record DbCampusNode(long NodeId, long? ParentId, string LevelType, string NodeName);
internal sealed record DbFloor(long FloorId, long NodeId, string FilePath, decimal ScaleRatio);
internal sealed record DbCamera(long CameraId, long FloorId, long DeviceId, int ChannelNo, decimal PosX, decimal PosY);
internal sealed record DbRoi(long RoiId, long CameraId, long RoomNodeId, string VerticesJson, DateTimeOffset CreatedAt);
internal sealed record DbTrackEvent(long EventId, string Vid, long CameraId, long RoiId, DateTimeOffset EventTime);
internal sealed record DbJudgeResult(long JudgeId, string Vid, long RoomId, string JudgeType, DateTime JudgeDate, string DetailJson, DateTimeOffset CreatedAt);
internal sealed record DbVirtualPerson(string Vid, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long DeviceId, int CaptureCount);
