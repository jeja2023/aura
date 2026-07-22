using System.Text.Json;
using System.Net;
using Aura.Api.Graph;
using Aura.Api.MediaAnalysis;
using Aura.Api.Vector;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aura.Api.Tests;

public sealed class MediaAnalysisContractTests
{
    [Fact]
    public void StandardEventEnvelopeUsesSnakeCaseContract()
    {
        var payload = JsonSerializer.SerializeToElement(new { track_id = "track-1" });
        var envelope = new MediaAnalysisEventEnvelope(
            "1.0", "evt-1", null, "tenant-a", "provider-a", "sub-1", "camera-1", 1,
            "track.updated", DateTimeOffset.Parse("2026-07-22T10:30:21+08:00"), null, "trace-1", payload);

        var json = JsonSerializer.Serialize(envelope, MediaAnalysisJson.Options);

        Assert.Contains("\"schema_version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"event_id\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", json, StringComparison.Ordinal);
        var roundTrip = JsonSerializer.Deserialize<MediaAnalysisEventEnvelope>(json, MediaAnalysisJson.Options);
        Assert.NotNull(roundTrip);
        Assert.Equal(envelope.EventId, roundTrip.EventId);
        Assert.Equal(envelope.Payload.GetRawText(), roundTrip.Payload.GetRawText());
    }

    [Fact]
    public void ProviderResponsesUseTheSameSnakeCaseProtocol()
    {
        const string capabilitiesJson = """
            {
              "protocol_version": "1.0",
              "capabilities": ["image.sync"],
              "pipelines": [{"code":"person-reid","models":["reid"],"embedding_dimension":512}]
            }
            """;
        const string submissionJson = """
            {"external_id":"job-42","state":"completed","result":{"confidence":0.9}}
            """;

        var capabilities = JsonSerializer.Deserialize<ProviderCapabilities>(capabilitiesJson, MediaAnalysisJson.Options);
        var submission = JsonSerializer.Deserialize<ProviderSubmission>(submissionJson, MediaAnalysisJson.Options);

        Assert.Equal("1.0", capabilities?.ProtocolVersion);
        Assert.Equal(512, capabilities?.Pipelines.Single().EmbeddingDimension);
        Assert.Equal("job-42", submission?.ExternalId);
        Assert.True(submission?.Result.HasValue);
    }

    [Theory]
    [InlineData("completed", "completed")]
    [InlineData("succeeded", "completed")]
    [InlineData("started", "running")]
    [InlineData("canceled", "cancelled")]
    [InlineData("unknown", "accepted")]
    public void ProviderJobStatesAreNormalized(string input, string expected)
    {
        Assert.Equal(expected, MediaAnalysisRepository.NormalizeJobState(input));
    }

    [Fact]
    public void GraphEdgeKeysAreStableAndTenantScoped()
    {
        var first = ArangoGraphRepository.StableEdgeKey(1, "visited", "persons/person-1", "rooms/room-1", "2026-07-22T10:00:00Z");
        var second = ArangoGraphRepository.StableEdgeKey(1, "visited", "persons/person-1", "rooms/room-1", "2026-07-22T10:00:00Z");
        var otherTenant = ArangoGraphRepository.StableEdgeKey(2, "visited", "persons/person-1", "rooms/room-1", "2026-07-22T10:00:00Z");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherTenant);
        Assert.StartsWith("t1_visited_", first, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorValidationRejectsWrongDimensionAndNonFiniteValues()
    {
        Assert.Throws<ArgumentException>(() => VectorValidation.Validate(new float[511]));
        var invalid = Enumerable.Repeat(0.1f, 512).ToArray();
        invalid[12] = float.NaN;
        Assert.Throws<ArgumentException>(() => VectorValidation.Validate(invalid));
    }

    [Fact]
    public void VectorSqlLiteralUsesInvariantRepresentation()
    {
        var feature = Enumerable.Repeat(0.25f, 512).ToArray();
        VectorValidation.Validate(feature);
        var literal = VectorValidation.ToSqlLiteral(feature);
        Assert.StartsWith("[0.25,0.25", literal, StringComparison.Ordinal);
        Assert.EndsWith("]", literal, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorNormalizationProducesUnitLengthWithoutMutatingInput()
    {
        var input = Enumerable.Repeat(2f, 512).ToArray();
        var normalized = VectorValidation.Normalize(input);

        Assert.All(input, value => Assert.Equal(2f, value));
        Assert.InRange(Math.Sqrt(normalized.Sum(value => (double)value * value)), 0.999999, 1.000001);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, false)]
    public void VectorBackfillOnlyRestartsCheckpointForFirstBatch(
        bool restartRequested,
        int batchNumber,
        bool expected)
    {
        Assert.Equal(expected, VectorMigrationService.ShouldRestartCheckpoint(restartRequested, batchNumber));
    }

    [Theory]
    [InlineData("http://user:password@example.com", "credentials")]
    [InlineData("https://example.com?target=http://internal", "query string")]
    [InlineData("file:///etc/passwd", "HTTP")]
    public void ProviderUrlSyntaxRejectsUnsafeForms(string url, string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(() => MediaAnalysisOutboundUrlPolicy.ValidateSyntax(url));
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.20.5")]
    [InlineData("192.168.1.2")]
    [InlineData("169.254.10.1")]
    [InlineData("fc00::1")]
    public void ProviderUrlPolicyClassifiesPrivateAndReservedAddresses(string value)
    {
        Assert.True(MediaAnalysisOutboundUrlPolicy.IsRestrictedAddress(IPAddress.Parse(value)));
    }

    [Fact]
    public async Task SecretSchemeResolvesFromNeutralConfigurationNamespace()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MediaAnalysis:Secrets:provider-a:webhook"] = "test-secret"
        }).Build();
        var resolver = new ConfigurationSecretReferenceResolver(configuration);

        Assert.Equal("test-secret", await resolver.ResolveAsync("secret://provider-a/webhook"));
    }

    [Theory]
    [InlineData("plain-text-secret")]
    [InlineData("env://BAD NAME")]
    [InlineData("config://MediaAnalysis:..:Password")]
    [InlineData("secret://provider/../password")]
    public void SecretReferencesRejectPlaintextAndUnsafeKeys(string value)
    {
        Assert.Throws<InvalidDataException>(() => SecretReferenceValidator.Validate(value));
    }

    [Theory]
    [InlineData("env://AURA_PROVIDER_TOKEN")]
    [InlineData("config://MediaAnalysis:Secrets:provider-token")]
    [InlineData("secret://provider-a/webhook")]
    public void SecretReferencesAcceptSupportedSchemes(string value)
    {
        Assert.Equal(value, SecretReferenceValidator.Validate(value));
    }

    [Fact]
    public void OAuthClientCredentialUsesStructuredSecretMaterial()
    {
        const string secret = """
            {
              "client_id": "aura-client",
              "client_secret": "not-logged",
              "token_url": "https://identity.example/oauth/token",
              "scope": "analysis.read analysis.write"
            }
            """;

        var credential = OAuthClientCredentialsTokenProvider.ParseCredential(secret);

        Assert.Equal("aura-client", credential.ClientId);
        Assert.Equal("https://identity.example/oauth/token", credential.TokenUrl);
        Assert.Equal("analysis.read analysis.write", credential.Scope);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"client_id\":\"id\",\"client_secret\":\"secret\"}")]
    public void OAuthClientCredentialRejectsIncompleteSecret(string secret)
    {
        Assert.Throws<InvalidDataException>(() => OAuthClientCredentialsTokenProvider.ParseCredential(secret));
    }

    [Fact]
    public void ProviderTrackMappingIsStableAndDoesNotReuseProviderIdentifier()
    {
        var first = MediaAnalysisBusinessProjector.StableTrackId(1, 2, 3, "provider-track-9");
        var second = MediaAnalysisBusinessProjector.StableTrackId(1, 2, 3, "provider-track-9");
        var otherSource = MediaAnalysisBusinessProjector.StableTrackId(1, 2, 4, "provider-track-9");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherSource);
        Assert.StartsWith("T_", first, StringComparison.Ordinal);
        Assert.NotEqual("provider-track-9", first);
    }

    [Fact]
    public void EmbeddingMetadataOmitsVectorAndIncludesEventProvenance()
    {
        using var payload = JsonDocument.Parse("""{"embedding":[0.1,0.2],"track_id":"track-1","_aura":{"spoofed":true}}""");
        var inbox = new MediaAnalysisInboxRecord(
            41, 7, 3, "evt-41", null, null, 5, null, "1.0", "object.detected",
            DateTime.UtcNow, null, DateTime.UtcNow, "{}", "hash", "processing", 1, "trace-41");

        using var metadata = JsonDocument.Parse(MediaAnalysisBusinessProjector.BuildEmbeddingMetadata(payload.RootElement, inbox, 99));

        Assert.False(metadata.RootElement.TryGetProperty("embedding", out _));
        Assert.Equal("track-1", metadata.RootElement.GetProperty("track_id").GetString());
        var provenance = metadata.RootElement.GetProperty("_aura");
        Assert.Equal(99, provenance.GetProperty("analysis_event_id").GetInt64());
        Assert.Equal("evt-41", provenance.GetProperty("event_id").GetString());
        Assert.Equal("trace-41", provenance.GetProperty("trace_id").GetString());
        Assert.False(provenance.TryGetProperty("spoofed", out _));
    }

    [Fact]
    public async Task GraphPathQueryPassesClampedDepthToAql()
    {
        var repository = new CapturingGraphRepository();
        var service = new GraphQueryService(repository);

        await service.CameraPathsAsync(new GraphPathRequest(3, 10, 20, 999, 500), CancellationToken.None);

        Assert.Equal("camera_paths", repository.Operation);
        Assert.Contains("LENGTH(path.edges) <= @maxDepth", repository.Aql, StringComparison.Ordinal);
        var bindVars = JsonSerializer.SerializeToElement(repository.BindVars);
        Assert.Equal(20, bindVars.GetProperty("maxDepth").GetInt32());
        Assert.Equal(100, bindVars.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void RoiGeometryUsesActualPolygonInsteadOfFirstConfiguredRoi()
    {
        const string polygon = "[{\"x\":0,\"y\":0},{\"x\":10,\"y\":0},{\"x\":10,\"y\":10},{\"x\":0,\"y\":10}]";
        Assert.True(RoiGeometry.Contains(polygon, 5, 5));
        Assert.False(RoiGeometry.Contains(polygon, 15, 5));
    }

    [Fact]
    public void RoiGeometrySupportsLegacyCoordinateArrays()
    {
        const string polygon = "[[0,0],[10,0],[10,10],[0,10]]";
        Assert.True(RoiGeometry.Contains(polygon, 5, 5));
        Assert.False(RoiGeometry.Contains(polygon, -1, 5));
    }

    private sealed class CapturingGraphRepository : IGraphRepository
    {
        public string? Operation { get; private set; }
        public string? Aql { get; private set; }
        public object? BindVars { get; private set; }

        public Task<JsonElement> QueryAsync(string operation, string aql, object bindVars, CancellationToken cancellationToken)
        {
            Operation = operation;
            Aql = aql;
            BindVars = bindVars;
            return Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
        }

        public Task EnsureInitializedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProjectEventAsync(JsonElement payload, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertVertexAsync(string collection, string key, object document, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertEdgeAsync(string collection, string key, string from, string to, object document, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GraphHealth(true, "test", "test", "test", null));
    }
}
