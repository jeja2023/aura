using Aura.Api.Data;
using Aura.Api.Models;

internal sealed class SystemLogQueryService
{
    internal sealed record SystemLogQueryResult(object Data, object Pager);

    private readonly AppStore _store;
    private readonly PgSqlStore _db;

    public SystemLogQueryService(AppStore store, PgSqlStore db)
    {
        _store = store;
        _db = db;
    }

    public async Task<SystemLogQueryResult> GetSystemLogsAsync(string? keyword, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var dbResult = await _db.GetSystemLogsAsync(keyword, page, pageSize);
        if (dbResult.Total > 0 || dbResult.Rows.Count > 0)
        {
            return new SystemLogQueryResult(dbResult.Rows, new { page, pageSize, total = dbResult.Total });
        }

        var query = _store.SystemLogs.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                x.Level.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var total = query.Count();
        var rows = query.OrderByDescending(x => x.SystemLogId).Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return new SystemLogQueryResult(rows, new { page, pageSize, total });
    }
}
