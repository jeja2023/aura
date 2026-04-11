using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.Ops;
using Aura.Api.Models;

internal sealed class JudgeService
{
    private readonly AppStore _store;
    private readonly PgSqlStore _db;
    private readonly EventDispatchService _eventDispatchService;

    public JudgeService(AppStore store, PgSqlStore db, EventDispatchService eventDispatchService)
    {
        _store = store;
        _db = db;
        _eventDispatchService = eventDispatchService;
    }

    public async Task<JudgeRunResult> RunHomeAsync(DateOnly judgeDate)
    {
        await _db.DeleteJudgeResultsByDateAsync(judgeDate, "home_room");
        var start = judgeDate.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        var events = await _db.GetTrackEventsInRangeAsync(start, end);
        if (events.Count == 0)
        {
            return new JudgeRunResult(judgeDate, "home_room", 0, 0);
        }

        var roiMap = (await _db.GetRoisAsync()).ToDictionary(x => x.RoiId, x => x.RoomNodeId);
        var saveCount = 0;
        foreach (var group in events.GroupBy(x => x.Vid))
        {
            var roomAgg = group
                .Where(x => roiMap.ContainsKey(x.RoiId))
                .GroupBy(x => roiMap[x.RoiId])
                .Select(x => new { RoomId = x.Key, Count = x.Count(), Last = x.Max(e => e.EventTime) })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.Last)
                .FirstOrDefault();
            if (roomAgg is null)
            {
                continue;
            }

            var detail = JsonSerializer.Serialize(new { roomAgg.Count, roomAgg.Last });
            var id = await _db.InsertJudgeResultAsync(group.Key, roomAgg.RoomId, "home_room", judgeDate, detail);
            if (id.HasValue)
            {
                saveCount++;
            }
            else
            {
                _store.JudgeResults.Add(new JudgeResultEntity(Interlocked.Increment(ref _store.JudgeSeed), group.Key, roomAgg.RoomId, "home_room", judgeDate, detail, DateTimeOffset.Now));
                saveCount++;
            }
        }

        await _db.InsertOperationAsync("系统任务", "归寝研判", $"日期={judgeDate:yyyy-MM-dd}, 结果={saveCount}");
        return new JudgeRunResult(judgeDate, "home_room", events.Count, saveCount);
    }

    public async Task<JudgeRunResult> RunGroupRentAndStayAsync(DateOnly judgeDate, int groupThreshold, int stayMinutes)
    {
        await _db.DeleteJudgeResultsByDateAsync(judgeDate, "group_rent");
        await _db.DeleteJudgeResultsByDateAsync(judgeDate, "abnormal_stay");
        var start = judgeDate.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        var events = await _db.GetTrackEventsInRangeAsync(start, end);
        if (events.Count == 0)
        {
            return new JudgeRunResult(judgeDate, "group_rent+abnormal_stay", 0, 0);
        }

        var roiMap = (await _db.GetRoisAsync()).ToDictionary(x => x.RoiId, x => x.RoomNodeId);
        var eventWithRoom = events
            .Where(x => roiMap.ContainsKey(x.RoiId))
            .Select(x => new { x.Vid, RoomId = roiMap[x.RoiId], x.EventTime })
            .ToList();

        var saveCount = 0;
        foreach (var room in eventWithRoom.GroupBy(x => x.RoomId))
        {
            var distinctVid = room.Select(x => x.Vid).Distinct().ToArray();
            if (distinctVid.Length < groupThreshold)
            {
                continue;
            }

            var detail = JsonSerializer.Serialize(new { distinctVidCount = distinctVid.Length, vids = distinctVid });
            var id = await _db.InsertJudgeResultAsync($"ROOM_{room.Key}", room.Key, "group_rent", judgeDate, detail);
            if (id.HasValue)
            {
                saveCount++;
            }

            var aid = await _db.InsertAlertAsync("群租预警", $"房间={room.Key}, 人数={distinctVid.Length}");
            if (!aid.HasValue)
            {
                _store.Alerts.Add(new AlertEntity(Interlocked.Increment(ref _store.AlertSeed), "群租预警", $"房间={room.Key}, 人数={distinctVid.Length}", DateTimeOffset.Now));
            }
            await _eventDispatchService.NotifyAlertAsync("群租预警", $"房间={room.Key}, 人数={distinctVid.Length}", "群租研判");
            await _eventDispatchService.BroadcastRoleEventAsync("alert.created", new { alertType = "群租预警", roomId = room.Key, count = distinctVid.Length, date = judgeDate });
        }

        foreach (var personRoom in eventWithRoom.GroupBy(x => new { x.Vid, x.RoomId }))
        {
            var first = personRoom.Min(x => x.EventTime);
            var last = personRoom.Max(x => x.EventTime);
            var minutes = (last - first).TotalMinutes;
            if (minutes < stayMinutes)
            {
                continue;
            }

            var detail = JsonSerializer.Serialize(new { stayMinutes = minutes, first, last });
            var id = await _db.InsertJudgeResultAsync(personRoom.Key.Vid, personRoom.Key.RoomId, "abnormal_stay", judgeDate, detail);
            if (id.HasValue)
            {
                saveCount++;
            }

            var aid = await _db.InsertAlertAsync("异常滞留", $"VID={personRoom.Key.Vid}, 房间={personRoom.Key.RoomId}, 分钟={Math.Round(minutes, 1)}");
            if (!aid.HasValue)
            {
                _store.Alerts.Add(new AlertEntity(Interlocked.Increment(ref _store.AlertSeed), "异常滞留", $"VID={personRoom.Key.Vid}, 房间={personRoom.Key.RoomId}, 分钟={Math.Round(minutes, 1)}", DateTimeOffset.Now));
            }
            await _eventDispatchService.NotifyAlertAsync("异常滞留", $"VID={personRoom.Key.Vid}, 房间={personRoom.Key.RoomId}, 分钟={Math.Round(minutes, 1)}", "滞留研判");
        }

        await _db.InsertOperationAsync("系统任务", "群租/滞留研判", $"日期={judgeDate:yyyy-MM-dd}, 结果={saveCount}");
        return new JudgeRunResult(judgeDate, "group_rent+abnormal_stay", events.Count, saveCount);
    }

    public async Task<JudgeRunResult> RunNightAbsenceAsync(DateOnly judgeDate, int cutoffHour)
    {
        await _db.DeleteJudgeResultsByDateAsync(judgeDate, "night_absence");
        var homeRows = await _db.GetJudgeResultsAsync(judgeDate, "home_room");
        var home = homeRows.Count > 0
            ? homeRows.Select(x => new JudgeResultEntity(x.JudgeId, x.Vid, x.RoomId, x.JudgeType, DateOnly.FromDateTime(x.JudgeDate), x.DetailJson, x.CreatedAt)).ToList()
            : _store.JudgeResults.Where(x => x.JudgeDate == judgeDate && x.JudgeType == "home_room").ToList();
        if (home.Count == 0)
        {
            return new JudgeRunResult(judgeDate, "night_absence", 0, 0);
        }

        var start = judgeDate.ToDateTime(new TimeOnly(cutoffHour, 0));
        var end = start.AddHours(8);
        var roiMap = (await _db.GetRoisAsync()).ToDictionary(x => x.RoiId, x => x.RoomNodeId);
        var events = (await _db.GetTrackEventsInRangeAsync(start, end))
            .Where(x => roiMap.ContainsKey(x.RoiId))
            .ToList();
        var existedPairs = events
            .Select(x => (Vid: x.Vid, RoomId: roiMap[x.RoiId]))
            .ToHashSet();

        var saveCount = 0;
        foreach (var row in home)
        {
            if (existedPairs.Contains((row.Vid, row.RoomId)))
            {
                continue;
            }

            var detail = JsonSerializer.Serialize(new { cutoffHour, message = "截止时间后未回到归属房间" });
            var id = await _db.InsertJudgeResultAsync(row.Vid, row.RoomId, "night_absence", judgeDate, detail);
            if (id.HasValue)
            {
                saveCount++;
            }

            var aid = await _db.InsertAlertAsync("夜不归宿", $"VID={row.Vid}, 房间={row.RoomId}, 日期={judgeDate:yyyy-MM-dd}");
            if (!aid.HasValue)
            {
                _store.Alerts.Add(new AlertEntity(Interlocked.Increment(ref _store.AlertSeed), "夜不归宿", $"VID={row.Vid}, 房间={row.RoomId}, 日期={judgeDate:yyyy-MM-dd}", DateTimeOffset.Now));
            }
            await _eventDispatchService.NotifyAlertAsync("夜不归宿", $"VID={row.Vid}, 房间={row.RoomId}, 日期={judgeDate:yyyy-MM-dd}", "夜不归宿研判");
            await _eventDispatchService.BroadcastRoleEventAsync("alert.created", new { alertType = "夜不归宿", vid = row.Vid, roomId = row.RoomId, date = judgeDate });
        }

        await _db.InsertOperationAsync("系统任务", "夜不归宿研判", $"日期={judgeDate:yyyy-MM-dd}, 结果={saveCount}");
        return new JudgeRunResult(judgeDate, "night_absence", home.Count, saveCount);
    }
}
