using Aura.Api.Data;
using Aura.Api.Models;

internal sealed class SystemLogQueryService
{
    internal sealed record SystemLogQueryResult(object Data, object Pager);

    private readonly AppStore _store;
    private readonly PgSqlConnectionFactory _pgSqlConnectionFactory;
    private readonly AuditRepository _auditRepository;

    public SystemLogQueryService(AppStore store, PgSqlConnectionFactory pgSqlConnectionFactory, AuditRepository auditRepository)
    {
        _store = store;
        _pgSqlConnectionFactory = pgSqlConnectionFactory;
        _auditRepository = auditRepository;
    }

    public async Task<SystemLogQueryResult> GetSystemLogsAsync(string? keyword, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var dbResult = await _auditRepository.GetSystemLogsAsync(keyword, page, pageSize);
        if (_pgSqlConnectionFactory.IsConfigured)
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
