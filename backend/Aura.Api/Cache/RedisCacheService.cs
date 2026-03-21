using StackExchange.Redis;

namespace Aura.Api.Cache;

internal sealed class RedisCacheService
{
    private readonly IDatabase? _db;

    public RedisCacheService(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            var mux = ConnectionMultiplexer.Connect(connectionString);
            _db = mux.GetDatabase();
        }
        catch
        {
            _db = null;
        }
    }

    public async Task<string?> GetAsync(string key)
    {
        if (_db is null)
        {
            return null;
        }
        var value = await _db.StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl)
    {
        if (_db is null)
        {
            return;
        }
        await _db.StringSetAsync(key, value, ttl);
    }
}
