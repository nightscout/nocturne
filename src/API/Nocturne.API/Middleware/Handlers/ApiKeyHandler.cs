using Nocturne.API.Authorization;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.API.Extensions;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for API keys sent via the api-secret header or ?secret= query parameter.
/// Resolves both noc_-prefixed tokens (SHA-256 lookup against TokenHash) and legacy Nightscout
/// secrets (SHA-1 lookup against LegacySecretHash) through DirectGrant rows.
/// Returns the grant's actual scopes rather than hardcoded admin permissions.
/// </summary>
public class ApiKeyHandler : IAuthHandler
{
    public int Priority => 400;

    public string Name => "ApiKeyHandler";

    private readonly IOAuthGrantRepository _grants;
    private readonly TimeProvider _timeProvider;
    private readonly LastUsedClaims _lastUsedClaims;
    private readonly ILogger<ApiKeyHandler> _logger;

    /// <param name="grants">The stored grants, pinned per read to the request's tenant.</param>
    /// <param name="lastUsedClaims">Decides which concurrent request stamps the grant.</param>
    public ApiKeyHandler(
        IOAuthGrantRepository grants,
        TimeProvider timeProvider,
        LastUsedClaims lastUsedClaims,
        ILogger<ApiKeyHandler> logger)
    {
        _grants = grants;
        _timeProvider = timeProvider;
        _lastUsedClaims = lastUsedClaims;
        _logger = logger;
    }

    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        // 1. Extract value from the api-secret header or ?secret= query param.
        //    Accept the api_secret (underscore) spelling too — some legacy Nightscout
        //    clients send it, and the V1 OpenAPI docs advertise it as accepted.
        var apiKey = context.Request.Headers["api-secret"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = context.Request.Headers["api_secret"].FirstOrDefault();
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = context.Request.Query["secret"].FirstOrDefault();
        }

        // 2. If null/empty -> Skip
        if (string.IsNullOrEmpty(apiKey))
            return AuthResult.Skip();

        // 3. Resolve tenant context
        if (context.GetTenantContext() is not { } tenantCtx)
        {
            _logger.LogWarning("API key provided but no tenant context resolved");
            return AuthResult.Failure("API key requires a resolved tenant");
        }

        // 4. Determine hash strategy based on prefix
        DirectGrantLookup lookup = apiKey.StartsWith("noc_", StringComparison.Ordinal)
            ? new DirectGrantLookup.ByTokenHash([HashUtils.Sha256Hex(apiKey)])
            // Legacy path: the value sent in the header is already a SHA-1 hex hash
            // (Nightscout clients pre-hash secrets before sending). Normalize to lowercase
            // to match the canonical stored form (HashUtils.Sha1Hex is lowercase) — some
            // clients (e.g. Android) emit uppercase hex, which the case-sensitive column
            // comparison would otherwise miss.
            : new DirectGrantLookup.ByLegacySecretHash(apiKey.ToLowerInvariant());

        // 5. Query for matching grant
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var found = await _grants.FindLiveDirectGrantAsync(tenantCtx.TenantId, lookup, now, CancellationToken.None);

        if (found == null)
        {
            _logger.LogDebug("No matching API key grant found");
            return AuthResult.Failure("Invalid API key");
        }

        var grant = found.Grant;
        new RequestMembership(grant.SubjectId, tenantCtx.TenantId, MembershipFacts.From(found.Membership)).StoreOn(context);

        // 6. Fire-and-forget last-used stamp
        if (_lastUsedClaims.TryClaim(grant.Id, grant.LastUsedAt))
        {
            _ = DirectGrantTokenHandler.RecordLastUsedAsync(
                _grants, _logger, grant.Id, tenantCtx.TenantId,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.FirstOrDefault());
        }

        // 7. If this is a migrated full-access secret's first use, nudge rotation to scoped keys.
        //    Minted noc_ tokens also carry a LegacySecretHash (for pre-hashing clients), so the
        //    nudge keys off IsMigrated rather than the hash's presence.
        if (grant.IsMigrated && grant.LastUsedAt == null)
        {
            var scopeFactory = context.RequestServices?.GetService<IServiceScopeFactory>();
            if (scopeFactory != null)
            {
                _ = SendRotationNudgeAsync(scopeFactory, tenantCtx, grant);
            }
        }

        _logger.LogDebug("API key authentication successful for grant {GrantId}, subject {SubjectId}",
            grant.Id, grant.SubjectId);

        return AuthResult.Success(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectId = grant.SubjectId,
            Scopes = [.. grant.Scopes],
            LimitTo24Hours = grant.LimitTo24Hours,
            TokenId = grant.Id,
            CredentialFingerprint = AuditFingerprint.Of(
                AuditFingerprint.ApiSecretDomain, grant.TokenHash ?? grant.LegacySecretHash),
        });
    }

    /// <remarks>
    /// Runs after the request may have ended, on a scope of its own opened on the grant's tenant: the
    /// notification is tenant-scoped, and a fresh scope carries no tenant until one is set. A site
    /// secret held by the device subject has no person behind it to read the nudge, so it goes to
    /// the tenant's owner.
    /// </remarks>
    private async Task SendRotationNudgeAsync(
        IServiceScopeFactory scopeFactory, TenantContext tenant, DirectGrantCredential grant)
    {
        try
        {
            await using var scope = TenantJobScope.Open(scopeFactory, tenant, "api-key-handler");
            var notificationService = scope.ServiceProvider.GetRequiredService<IInAppNotificationService>();

            string? recipient = grant.SubjectId.ToString();
            if (await scope.ServiceProvider.GetRequiredService<ISubjectRepository>()
                    .FindDeviceSubjectAsync(CancellationToken.None) == grant.SubjectId)
            {
                recipient = await scope.ServiceProvider.GetRequiredService<ITenantOwnerResolver>()
                    .GetOwnerSubjectIdAsync(tenant.TenantId);
            }

            if (recipient is null)
            {
                return;
            }

            await notificationService.CreateNotificationAsync(
                userId: recipient,
                type: "api-key-rotation",
                title: "Rotate your API key",
                category: NotificationCategory.ActionRequired,
                urgency: NotificationUrgency.Info,
                icon: "key",
                source: "api-key-handler",
                subtitle: "Your API key has full access. Create per-device keys with least privilege.",
                sourceId: grant.Id.ToString(),
                actions:
                [
                    new NotificationActionDto
                    {
                        ActionId = "manage-keys",
                        Label = "Manage Keys",
                    },
                ]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send rotation nudge notification for grant {GrantId}", grant.Id);
        }
    }

}
