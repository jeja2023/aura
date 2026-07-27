using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.Internal;
using Dapper;
using Microsoft.IdentityModel.Tokens;

namespace Aura.Api.Product;

internal sealed class EvidenceExportService(
    PgSqlConnectionFactory connectionFactory,
    IHostEnvironment environment,
    ILogger<EvidenceExportService> logger)
{
    private readonly string exportRoot = Path.Combine(ProjectPaths.ResolveStorageRoot(environment), "evidence-exports");

    public async Task<ProductCommandResult> CreateAsync(
        long caseId,
        EvidenceExportCreateRequest request,
        bool canViewOriginal,
        string actor,
        CancellationToken cancellationToken)
    {
        var masking = request.MaskingPolicy.Trim().ToLowerInvariant();
        if (masking is not ("metadata_only" or "redacted" or "original"))
            return new(ProductCommandStatus.Invalid, Message: "maskingPolicy must be metadata_only, redacted, or original");
        if (masking == "original" && !canViewOriginal)
            return new(ProductCommandStatus.Forbidden, Message: "Original evidence export requires evidence.view_original");
        var expires = DateTimeOffset.UtcNow.AddHours(Math.Clamp(request.ExpiresInHours, 1, 168));
        await using var connection = connectionFactory.CreateConnection();
        var caseRow = await connection.QuerySingleOrDefaultAsync<ExportCaseRow>(new CommandDefinition(
            "SELECT case_id AS CaseId,case_no AS CaseNo,title AS Title,status AS Status,created_at AS CreatedAt FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId",
            new { request.TenantId, CaseId = caseId }, cancellationToken: cancellationToken));
        if (caseRow is null) return new(ProductCommandStatus.NotFound, Message: "Case not found");
        var ids = request.EvidenceIds?.Distinct().Take(500).ToArray();
        var evidence = (await connection.QueryAsync<ExportEvidenceRow>(new CommandDefinition(
            """
            SELECT evidence_id AS EvidenceId,evidence_type AS EvidenceType,source_type AS SourceType,source_id AS SourceId,
              object_key AS ObjectKey,sha256 AS Sha256,media_type AS MediaType,masking_policy AS MaskingPolicy,
              legal_hold AS LegalHold,purpose AS Purpose,added_by AS AddedBy,created_at AS CreatedAt
            FROM incident_case_evidence WHERE tenant_id=@TenantId AND case_id=@CaseId
              AND (@Ids IS NULL OR evidence_id=ANY(@Ids)) ORDER BY evidence_id
            """, new { request.TenantId, CaseId = caseId, Ids = ids is { Length: > 0 } ? ids : null }, cancellationToken: cancellationToken))).AsList();
        if (evidence.Count == 0) return new(ProductCommandStatus.Invalid, Message: "No case evidence matched the export selection");
        var manifest = new
        {
            schemaVersion = "1.0",
            tenantId = request.TenantId,
            @case = caseRow,
            purpose = request.Purpose.Trim(),
            maskingPolicy = masking,
            requestedBy = actor,
            generatedAt = DateTimeOffset.UtcNow,
            expiresAt = expires,
            watermark = $"Aura evidence | {caseRow.CaseNo} | {actor} | {DateTimeOffset.UtcNow:O}",
            items = evidence.Select(item => new
            {
                item.EvidenceId,item.EvidenceType,item.SourceType,item.SourceId,
                objectKey = masking == "metadata_only" ? null : item.ObjectKey,
                item.Sha256,item.MediaType,item.MaskingPolicy,item.LegalHold,item.Purpose,item.AddedBy,item.CreatedAt
            })
        };
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var manifestHash = Hash(manifestJson);
        var exportId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO evidence_export(tenant_id,case_id,purpose,masking_policy,manifest_json,manifest_sha256,artifact_key,status,requested_by,expires_at)
            VALUES(@TenantId,@CaseId,@Purpose,@Masking,@Manifest::jsonb,@Hash,'pending','generating',@Actor,@Expires)
            RETURNING evidence_export_id
            """, new { request.TenantId, CaseId = caseId, Purpose = request.Purpose.Trim(), Masking = masking, Manifest = manifestJson, Hash = manifestHash, Actor = actor, Expires = expires }, cancellationToken: cancellationToken));
        try
        {
            Directory.CreateDirectory(exportRoot);
            var path = Path.Combine(exportRoot, $"evidence-{exportId}.zip");
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, false))
            {
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var entryStream = manifestEntry.Open())
                await using (var writer = new StreamWriter(entryStream, new UTF8Encoding(false)))
                    await writer.WriteAsync(manifestJson.AsMemory(), cancellationToken);
                if (masking != "metadata_only")
                {
                    foreach (var item in evidence)
                    {
                        if (masking == "redacted" && string.IsNullOrWhiteSpace(item.MaskingPolicy)) continue;
                        var source = ResolveStorageFile(item.ObjectKey);
                        if (source is null || !File.Exists(source)) continue;
                        var extension = Path.GetExtension(source);
                        var entry = archive.CreateEntry($"items/{item.EvidenceId}{extension}", CompressionLevel.Fastest);
                        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                        await using var output = entry.Open();
                        await input.CopyToAsync(output, cancellationToken);
                    }
                }
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE evidence_export SET artifact_key=@Path,status='ready',completed_at=CURRENT_TIMESTAMP WHERE evidence_export_id=@Id",
                new { Path = path, Id = exportId }, cancellationToken: cancellationToken));
            return ProductCommandResult.Ok(new { evidenceExportId = exportId, status = "ready", manifestSha256 = manifestHash, expiresAt = expires, maskingPolicy = masking });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Evidence export generation failed. exportId={ExportId}", exportId);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE evidence_export SET status='failed',completed_at=CURRENT_TIMESTAMP WHERE evidence_export_id=@Id",
                new { Id = exportId }, cancellationToken: CancellationToken.None));
            return new(ProductCommandStatus.Invalid, new { evidenceExportId = exportId }, "Evidence artifact generation failed");
        }
    }

    public async Task<ProductCommandResult> CreateGrantAsync(
        long tenantId,
        long exportId,
        EvidenceAccessGrantRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Grantee) || string.IsNullOrWhiteSpace(request.Purpose))
            return new(ProductCommandStatus.Invalid, Message: "grantee and purpose are required");
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var id = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(request.ExpiresInMinutes, 5, 1440));
        await using var connection = connectionFactory.CreateConnection();
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO evidence_access_grant(grant_id,evidence_export_id,grantee,purpose,token_hash,expires_at,max_downloads,created_by)
            SELECT @Id,evidence_export_id,@Grantee,@Purpose,@Hash,@Expires,@Max,@Actor FROM evidence_export
            WHERE evidence_export_id=@ExportId AND tenant_id=@TenantId AND status='ready' AND expires_at>CURRENT_TIMESTAMP
            """, new
            {
                Id = id, ExportId = exportId, TenantId = tenantId, Grantee = request.Grantee.Trim(), Purpose = request.Purpose.Trim(),
                Hash = Hash(token), Expires = expires, Max = Math.Clamp(request.MaxDownloads, 1, 20), Actor = actor
            }, cancellationToken: cancellationToken));
        return inserted > 0
            ? ProductCommandResult.Ok(new { grantId = id, token, expiresAt = expires, maxDownloads = Math.Clamp(request.MaxDownloads, 1, 20) })
            : new(ProductCommandStatus.NotFound, Message: "Ready evidence export not found or already expired");
    }

    public async Task<EvidenceDownload?> AuthorizeDownloadAsync(
        long tenantId,
        long exportId,
        string? token,
        string actor,
        bool canViewOriginal,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var export = await connection.QuerySingleOrDefaultAsync<ExportArtifactRow>(new CommandDefinition(
            """
            SELECT evidence_export_id AS EvidenceExportId,case_id AS CaseId,masking_policy AS MaskingPolicy,
              artifact_key AS ArtifactKey,manifest_sha256 AS ManifestSha256,status AS Status,expires_at AS ExpiresAt
            FROM evidence_export WHERE tenant_id=@TenantId AND evidence_export_id=@ExportId FOR UPDATE
            """, new { TenantId = tenantId, ExportId = exportId }, transaction, cancellationToken: cancellationToken));
        if (export is null || export.Status != "ready" || export.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        if (export.MaskingPolicy == "original" && !canViewOriginal) return null;
        if (!string.IsNullOrWhiteSpace(token))
        {
            var grant = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                """
                UPDATE evidence_access_grant SET download_count=download_count+1
                WHERE evidence_export_id=@ExportId AND token_hash=@Hash AND revoked_at IS NULL
                  AND expires_at>CURRENT_TIMESTAMP AND download_count<max_downloads
                RETURNING grant_id
                """, new { ExportId = exportId, Hash = Hash(token) }, transaction, cancellationToken: cancellationToken));
            if (!grant.HasValue) return null;
        }
        await transaction.CommitAsync(cancellationToken);
        var path = Path.GetFullPath(export.ArtifactKey);
        if (!path.StartsWith(Path.GetFullPath(exportRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        return new(path, $"evidence-{exportId}.zip", export.ManifestSha256, actor);
    }

    private string? ResolveStorageFile(string objectKey)
    {
        if (Uri.TryCreate(objectKey, UriKind.Absolute, out _)) return null;
        var storage = Path.GetFullPath(ProjectPaths.ResolveStorageRoot(environment));
        var path = Path.GetFullPath(Path.IsPathRooted(objectKey) ? objectKey : Path.Combine(storage, objectKey));
        return path.StartsWith(storage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ExportCaseRow(long CaseId,string CaseNo,string Title,string Status,DateTimeOffset CreatedAt);
    private sealed record ExportEvidenceRow(long EvidenceId,string EvidenceType,string SourceType,string SourceId,string ObjectKey,string Sha256,string? MediaType,string? MaskingPolicy,bool LegalHold,string Purpose,string AddedBy,DateTimeOffset CreatedAt);
    private sealed record ExportArtifactRow(long EvidenceExportId,long CaseId,string MaskingPolicy,string ArtifactKey,string ManifestSha256,string Status,DateTimeOffset ExpiresAt);
}

internal sealed record EvidenceDownload(string Path,string FileName,string ManifestSha256,string DownloadedBy);
