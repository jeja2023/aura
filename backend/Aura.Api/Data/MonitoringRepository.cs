/* 文件：监控与研判仓储 | File: Monitoring repository */
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal sealed class MonitoringRepository
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<MonitoringRepository>? _logger;

    public MonitoringRepository(PgSqlConnectionFactory connectionFactory, ILogger<MonitoringRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection() => _connectionFactory.CreateConnection();

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

    public async Task<long?> GetAlertCountAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM alert_record");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库统计告警总数失败。");
            return null;
        }
    }

    public async Task<List<DbAlert>> GetAlertsInRangeAsync(DateTimeOffset start, DateTimeOffset end, int maxRows = 200000)
    {
        try
        {
            var startUtc = start.ToUniversalTime();
            var endUtc = end.ToUniversalTime();
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
                WHERE created_at >= @Start AND created_at < @End
                ORDER BY created_at DESC, alert_id DESC
                LIMIT @MaxRows
                """,
                new { Start = startUtc, End = endUtc, MaxRows = maxRows });
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库按时间范围查询告警失败。start={Start}, end={End}, maxRows={MaxRows}", start, end, maxRows);
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
            var firstSeenUtc = firstSeen.ToUniversalTime();
            var lastSeenUtc = lastSeen.ToUniversalTime();
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO virtual_person(v_id, first_seen, last_seen, device_id, capture_count, created_at)
                VALUES(@Vid, @FirstSeen, @LastSeen, @DeviceId, @CaptureCount, NOW())
                """,
                new { Vid = vid, FirstSeen = firstSeenUtc, LastSeen = lastSeenUtc, DeviceId = deviceId, CaptureCount = captureCount });
        }
        catch (Exception ex)
        {
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
}
