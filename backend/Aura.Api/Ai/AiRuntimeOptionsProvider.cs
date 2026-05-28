using Aura.Api.Data;

namespace Aura.Api.Ai;

internal sealed class AiRuntimeOptionsProvider
{
    public const string BaseUrlsConfigKey = "ai.base_urls";

    private readonly SystemConfigRepository _configRepository;
    private readonly IReadOnlyList<string> _fallbackBaseUrls;
    private readonly ILogger<AiRuntimeOptionsProvider> _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private AiRuntimeOptions _current;

    public AiRuntimeOptionsProvider(
        SystemConfigRepository configRepository,
        IEnumerable<string?> fallbackBaseUrls,
        ILogger<AiRuntimeOptionsProvider> logger)
        : this(configRepository, fallbackBaseUrls, logger, TimeSpan.FromSeconds(2))
    {
    }

    internal AiRuntimeOptionsProvider(
        SystemConfigRepository configRepository,
        IEnumerable<string?> fallbackBaseUrls,
        ILogger<AiRuntimeOptionsProvider> logger,
        TimeSpan cacheTtl)
    {
        _configRepository = configRepository;
        _fallbackBaseUrls = AiClient.NormalizeBaseUrls(fallbackBaseUrls);
        _current = new AiRuntimeOptions(_fallbackBaseUrls, string.Empty, false, null, null);
        _logger = logger;
        _cacheTtl = cacheTtl;
    }

    public IReadOnlyList<string> FallbackBaseUrls => _fallbackBaseUrls;

    public async Task<AiRuntimeOptions> GetAsync(bool forceRefresh = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && now < _expiresAt)
        {
            return _current;
        }

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh && now < _expiresAt)
            {
                return _current;
            }

            var row = await _configRepository.GetAsync(BaseUrlsConfigKey).ConfigureAwait(false);
            var configuredValue = row?.ConfigValue?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                _current = new AiRuntimeOptions(_fallbackBaseUrls, string.Empty, false, row?.UpdatedBy, row?.UpdatedAt);
            }
            else
            {
                try
                {
                    var urls = AiClient.NormalizeBaseUrls([configuredValue]);
                    _current = new AiRuntimeOptions(urls, configuredValue, true, row?.UpdatedBy, row?.UpdatedAt);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogError(ex, "数据库中的 AI 节点运行时配置无效，临时回退启动配置。configKey={ConfigKey}", BaseUrlsConfigKey);
                    _current = new AiRuntimeOptions(_fallbackBaseUrls, configuredValue, true, row?.UpdatedBy, row?.UpdatedAt);
                }
            }

            _expiresAt = now.Add(_cacheTtl);
            return _current;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate()
    {
        _expiresAt = DateTimeOffset.MinValue;
    }
}

internal sealed record AiRuntimeOptions(
    IReadOnlyList<string> BaseUrls,
    string ConfiguredValue,
    bool HasRuntimeOverride,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt);
