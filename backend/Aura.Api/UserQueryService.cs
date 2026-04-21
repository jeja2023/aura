using Aura.Api.Data;

internal sealed class UserQueryService
{
    internal sealed record UserQueryResult(object Data, object Pager);

    private readonly AppStore _store;
    private readonly PgSqlConnectionFactory _pgSqlConnectionFactory;
    private readonly UserAuthRepository _userAuthRepository;

    public UserQueryService(AppStore store, PgSqlConnectionFactory pgSqlConnectionFactory, UserAuthRepository userAuthRepository)
    {
        _store = store;
        _pgSqlConnectionFactory = pgSqlConnectionFactory;
        _userAuthRepository = userAuthRepository;
    }

    public async Task<UserQueryResult> GetUsersAsync(string? keyword, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var dbResult = await _userAuthRepository.GetUsersAsync(keyword, page, pageSize);
        if (_pgSqlConnectionFactory.IsConfigured)
        {
            var actualPage = ResolvePage(page, pageSize, dbResult.Total);
            return new UserQueryResult(dbResult.Rows, new { page = actualPage, pageSize, total = dbResult.Total });
        }

        var query = _store.Users.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var trimmed = keyword.Trim();
            query = query.Where(x =>
                x.UserName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || x.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        var total = query.Count();
        var actual = ResolvePage(page, pageSize, total);
        var rows = query
            .OrderByDescending(x => x.UserId)
            .Skip((actual - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new DbUserListItem(
                u.UserId,
                u.UserName,
                u.Status,
                u.DisplayName,
                u.RoleName,
                u.RoleId,
                u.CreatedAt.DateTime,
                u.LastLoginAt?.DateTime,
                u.MustChangePassword))
            .ToArray();
        return new UserQueryResult(rows, new { page = actual, pageSize, total });
    }

    private static int ResolvePage(int page, int pageSize, int total)
    {
        if (page <= 1 || total <= 0)
        {
            return 1;
        }

        var maxPage = (int)Math.Ceiling(total / (double)pageSize);
        return Math.Min(page, Math.Max(1, maxPage));
    }
}
