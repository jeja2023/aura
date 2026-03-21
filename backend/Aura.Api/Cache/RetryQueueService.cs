using System.Text.Json;
using StackExchange.Redis;

namespace Aura.Api.Cache;

internal sealed class RetryQueueService
{
    private readonly IDatabase? _db;
    private const string QueueKey = "aura:retry:capture";

    public RetryQueueService(string? connectionString)
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

    public bool Enabled => _db is not null;

    public async Task EnqueueAsync(RetryTask task)
    {
        if (_db is null)
        {
            return;
        }
        var json = JsonSerializer.Serialize(task);
        await _db.ListRightPushAsync(QueueKey, json);
    }

    public async Task<RetryTask?> DequeueAsync()
    {
        if (_db is null)
        {
            return null;
        }
        var value = await _db.ListLeftPopAsync(QueueKey);
        if (!value.HasValue)
        {
            return null;
        }
        return JsonSerializer.Deserialize<RetryTask>(value.ToString());
    }

    public async Task<long> LengthAsync()
    {
        if (_db is null)
        {
            return 0;
        }
        return await _db.ListLengthAsync(QueueKey);
    }
}

internal sealed record RetryTask(
    long DeviceId,
    int ChannelNo,
    string ImageBase64,
    string MetadataJson,
    string Source,
    int RetryCount,
    DateTimeOffset CreatedAt);
