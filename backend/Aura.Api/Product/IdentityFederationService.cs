using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.Cache;
using Aura.Api.Internal;
using Aura.Api.MediaAnalysis;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Aura.Api.Product;

internal sealed class IdentityFederationService(
    PgSqlConnectionFactory connectionFactory,
    MediaAnalysisOutboundUrlPolicy outboundUrlPolicy,
    ISecretReferenceResolver secretResolver,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpClientFactory,
    IConfiguration applicationConfiguration,
    RedisCacheService cache,
    AuditRepository audit,
    ILogger<IdentityFederationService> logger)
{
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> Managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDataProtector protector = dataProtection.CreateProtector("Aura.Oidc.Pkce.v1");

    public async Task<ProductCommandResult> CreateProviderAsync(
        OidcProviderWriteRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        Uri authority;
        try
        {
            authority = await outboundUrlPolicy.ValidateAsync(request.Authority.TrimEnd('/'), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or UriFormatException)
        {
            return new(ProductCommandStatus.Invalid, Message: ex.Message);
        }
        if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect)
            || redirect.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(redirect.Fragment))
            return new(ProductCommandStatus.Invalid, Message: "redirectUri must be an absolute HTTP(S) URL without a fragment");
        var code = CleanCode(request.ProviderCode);
        var scopes = (request.Scopes ?? ["openid", "profile", "email", "groups"])
            .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (!scopes.Contains("openid", StringComparer.Ordinal)) scopes.Insert(0, "openid");
        if (!string.IsNullOrWhiteSpace(request.ClientSecretRef) && !IsSecretReference(request.ClientSecretRef))
            return new(ProductCommandStatus.Invalid, Message: "clientSecretRef must use env://, vault://, or k8s://; plain secrets are rejected");
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO oidc_provider_config(tenant_id,provider_code,authority,client_id,client_secret_ref,redirect_uri,
              scopes_json,require_pkce,require_mfa,enabled,version,updated_by)
            SELECT @TenantId,@Code,@Authority,@ClientId,@SecretRef,@Redirect,@Scopes::jsonb,TRUE,@RequireMfa,FALSE,
              COALESCE(MAX(version),0)+1,@Actor FROM oidc_provider_config
            WHERE tenant_id=@TenantId AND provider_code=@Code RETURNING oidc_provider_id
            """, new
            {
                request.TenantId,
                Code = code,
                Authority = authority.ToString().TrimEnd('/'),
                ClientId = Required(request.ClientId, "clientId", 256),
                SecretRef = CleanNullable(request.ClientSecretRef, 512),
                Redirect = redirect.ToString(),
                Scopes = JsonSerializer.Serialize(scopes),
                request.RequireMfa,
                Actor = actor
            }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { oidcProviderId = id, tenantId = request.TenantId, providerCode = code, enabled = false, requiresValidation = true });
    }

    public async Task<IReadOnlyList<OidcProviderRow>> ListProvidersAsync(long tenantId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<OidcProviderRow>(new CommandDefinition(
            $"{ProviderColumns} WHERE tenant_id=@TenantId ORDER BY provider_code,version DESC",
            new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<ProductCommandResult> ValidateProviderAsync(long tenantId, long providerId, CancellationToken cancellationToken)
    {
        var provider = await GetProviderAsync(tenantId, providerId, false, cancellationToken);
        if (provider is null) return new(ProductCommandStatus.NotFound, Message: "OIDC provider not found");
        try
        {
            var configuration = await GetConfigurationAsync(provider.Authority, cancellationToken);
            return ProductCommandResult.Ok(new
            {
                provider.OidcProviderId,
                issuer = configuration.Issuer,
                authorizationEndpoint = configuration.AuthorizationEndpoint,
                tokenEndpoint = configuration.TokenEndpoint,
                signingKeyCount = configuration.SigningKeys.Count,
                supportsPkce = true,
                validatedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OIDC validation failed. providerId={ProviderId}", providerId);
            return new(ProductCommandStatus.Invalid, Message: $"OIDC discovery failed: {ex.Message}");
        }
    }

    public async Task<ProductCommandResult> SetProviderEnabledAsync(
        long providerId,
        OidcProviderTransitionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        if (request.Enabled)
        {
            var validation = await ValidateProviderAsync(request.TenantId, providerId, cancellationToken);
            if (validation.Status != ProductCommandStatus.Success) return validation;
        }
        await using var connection = connectionFactory.CreateConnection();
        var version = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            UPDATE oidc_provider_config SET enabled=@Enabled,version=version+1,updated_by=@Actor,updated_at=CURRENT_TIMESTAMP
            WHERE oidc_provider_id=@Id AND tenant_id=@TenantId AND version=@ExpectedVersion
              AND (NOT @Enabled OR EXISTS(SELECT 1 FROM identity_group_mapping m WHERE m.oidc_provider_id=@Id AND m.tenant_id=@TenantId AND m.status='active'))
            RETURNING version
            """, new { Id = providerId, request.TenantId, request.Enabled, request.ExpectedVersion, Actor = actor }, cancellationToken: cancellationToken));
        return version.HasValue
            ? ProductCommandResult.Ok(new { oidcProviderId = providerId, request.Enabled, version })
            : new(ProductCommandStatus.Conflict, Message: "Provider version conflict, missing active group mapping, or provider not found");
    }

    public async Task<ProductCommandResult> CreateMappingAsync(
        IdentityGroupMappingWriteRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO identity_group_mapping(oidc_provider_id,external_group,role_name,tenant_id,status,version)
            SELECT @ProviderId,@Group,r.role_name,@TenantId,'draft',1 FROM sys_role r
            WHERE r.role_name=@Role AND EXISTS(SELECT 1 FROM oidc_provider_config p WHERE p.oidc_provider_id=@ProviderId AND p.tenant_id=@TenantId)
            RETURNING mapping_id
            """, new
            {
                ProviderId = request.OidcProviderId,
                Group = Required(request.ExternalGroup, "externalGroup", 256),
                Role = Required(request.RoleName, "roleName", 64),
                request.TenantId
            }, cancellationToken: cancellationToken));
        return id.HasValue
            ? ProductCommandResult.Ok(new { mappingId = id.Value, status = "draft" })
            : new(ProductCommandStatus.Invalid, Message: "Provider, tenant, or role is invalid");
    }

    public async Task<ProductCommandResult> ApproveMappingAsync(long tenantId, long mappingId, string actor, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE identity_group_mapping SET status='active',approved_by=@Actor,version=version+1 WHERE tenant_id=@TenantId AND mapping_id=@Id AND status='draft'",
            new { TenantId = tenantId, Id = mappingId, Actor = actor }, cancellationToken: cancellationToken));
        return updated > 0
            ? ProductCommandResult.Ok(new { mappingId, status = "active" })
            : new(ProductCommandStatus.NotFound, Message: "Draft mapping not found");
    }

    public async Task<object> PreviewMappingsAsync(IdentityGroupPreviewRequest request, CancellationToken cancellationToken)
    {
        var groups = request.Groups.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        await using var connection = connectionFactory.CreateConnection();
        var matches = (await connection.QueryAsync<GroupMatchRow>(new CommandDefinition(
            """
            SELECT mapping_id AS MappingId,external_group AS ExternalGroup,role_name AS RoleName,status AS Status,approved_by AS ApprovedBy
            FROM identity_group_mapping WHERE tenant_id=@TenantId AND oidc_provider_id=@ProviderId AND external_group=ANY(@Groups)
            ORDER BY external_group,version DESC
            """, new { request.TenantId, ProviderId = request.OidcProviderId, Groups = groups }, cancellationToken: cancellationToken))).AsList();
        return new
        {
            request.TenantId,
            request.OidcProviderId,
            suppliedGroups = groups,
            matches,
            activeRoles = matches.Where(item => item.Status == "active").Select(item => item.RoleName).Distinct(),
            unmatchedGroups = groups.Except(matches.Select(item => item.ExternalGroup), StringComparer.Ordinal),
            ambiguous = matches.Where(item => item.Status == "active").Select(item => item.RoleName).Distinct().Count() > 1
        };
    }

    public async Task<IResult> BeginAuthorizationAsync(
        HttpContext http,
        long tenantId,
        string providerCode,
        string? returnUrl,
        Guid? stepUpChallengeId,
        CancellationToken cancellationToken)
    {
        var provider = await GetProviderByCodeAsync(tenantId, CleanCode(providerCode), cancellationToken);
        if (provider is null) return AuraApiResults.NotFound("Enabled OIDC provider not found", 40470);
        if (stepUpChallengeId.HasValue && !await ChallengeCanReauthenticateAsync(http.User, tenantId, stepUpChallengeId.Value, cancellationToken))
            return AuraApiResults.Forbidden("Step-up challenge does not belong to the current active session", 40370);
        OpenIdConnectConfiguration discovery;
        try
        {
            discovery = await GetConfigurationAsync(provider.Authority, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OIDC discovery unavailable. provider={Provider}", provider.ProviderCode);
            return AuraApiResults.ServiceUnavailable("Identity provider is unavailable", 50370);
        }
        var state = RandomToken(32);
        var verifier = RandomToken(48);
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var nonce = RandomToken(24);
        var safeReturn = SafeReturnUrl(returnUrl);
        await using (var connection = connectionFactory.CreateConnection())
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO oidc_login_transaction(login_transaction_id,state_sha256,tenant_id,oidc_provider_id,
                  code_verifier_protected,nonce,return_url,step_up_challenge_id,expires_at)
                VALUES(@Id,@StateHash,@TenantId,@ProviderId,@Verifier,@Nonce,@ReturnUrl,@ChallengeId,CURRENT_TIMESTAMP+INTERVAL '10 minutes')
                """, new
                {
                    Id = Guid.NewGuid(),
                    StateHash = Hash(state),
                    TenantId = tenantId,
                    ProviderId = provider.OidcProviderId,
                    Verifier = protector.Protect(verifier),
                    Nonce = nonce,
                    ReturnUrl = safeReturn,
                    ChallengeId = stepUpChallengeId
                }, cancellationToken: cancellationToken));
        }
        http.Response.Cookies.Append("aura_oidc_state", state, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps || applicationConfiguration.GetValue("Security:Cookies:ForceSecure", false),
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/api/v1/identity/oidc"
        });
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = provider.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = provider.RedirectUri,
            ["scope"] = string.Join(' ', ParseArray(provider.ScopesJson)),
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        if (stepUpChallengeId.HasValue) parameters["prompt"] = "login";
        return Results.Redirect(QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, parameters));
    }

    public async Task<IResult> CompleteAuthorizationAsync(
        HttpContext http,
        string? code,
        string? state,
        string? error,
        IdentityAdminService identity,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error)) return AuraApiResults.BadRequest($"Identity provider rejected the login: {error}", 40070);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return AuraApiResults.BadRequest("OIDC callback is missing code or state", 40070);
        var cookieState = http.Request.Cookies["aura_oidc_state"];
        if (string.IsNullOrWhiteSpace(cookieState)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(cookieState)), SHA256.HashData(Encoding.UTF8.GetBytes(state))))
            return AuraApiResults.BadRequest("OIDC state binding failed", 40071);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var login = await connection.QuerySingleOrDefaultAsync<OidcLoginRow>(new CommandDefinition(
            """
            SELECT x.login_transaction_id AS LoginTransactionId,x.tenant_id AS TenantId,x.oidc_provider_id AS OidcProviderId,
              x.code_verifier_protected AS CodeVerifierProtected,x.nonce AS Nonce,x.return_url AS ReturnUrl,
              x.step_up_challenge_id AS StepUpChallengeId,p.provider_code AS ProviderCode,p.authority AS Authority,
              p.client_id AS ClientId,p.client_secret_ref AS ClientSecretRef,p.redirect_uri AS RedirectUri,
              p.require_mfa AS RequireMfa
            FROM oidc_login_transaction x JOIN oidc_provider_config p ON p.oidc_provider_id=x.oidc_provider_id
            WHERE x.state_sha256=@Hash AND x.status='pending' AND x.expires_at>CURRENT_TIMESTAMP AND p.enabled=TRUE
            FOR UPDATE OF x
            """, new { Hash = Hash(state) }, transaction, cancellationToken: cancellationToken));
        if (login is null) return AuraApiResults.BadRequest("OIDC transaction is expired or already used", 40072);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE oidc_login_transaction SET status='used',used_at=CURRENT_TIMESTAMP WHERE login_transaction_id=@Id",
            new { Id = login.LoginTransactionId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        http.Response.Cookies.Delete("aura_oidc_state", new CookieOptions { Path = "/api/v1/identity/oidc" });

        try
        {
            var discovery = await GetConfigurationAsync(login.Authority, cancellationToken);
            var secret = await secretResolver.ResolveAsync(login.ClientSecretRef, cancellationToken);
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = login.RedirectUri,
                ["client_id"] = login.ClientId,
                ["code_verifier"] = protector.Unprotect(login.CodeVerifierProtected)
            };
            if (!string.IsNullOrWhiteSpace(secret)) form["client_secret"] = secret;
            var client = httpClientFactory.CreateClient();
            using var tokenResponse = await client.PostAsync(discovery.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
            var tokenPayload = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode) throw new InvalidOperationException($"Token endpoint returned {(int)tokenResponse.StatusCode}");
            using var tokenDocument = JsonDocument.Parse(tokenPayload);
            var idToken = tokenDocument.RootElement.TryGetProperty("id_token", out var idTokenNode) ? idTokenNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(idToken)) throw new InvalidOperationException("Token response did not contain id_token");
            var validation = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = discovery.Issuer,
                ValidateAudience = true,
                ValidAudience = login.ClientId,
                ValidateLifetime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = discovery.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(2)
            });
            if (!validation.IsValid || validation.ClaimsIdentity is null) throw validation.Exception ?? new SecurityTokenValidationException("ID token validation failed");
            var principal = new ClaimsPrincipal(validation.ClaimsIdentity);
            if (!string.Equals(principal.FindFirst("nonce")?.Value, login.Nonce, StringComparison.Ordinal))
                throw new SecurityTokenValidationException("OIDC nonce mismatch");
            var subject = principal.FindFirst("sub")?.Value ?? throw new SecurityTokenValidationException("OIDC subject is missing");
            var amr = ReadMultiValueClaim(principal, "amr");
            var acr = principal.FindFirst("acr")?.Value;
            var hasMfa = amr.Any(item => item.Contains("mfa", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(acr) && (acr.Contains("mfa", StringComparison.OrdinalIgnoreCase) || acr.Contains("high", StringComparison.OrdinalIgnoreCase)));
            if (login.RequireMfa && !hasMfa) return AuraApiResults.Forbidden("The identity provider did not assert the required MFA strength", 40371);
            var groups = ReadMultiValueClaim(principal, "groups");
            var mapping = await ResolveActiveMappingAsync(connection, login.TenantId, login.OidcProviderId, groups, cancellationToken);
            if (mapping is null) return AuraApiResults.Forbidden("No approved tenant role mapping matched the asserted groups", 40372);
            var stableUserName = $"oidc_{login.TenantId}_{Hash($"{login.ProviderCode}|{subject}")[..16]}";
            var display = principal.FindFirst("name")?.Value ?? principal.FindFirst("preferred_username")?.Value ?? stableUserName;
            var roleId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT role_id FROM sys_role WHERE role_name=@Role LIMIT 1", new { Role = mapping.RoleName }, cancellationToken: cancellationToken));
            var userId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO sys_user(user_name,display_name,password_hash,role_id,status,must_change_password,last_login_at)
                VALUES(@UserName,@Display,@PasswordHash,@RoleId,1,FALSE,CURRENT_TIMESTAMP)
                ON CONFLICT(user_name) DO UPDATE SET display_name=EXCLUDED.display_name,role_id=EXCLUDED.role_id,status=1,last_login_at=CURRENT_TIMESTAMP
                RETURNING user_id
                """, new
                {
                    UserName = stableUserName,
                    Display = display[..Math.Min(display.Length, 64)],
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(RandomToken(32)),
                    RoleId = roleId
                }, cancellationToken: cancellationToken));
            var permissionJson = await connection.QuerySingleAsync<PermissionJsonRow>(new CommandDefinition(
                """
                SELECT r.permission_json::text AS RolePermissions,
                  COALESCE((SELECT permission_json::text FROM tenant_role_scope WHERE tenant_id=@TenantId AND role_name=r.role_name),'[]') AS TenantPermissions
                FROM sys_role r WHERE r.role_id=@RoleId
                """, new { TenantId = login.TenantId, RoleId = roleId }, cancellationToken: cancellationToken));
            var permissions = AuraPermissions.Normalize(AuraPermissions.ParsePermissionJson(permissionJson.RolePermissions)
                .Concat(AuraPermissions.ParsePermissionJson(permissionJson.TenantPermissions)));
            var sessionId = Guid.NewGuid();
            var issued = DateTimeOffset.UtcNow;
            var expires = issued.AddMinutes(Math.Clamp(applicationConfiguration.GetValue("Jwt:ExpireMinutes", 480), 5, 1440));
            var strength = hasMfa ? "mfa" : "federated";
            var userAgent = http.Request.Headers.UserAgent.ToString();
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO auth_session(session_id,tenant_id,user_id,user_name,provider_code,authentication_method,authentication_strength,
                  issued_at,expires_at,last_seen_at,oidc_subject,ip_address,user_agent)
                VALUES(@SessionId,@TenantId,@UserId,@UserName,@Provider,'oidc',@Strength,@Issued,@Expires,@Issued,@Subject,@Ip,@UserAgent)
                """, new
                {
                    SessionId = sessionId,
                    TenantId = login.TenantId,
                    UserId = userId,
                    UserName = stableUserName,
                    Provider = login.ProviderCode,
                    Strength = strength,
                    Issued = issued,
                    Expires = expires,
                    Subject = subject,
                    Ip = http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = userAgent[..Math.Min(userAgent.Length, 512)]
                }, cancellationToken: cancellationToken));
            if (login.StepUpChallengeId.HasValue)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE step_up_challenge SET status='verified',verified_at=CURRENT_TIMESTAMP WHERE challenge_id=@Id AND status='pending' AND expires_at>CURRENT_TIMESTAMP",
                    new { Id = login.StepUpChallengeId.Value }, cancellationToken: cancellationToken));
            }
            await audit.InsertOperationAsync(stableUserName, "OIDC login", $"tenantId={login.TenantId}, provider={login.ProviderCode}, strength={strength}, sessionId={sessionId}");
            return identity.IssueFederatedLogin(http, stableUserName, AuraHelpers.ConvertRole(mapping.RoleName), permissions,
                sessionId, login.TenantId, login.ProviderCode, amr, strength, acr, expires, login.ReturnUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OIDC callback failed. transactionId={TransactionId}", login.LoginTransactionId);
            return AuraApiResults.BadRequest("OIDC token validation or tenant mapping failed", 40073);
        }
    }

    public async Task<IReadOnlyList<AuthSessionRow>> ListSessionsAsync(long? tenantId, string? userName, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<AuthSessionRow>(new CommandDefinition(
            """
            SELECT session_id AS SessionId,tenant_id AS TenantId,user_name AS UserName,provider_code AS ProviderCode,
              authentication_method AS AuthenticationMethod,authentication_strength AS AuthenticationStrength,
              issued_at AS IssuedAt,expires_at AS ExpiresAt,revoked_at AS RevokedAt,revoked_by AS RevokedBy,
              revoke_reason AS RevokeReason,last_seen_at AS LastSeenAt,ip_address AS IpAddress,user_agent AS UserAgent
            FROM auth_session WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND (@UserName IS NULL OR user_name=@UserName)
            ORDER BY issued_at DESC LIMIT 500
            """, new { TenantId = tenantId, UserName = CleanNullable(userName, 128) }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<AuthSessionRow?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<AuthSessionRow>(new CommandDefinition(
            """
            SELECT session_id AS SessionId,tenant_id AS TenantId,user_name AS UserName,provider_code AS ProviderCode,
              authentication_method AS AuthenticationMethod,authentication_strength AS AuthenticationStrength,
              issued_at AS IssuedAt,expires_at AS ExpiresAt,revoked_at AS RevokedAt,revoked_by AS RevokedBy,
              revoke_reason AS RevokeReason,last_seen_at AS LastSeenAt,ip_address AS IpAddress,user_agent AS UserAgent
            FROM auth_session WHERE session_id=@SessionId
            """, new { SessionId = sessionId }, cancellationToken: cancellationToken));
    }

    public async Task<ProductCommandResult> RevokeSessionAsync(Guid sessionId, AuthSessionRevokeRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return new(ProductCommandStatus.Invalid, Message: "Revocation reason is required");
        await using var connection = connectionFactory.CreateConnection();
        var count = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE auth_session SET revoked_at=CURRENT_TIMESTAMP,revoked_by=@Actor,revoke_reason=@Reason WHERE session_id=@Id AND revoked_at IS NULL",
            new { Id = sessionId, Actor = actor, Reason = request.Reason.Trim()[..Math.Min(request.Reason.Trim().Length, 512)] }, cancellationToken: cancellationToken));
        if (count > 0) await cache.DeleteAsync($"aura:auth:session:{sessionId:N}");
        return count > 0 ? ProductCommandResult.Ok(new { sessionId, status = "revoked" }) : new(ProductCommandStatus.NotFound, Message: "Active session not found");
    }

    public async Task<ProductCommandResult> CreateStepUpAsync(ClaimsPrincipal user, StepUpChallengeRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(user.FindFirstValue("sid"), out var sessionId))
            return new(ProductCommandStatus.Invalid, Message: "The current token has no revocable session");
        var id = Guid.NewGuid();
        var alreadyStrong = user.FindAll("amr").Any(claim => claim.Value.Contains("mfa", StringComparison.OrdinalIgnoreCase));
        await using var connection = connectionFactory.CreateConnection();
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO step_up_challenge(challenge_id,session_id,action,resource_ref,required_strength,status,expires_at,verified_at)
            SELECT @Id,@SessionId,@Action,@Resource,@Strength,@Status,CURRENT_TIMESTAMP+INTERVAL '10 minutes',
              CASE WHEN @Status='verified' THEN CURRENT_TIMESTAMP ELSE NULL END
            WHERE EXISTS(SELECT 1 FROM auth_session WHERE session_id=@SessionId AND revoked_at IS NULL AND expires_at>CURRENT_TIMESTAMP)
            """, new
            {
                Id = id,
                SessionId = sessionId,
                Action = Required(request.Action, "action", 128),
                Resource = CleanNullable(request.ResourceRef, 256),
                Strength = Required(request.RequiredStrength, "requiredStrength", 64),
                Status = alreadyStrong ? "verified" : "pending"
            }, cancellationToken: cancellationToken));
        return inserted > 0
            ? ProductCommandResult.Ok(new { challengeId = id, status = alreadyStrong ? "verified" : "pending", expiresInSeconds = 600 })
            : new(ProductCommandStatus.NotFound, Message: "Active session not found");
    }

    private async Task<bool> ChallengeCanReauthenticateAsync(ClaimsPrincipal user, long tenantId, Guid challengeId, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sid"), out var sessionId)) return false;
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS(SELECT 1 FROM step_up_challenge c JOIN auth_session s ON s.session_id=c.session_id
              WHERE c.challenge_id=@ChallengeId AND c.session_id=@SessionId AND c.status='pending'
                AND c.expires_at>CURRENT_TIMESTAMP AND s.tenant_id=@TenantId)
            """, new { ChallengeId = challengeId, SessionId = sessionId, TenantId = tenantId }, cancellationToken: ct));
    }

    private async Task<OidcProviderRow?> GetProviderAsync(long tenantId, long providerId, bool enabledOnly, CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<OidcProviderRow>(new CommandDefinition(
            $"{ProviderColumns} WHERE tenant_id=@TenantId AND oidc_provider_id=@ProviderId AND (NOT @EnabledOnly OR enabled=TRUE)",
            new { TenantId = tenantId, ProviderId = providerId, EnabledOnly = enabledOnly }, cancellationToken: ct));
    }

    private async Task<OidcProviderRow?> GetProviderByCodeAsync(long tenantId, string code, CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<OidcProviderRow>(new CommandDefinition(
            $"{ProviderColumns} WHERE tenant_id=@TenantId AND provider_code=@Code AND enabled=TRUE ORDER BY version DESC LIMIT 1",
            new { TenantId = tenantId, Code = code }, cancellationToken: ct));
    }

    private async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string authority, CancellationToken ct)
    {
        var validated = await outboundUrlPolicy.ValidateAsync(authority.TrimEnd('/'), ct);
        var metadata = $"{validated.ToString().TrimEnd('/')}/.well-known/openid-configuration";
        var manager = Managers.GetOrAdd(metadata, key => new ConfigurationManager<OpenIdConnectConfiguration>(
            key,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = validated.Scheme == Uri.UriSchemeHttps }));
        return await manager.GetConfigurationAsync(ct);
    }

    private static async Task<GroupMatchRow?> ResolveActiveMappingAsync(System.Data.IDbConnection connection,long tenantId,long providerId,IReadOnlyList<string> groups,CancellationToken ct)
    {
        var matches = (await connection.QueryAsync<GroupMatchRow>(new CommandDefinition(
            """
            SELECT mapping_id AS MappingId,external_group AS ExternalGroup,role_name AS RoleName,status AS Status,approved_by AS ApprovedBy
            FROM identity_group_mapping WHERE tenant_id=@TenantId AND oidc_provider_id=@ProviderId
              AND status='active' AND external_group=ANY(@Groups)
            """, new { TenantId = tenantId, ProviderId = providerId, Groups = groups.ToArray() }, cancellationToken: ct))).AsList();
        var roles = matches.Select(item => item.RoleName).Distinct(StringComparer.Ordinal).ToArray();
        return roles.Length == 1 ? matches.First() : null;
    }

    private static IReadOnlyList<string> ReadMultiValueClaim(ClaimsPrincipal principal,string type) => principal.FindAll(type)
        .SelectMany(claim => claim.Value.StartsWith('[') ? ParseArray(claim.Value) : new[] { claim.Value })
        .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray();
    private static IReadOnlyList<string> ParseArray(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }
    private static string SafeReturnUrl(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') && !value.StartsWith("//") ? value : "/frontend/index.html";
    private static string RandomToken(int bytes) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CleanCode(string value)
    {
        var code = Required(value, "providerCode", 64).ToLowerInvariant();
        if (code.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_'))) throw new ArgumentException("providerCode contains unsupported characters");
        return code;
    }
    private static bool IsSecretReference(string value) => value.StartsWith("env://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("vault://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("k8s://", StringComparison.OrdinalIgnoreCase);
    private static string Required(string? value,string name,int max) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required") : value.Trim()[..Math.Min(value.Trim().Length,max)];
    private static string? CleanNullable(string? value,int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length,max)];

    private const string ProviderColumns = """
        SELECT oidc_provider_id AS OidcProviderId,tenant_id AS TenantId,provider_code AS ProviderCode,
          authority AS Authority,client_id AS ClientId,client_secret_ref AS ClientSecretRef,redirect_uri AS RedirectUri,
          scopes_json::text AS ScopesJson,require_pkce AS RequirePkce,require_mfa AS RequireMfa,
          enabled AS Enabled,version AS Version,updated_by AS UpdatedBy,updated_at AS UpdatedAt FROM oidc_provider_config
        """;

    internal sealed record OidcProviderRow(long OidcProviderId,long TenantId,string ProviderCode,string Authority,string ClientId,string? ClientSecretRef,string RedirectUri,string ScopesJson,bool RequirePkce,bool RequireMfa,bool Enabled,int Version,string UpdatedBy,DateTimeOffset UpdatedAt);
    private sealed record OidcLoginRow(Guid LoginTransactionId,long TenantId,long OidcProviderId,string CodeVerifierProtected,string Nonce,string ReturnUrl,Guid? StepUpChallengeId,string ProviderCode,string Authority,string ClientId,string? ClientSecretRef,string RedirectUri,bool RequireMfa);
    internal sealed record GroupMatchRow(long MappingId,string ExternalGroup,string RoleName,string Status,string? ApprovedBy);
    private sealed record PermissionJsonRow(string? RolePermissions,string? TenantPermissions);
}

internal sealed record AuthSessionRow(Guid SessionId,long? TenantId,string UserName,string ProviderCode,string AuthenticationMethod,string AuthenticationStrength,DateTimeOffset IssuedAt,DateTimeOffset ExpiresAt,DateTimeOffset? RevokedAt,string? RevokedBy,string? RevokeReason,DateTimeOffset LastSeenAt,string? IpAddress,string? UserAgent);
