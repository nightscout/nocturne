using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

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

    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory;
    private readonly ILogger<ApiKeyHandler> _logger;

    public ApiKeyHandler(
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        ILogger<ApiKeyHandler> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        // 1. Extract value from api-secret header or ?secret= query param
        var apiKey = context.Request.Headers["api-secret"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = context.Request.Query["secret"].FirstOrDefault();
        }

        // 2. If null/empty -> Skip
        if (string.IsNullOrEmpty(apiKey))
            return AuthResult.Skip();

        // 3. Resolve tenant context
        if (context.Items["TenantContext"] is not TenantContext tenantCtx)
        {
            _logger.LogWarning("API key provided but no tenant context resolved");
            return AuthResult.Failure("API key requires a resolved tenant");
        }

        // 4. Determine hash strategy based on prefix
        string? tokenHash = null;
        string? legacySecretHash = null;

        if (apiKey.StartsWith("noc_", StringComparison.Ordinal))
        {
            tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(apiKey);
        }
        else
        {
            // Legacy path: the value sent in the header is already a SHA-1 hex hash
            // (Nightscout clients pre-hash secrets before sending)
            legacySecretHash = apiKey;
        }

        // 5. Query for matching grant
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        OAuthGrantEntity? grant;

        if (tokenHash != null)
        {
            grant = await dbContext.OAuthGrants
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(g => g.TokenHash == tokenHash
                         && g.TenantId == tenantCtx.TenantId
                         && g.GrantType == OAuthGrantTypes.Direct
                         && g.RevokedAt == null)
                .FirstOrDefaultAsync();
        }
        else
        {
            grant = await dbContext.OAuthGrants
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(g => g.LegacySecretHash == legacySecretHash
                         && g.TenantId == tenantCtx.TenantId
                         && g.GrantType == OAuthGrantTypes.Direct
                         && g.RevokedAt == null)
                .FirstOrDefaultAsync();
        }

        if (grant == null)
        {
            _logger.LogDebug("No matching API key grant found");
            return AuthResult.Failure("Invalid API key");
        }

        // 6. Fire-and-forget UpdateLastUsedAsync
        var ipAddress = context.Connection.RemoteIpAddress?.ToString();
        var userAgent = context.Request.Headers.UserAgent.FirstOrDefault();
        _ = UpdateLastUsedAsync(grant.Id, ipAddress, userAgent);

        // 7. If this is a legacy grant's first use, nudge rotation
        if (grant.LegacySecretHash != null && grant.LastUsedAt == null)
        {
            var scopeFactory = context.RequestServices?.GetService<IServiceScopeFactory>();
            if (scopeFactory != null)
            {
                _ = SendRotationNudgeAsync(scopeFactory, grant);
            }
        }

        // Store hash prefix for read-access audit logging (distinguishes API key readers)
        var hashSource = grant.TokenHash ?? grant.LegacySecretHash;
        if (hashSource is { Length: > 0 })
        {
            context.Items["ApiSecretHashPrefix"] = hashSource.Length >= 8
                ? hashSource[..8]
                : hashSource;
        }

        _logger.LogDebug("API key authentication successful for grant {GrantId}, subject {SubjectId}",
            grant.Id, grant.SubjectId);

        return AuthResult.Success(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectId = grant.SubjectId,
            Scopes = grant.Scopes,
            TokenId = grant.Id,
        });
    }

    private async Task UpdateLastUsedAsync(Guid grantId, string? ipAddress, string? userAgent)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            await dbContext.OAuthGrants
                .Where(g => g.Id == grantId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.LastUsedAt, DateTime.UtcNow)
                    .SetProperty(g => g.LastUsedIp, ipAddress)
                    .SetProperty(g => g.LastUsedUserAgent, userAgent));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update last used metadata for grant {GrantId}", grantId);
        }
    }

    private async Task SendRotationNudgeAsync(IServiceScopeFactory scopeFactory, OAuthGrantEntity grant)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var notificationService = scope.ServiceProvider.GetRequiredService<IInAppNotificationService>();

            await notificationService.CreateNotificationAsync(
                userId: grant.SubjectId.ToString(),
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
