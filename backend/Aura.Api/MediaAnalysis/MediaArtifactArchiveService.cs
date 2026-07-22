using System.Data;
using System.Net;
using System.Security.Cryptography;
using Aura.Api.Data;
using Aura.Api.Internal;
using Dapper;

namespace Aura.Api.MediaAnalysis;

internal sealed class MediaArtifactRepository(PgSqlConnectionFactory connectionFactory, IConfiguration configuration)
{
    public async Task<object> GetStatusAsync(long? tenantId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var counts = (await connection.QueryAsync<dynamic>(new CommandDefinition(
            "SELECT archive_status AS Status,COUNT(*) AS Count,MIN(created_at) AS OldestAt FROM media_artifact WHERE (@TenantId IS NULL OR tenant_id=@TenantId) GROUP BY archive_status",
            new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
        var recent = (await connection.QueryAsync<dynamic>(new CommandDefinition(
            """
            SELECT artifact_id AS ArtifactId,tenant_id AS TenantId,analysis_event_id AS AnalysisEventId,
              archive_uri AS ArchiveUri,media_type AS MediaType,content_type AS ContentType,size_bytes AS SizeBytes,
              sha256 AS Sha256,archive_status AS ArchiveStatus,attempt_count AS AttemptCount,
              next_attempt_at AS NextAttemptAt,last_error AS LastError,archived_at AS ArchivedAt,created_at AS CreatedAt
            FROM media_artifact WHERE (@TenantId IS NULL OR tenant_id=@TenantId) ORDER BY artifact_id DESC LIMIT @Limit
            """,
            new { TenantId = tenantId, Limit = Math.Clamp(limit, 1, 500) }, cancellationToken: cancellationToken))).AsList();
        return new { counts, recent };
    }

    public async Task<MediaArtifactArchiveRecord?> ClaimAsync(
        string workerId,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaArtifactArchiveRecord>(new CommandDefinition(
            """
            WITH due AS (
              SELECT artifact_id FROM media_artifact
              WHERE archive_status IN ('pending','retry_wait') AND provider_uri IS NOT NULL
                AND next_attempt_at<=NOW() AND (lock_until IS NULL OR lock_until<NOW())
              ORDER BY artifact_id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE media_artifact artifact SET archive_status='archiving',attempt_count=attempt_count+1,
              locked_by=@WorkerId,lock_until=NOW()+@Lease,updated_at=NOW()
            FROM due WHERE artifact.artifact_id=due.artifact_id
            RETURNING artifact.artifact_id AS ArtifactId,artifact.tenant_id AS TenantId,
              artifact.provider_uri AS ProviderUri,artifact.media_type AS MediaType,
              artifact.content_type AS ContentType,artifact.size_bytes AS SizeBytes,
              artifact.sha256 AS Sha256,artifact.attempt_count AS AttemptCount,
              artifact.created_at AS CreatedAt
            """,
            new { WorkerId = workerId, Lease = lease }, cancellationToken: cancellationToken));
    }

    public async Task MarkArchivedAsync(
        long artifactId,
        string archiveUri,
        string contentType,
        long sizeBytes,
        string sha256,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE media_artifact SET archive_status='archived',archive_uri=@ArchiveUri,
              content_type=@ContentType,size_bytes=@SizeBytes,sha256=@Sha256,archived_at=NOW(),
              locked_by=NULL,lock_until=NULL,last_error=NULL,updated_at=NOW()
            WHERE artifact_id=@ArtifactId
            """,
            new { ArtifactId = artifactId, ArchiveUri = archiveUri, ContentType = contentType, SizeBytes = sizeBytes, Sha256 = sha256 },
            cancellationToken: cancellationToken));
    }

    public async Task MarkFailureAsync(MediaArtifactArchiveRecord artifact, Exception exception, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Clamp(configuration.GetValue("MediaAnalysis:Artifacts:MaxAttempts", 8), 1, 100);
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE media_artifact SET
              archive_status=CASE WHEN @AttemptCount>=@MaxAttempts THEN 'dead_letter' ELSE 'retry_wait' END,
              next_attempt_at=CASE WHEN @AttemptCount>=@MaxAttempts THEN next_attempt_at
                ELSE NOW()+(LEAST(1800,POWER(2,LEAST(@AttemptCount,10))) * INTERVAL '1 second') END,
              locked_by=NULL,lock_until=NULL,last_error=LEFT(@Error,2000),updated_at=NOW()
            WHERE artifact_id=@ArtifactId
            """,
            new { artifact.ArtifactId, artifact.AttemptCount, MaxAttempts = maxAttempts, Error = exception.Message },
            cancellationToken: cancellationToken));
    }

    public async Task<int> ReplayAsync(long? tenantId, IReadOnlyList<long>? artifactIds, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        if (artifactIds is { Count: > 0 })
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE media_artifact SET archive_status='pending',next_attempt_at=NOW(),locked_by=NULL,
                  lock_until=NULL,last_error=NULL,updated_at=NOW()
                WHERE artifact_id=ANY(@ArtifactIds) AND (@TenantId IS NULL OR tenant_id=@TenantId)
                  AND archive_status IN ('retry_wait','failed','dead_letter')
                """,
                new { TenantId = tenantId, ArtifactIds = artifactIds.Distinct().Take(1000).ToArray() }, cancellationToken: cancellationToken));
        }
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            WITH selected AS (
              SELECT artifact_id FROM media_artifact WHERE archive_status='dead_letter'
                AND (@TenantId IS NULL OR tenant_id=@TenantId)
              ORDER BY artifact_id LIMIT @Limit FOR UPDATE SKIP LOCKED)
            UPDATE media_artifact artifact SET archive_status='pending',next_attempt_at=NOW(),
              locked_by=NULL,lock_until=NULL,last_error=NULL,updated_at=NOW()
            FROM selected WHERE artifact.artifact_id=selected.artifact_id
            """,
            new { TenantId = tenantId, Limit = Math.Clamp(limit, 1, 1000) }, cancellationToken: cancellationToken));
    }
}

internal sealed class MediaArtifactArchiveService(
    IHttpClientFactory httpClientFactory,
    MediaAnalysisOutboundUrlPolicy urlPolicy,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment)
{
    public async Task<ArchivedArtifact> ArchiveAsync(MediaArtifactArchiveRecord artifact, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Clamp(configuration.GetValue("MediaAnalysis:Artifacts:MaxBytes", 25L * 1024 * 1024), 1024, 2L * 1024 * 1024 * 1024);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MediaAnalysis:Artifacts:TimeoutSeconds", 60), 1, 900));
        var maxRedirects = Math.Clamp(configuration.GetValue("MediaAnalysis:Artifacts:MaxRedirects", 3), 0, 10);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var current = await urlPolicy.ValidateArtifactUriAsync(artifact.ProviderUri, timeoutSource.Token);
        HttpResponseMessage? response = null;
        try
        {
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await httpClientFactory.CreateClient("MediaArtifact").SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
                if (!IsRedirect(response.StatusCode)) break;
                if (redirect >= maxRedirects) throw new InvalidDataException("Artifact redirect limit was exceeded.");
                var location = response.Headers.Location
                    ?? throw new InvalidDataException("Artifact redirect did not include a Location header.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                response = null;
                current = await urlPolicy.ValidateArtifactUriAsync(next.ToString(), timeoutSource.Token);
            }

            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > maxBytes) throw new InvalidDataException($"Artifact exceeds the {maxBytes}-byte limit.");
            var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
                ?? artifact.ContentType?.ToLowerInvariant()
                ?? "application/octet-stream";
            EnsureContentType(contentType);

            var created = NormalizeUtc(artifact.CreatedAt);
            var relativeDirectory = Path.Combine("media-analysis", artifact.TenantId.ToString(), created.ToString("yyyy"), created.ToString("MM"));
            var extension = Extension(contentType, artifact.MediaType);
            var storageRoot = ProjectPaths.ResolveStorageRoot(hostEnvironment);
            var directory = Path.GetFullPath(Path.Combine(storageRoot, relativeDirectory));
            var root = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Artifact archive path escaped the storage root.");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, $"artifact-{artifact.ArtifactId}{extension}");
            var temporary = target + $".{Guid.NewGuid():N}.part";
            long written = 0;
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var input = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, timeoutSource.Token)) > 0)
                    {
                        written += read;
                        if (written > maxBytes) throw new InvalidDataException($"Artifact exceeds the {maxBytes}-byte limit.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token);
                    }
                    await output.FlushAsync(timeoutSource.Token);
                }
                if (artifact.SizeBytes.HasValue && artifact.SizeBytes.Value != written)
                    throw new InvalidDataException("Artifact size does not match the provider metadata.");
                var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(artifact.Sha256)
                    && !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(sha256),
                        System.Text.Encoding.ASCII.GetBytes(artifact.Sha256.Trim().ToLowerInvariant())))
                    throw new InvalidDataException("Artifact SHA-256 does not match the provider metadata.");
                File.Move(temporary, target, true);
                var archiveUri = "/storage/" + Path.Combine(relativeDirectory, Path.GetFileName(target)).Replace('\\', '/');
                return new ArchivedArtifact(archiveUri, contentType, written, sha256);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    private void EnsureContentType(string contentType)
    {
        var allowed = configuration.GetSection("MediaAnalysis:Artifacts:AllowedContentTypes").Get<string[]>()
            ?? ["image/", "video/", "application/octet-stream"];
        if (!allowed.Any(value => value.EndsWith("/", StringComparison.Ordinal)
                ? contentType.StartsWith(value, StringComparison.OrdinalIgnoreCase)
                : string.Equals(contentType, value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Artifact content type '{contentType}' is not allowed.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static string Extension(string contentType, string mediaType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        _ when mediaType.Contains("image", StringComparison.OrdinalIgnoreCase) => ".img",
        _ when mediaType.Contains("video", StringComparison.OrdinalIgnoreCase) => ".video",
        _ => ".bin"
    };
    private static DateTime NormalizeUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

internal sealed class MediaArtifactArchiveHostedService(
    MediaArtifactRepository repository,
    MediaArtifactArchiveService archiveService,
    BackgroundWorkerHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<MediaArtifactArchiveHostedService> logger) : BackgroundService
{
    private const string WorkerName = "media-artifact-archive";
    private readonly string _workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(250, configuration.GetValue("MediaAnalysis:Artifacts:PollMilliseconds", 1000)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var artifact = await repository.ClaimAsync(_workerId, TimeSpan.FromMinutes(2), stoppingToken);
                if (artifact is null)
                {
                    await heartbeat.SuccessAsync(WorkerName, _workerId, stoppingToken);
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }
                try
                {
                    var archived = await archiveService.ArchiveAsync(artifact, stoppingToken);
                    await repository.MarkArchivedAsync(artifact.ArtifactId, archived.ArchiveUri, archived.ContentType,
                        archived.SizeBytes, archived.Sha256, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Media artifact archive failed. artifactId={ArtifactId}", artifact.ArtifactId);
                    await repository.MarkFailureAsync(artifact, ex, stoppingToken);
                }
                await heartbeat.SuccessAsync(WorkerName, _workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media artifact archive worker iteration failed.");
                await heartbeat.FailureAsync(WorkerName, _workerId, ex, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

internal sealed record MediaArtifactArchiveRecord(
    long ArtifactId,
    long TenantId,
    string ProviderUri,
    string MediaType,
    string? ContentType,
    long? SizeBytes,
    string? Sha256,
    int AttemptCount,
    DateTime CreatedAt);
internal sealed record ArchivedArtifact(string ArchiveUri, string ContentType, long SizeBytes, string Sha256);
internal sealed record ArtifactReplayRequest(IReadOnlyList<long>? ArtifactIds, int Limit = 100, long? TenantId = null);
