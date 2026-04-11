using Aura.Api.Data;

internal sealed class OperationQueryService
{
    internal sealed record OperationQueryResult(object Data, object Pager);

    private readonly AppStore _store;
    private readonly PgSqlStore _db;

    public OperationQueryService(AppStore store, PgSqlStore db)
    {
        _store = store;
        _db = db;
    }

    public async Task<OperationQueryResult> GetOperationsAsync(string? keyword, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var dbResult = await _db.GetOperationsAsync(keyword, page, pageSize);
        if (dbResult.Total > 0 || dbResult.Rows.Count > 0)
        {
            return new OperationQueryResult(dbResult.Rows, new { page, pageSize, total = dbResult.Total });
        }

        var query = _store.Operations.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                x.Action.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.OperatorName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var total = query.Count();
        var rows = query.OrderByDescending(x => x.OperationId).Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return new OperationQueryResult(rows, new { page, pageSize, total });
    }
}
