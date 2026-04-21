/* 文件：抓拍与轨迹仓储 | File: Capture and track repository */
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal sealed class CaptureRepository
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<CaptureRepository>? _logger;

    public CaptureRepository(PgSqlConnectionFactory connectionFactory, ILogger<CaptureRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection() => _connectionFactory.CreateConnection();

    public async Task<long?> InsertCaptureAsync(long deviceId, int channelNo, DateTimeOffset captureTime, string metadataJson, string? imagePath)
    {
        try
        {
            var captureTimeUtc = captureTime.ToUniversalTime();
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO capture_record(device_id, channel_no, capture_time, image_path, metadata_json, created_at)
                VALUES(@DeviceId, @ChannelNo, @CaptureTime, @ImagePath, CAST(@MetadataJson AS jsonb), NOW())
                RETURNING capture_id
                """,
                new { DeviceId = deviceId, ChannelNo = channelNo, CaptureTime = captureTimeUtc, ImagePath = imagePath, MetadataJson = metadataJson });
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

    public async Task<long?> GetCaptureCountAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM capture_record");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库统计抓拍总数失败。");
            return null;
        }
    }

    public async Task<List<DbCapture>> GetCapturesInRangeAsync(DateTimeOffset start, DateTimeOffset end, int maxRows = 200000)
    {
        try
        {
            var startUtc = start.ToUniversalTime();
            var endUtc = end.ToUniversalTime();
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbCapture>(
                """
                SELECT capture_id AS CaptureId, device_id AS DeviceId, channel_no AS ChannelNo,
                       capture_time AS CaptureTime, COALESCE(CAST(metadata_json AS TEXT), '') AS MetadataJson,
                       image_path AS ImagePath
                FROM capture_record
                WHERE capture_time >= @Start AND capture_time < @End
                ORDER BY capture_time DESC, capture_id DESC
                LIMIT @MaxRows
                """,
                new { Start = startUtc, End = endUtc, MaxRows = maxRows });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库按时间范围查询抓拍失败。start={Start}, end={End}, maxRows={MaxRows}", start, end, maxRows);
            return [];
        }
    }

    public async Task<(List<DbCapture> Rows, int Total)> GetCapturesPagedAsync(DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 20;

        try
        {
            var fromUtc = from?.ToUniversalTime();
            var toUtc = to?.ToUniversalTime();
            await using var conn = CreateConnection();
            var where = " WHERE 1=1 ";
            if (from.HasValue) where += " AND capture_time >= @From ";
            if (to.HasValue) where += " AND capture_time <= @To ";

            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(1) FROM capture_record {where}", new { From = fromUtc, To = toUtc });

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
                new { From = fromUtc, To = toUtc, Offset = offset, PageSize = pageSize });

            return (rows.ToList(), total);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库分页查询抓拍失败。from={From}, to={To}, page={Page}, pageSize={PageSize}", from, to, page, pageSize);
            return ([], 0);
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
            var eventTimeUtc = eventTime.ToUniversalTime();
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO track_event(vid, camera_id, roi_id, event_time, created_at)
                VALUES(@Vid, @CameraId, @RoiId, @EventTime, NOW())
                RETURNING event_id
                """,
                new { Vid = vid, CameraId = cameraId, RoiId = roiId, EventTime = eventTimeUtc });
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

    public async Task<List<DbTrackEvent>> GetTrackEventsInRangeAsync(DateTimeOffset start, DateTimeOffset end, int maxRows = 200000)
    {
        try
        {
            var startUtc = start.ToUniversalTime();
            var endUtc = end.ToUniversalTime();
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
                new { Start = startUtc, End = endUtc, MaxRows = maxRows });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库按时间范围查询轨迹失败。start={Start}, end={End}, maxRows={MaxRows}", start, end, maxRows);
            return [];
        }
    }

    public async Task<Dictionary<string, string>> GetBestCaptureImageByVidsAsync(IReadOnlyCollection<string> vids)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (vids is null || vids.Count == 0)
        {
            return result;
        }

        var normalized = vids
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            return result;
        }

        try
        {
            await using var conn = CreateConnection();
            var byTrack = await conn.QueryAsync<DbVidImage>(
                """
                SELECT t.vid AS Vid, c.image_path AS ImagePath
                FROM (
                  SELECT DISTINCT ON (te.vid) te.vid, te.camera_id, te.event_time, te.event_id
                  FROM track_event te
                  WHERE te.vid = ANY(@Vids)
                  ORDER BY te.vid, te.event_time DESC, te.event_id DESC
                ) t
                JOIN map_camera mc ON mc.camera_id = t.camera_id
                LEFT JOIN LATERAL (
                  SELECT cr.image_path
                  FROM capture_record cr
                  WHERE cr.device_id = mc.device_id
                    AND cr.image_path IS NOT NULL
                    AND btrim(cr.image_path) <> ''
                  ORDER BY ABS(EXTRACT(EPOCH FROM (cr.capture_time - t.event_time))) ASC,
                           cr.capture_time DESC,
                           cr.capture_id DESC
                  LIMIT 1
                ) c ON TRUE
                WHERE c.image_path IS NOT NULL AND btrim(c.image_path) <> ''
                """,
                new { Vids = normalized });
            foreach (var row in byTrack)
            {
                if (string.IsNullOrWhiteSpace(row.Vid) || string.IsNullOrWhiteSpace(row.ImagePath)) continue;
                if (!result.ContainsKey(row.Vid))
                {
                    result[row.Vid] = row.ImagePath!;
                }
            }

            var missing = normalized.Where(v => !result.ContainsKey(v)).ToArray();
            if (missing.Length == 0)
            {
                return result;
            }

            var byFeature = await conn.QueryAsync<DbVidImage>(
                """
                SELECT DISTINCT ON (cr.feature_id) cr.feature_id AS Vid, cr.image_path AS ImagePath
                FROM capture_record cr
                WHERE cr.feature_id = ANY(@Vids)
                  AND cr.image_path IS NOT NULL
                  AND btrim(cr.image_path) <> ''
                ORDER BY cr.feature_id, cr.capture_time DESC, cr.capture_id DESC
                """,
                new { Vids = missing });
            foreach (var row in byFeature)
            {
                if (string.IsNullOrWhiteSpace(row.Vid) || string.IsNullOrWhiteSpace(row.ImagePath)) continue;
                if (!result.ContainsKey(row.Vid))
                {
                    result[row.Vid] = row.ImagePath!;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "数据库按 VID 查询命中图片失败。vidCount={VidCount}", normalized.Length);
        }

        return result;
    }
}
