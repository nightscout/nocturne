using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Service for managing follower invite links.
/// </summary>
public class FollowerInviteService : IFollowerInviteService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IJwtService _jwtService;
    private readonly IOAuthGrantService _grantService;
    private readonly ILogger<FollowerInviteService> _logger;
    private readonly OidcOptions _oidcOptions;

    /// <summary>
    /// Default invite expiration: 7 days
    /// </summary>
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromDays(7);

    /// <summary>
    /// Scopes that are allowed for follower grants (read-only access)
    /// </summary>
    private static readonly HashSet<string> AllowedScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        OAuthScopes.EntriesRead,
        OAuthScopes.TreatmentsRead,
        OAuthScopes.DeviceStatusRead,
        OAuthScopes.ProfileRead,
        OAuthScopes.NotificationsRead,
        OAuthScopes.ReportsRead,
        OAuthScopes.IdentityRead,
        OAuthScopes.HealthRead,
    };

    public FollowerInviteService(
        NocturneDbContext dbContext,
        IJwtService jwtService,
        IOAuthGrantService grantService,
        IOptions<OidcOptions> oidcOptions,
        ILogger<FollowerInviteService> logger)
    {
        _dbContext = dbContext;
        _jwtService = jwtService;
        _grantService = grantService;
        _oidcOptions = oidcOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FollowerInviteResult> CreateInviteAsync(
        Guid ownerSubjectId,
        IEnumerable<string> scopes,
        string? label = null,
        TimeSpan? expiresIn = null,
        int? maxUses = null,
        bool limitTo24Hours = false,
        CancellationToken ct = default)
    {
        // Validate scopes - only allow read scopes for followers
        var scopeList = scopes.ToList();
        var invalidScopes = scopeList.Where(s => !AllowedScopes.Contains(s)).ToList();
        if (invalidScopes.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid scopes for follower invite: {string.Join(", ", invalidScopes)}. " +
                "Only read-only scopes are allowed.");
        }

        if (scopeList.Count == 0)
        {
            throw new ArgumentException("At least one scope is required.");
        }

        // Generate token
        var token = _jwtService.GenerateRefreshToken();
        var tokenHash = _jwtService.HashRefreshToken(token);
        var expiration = expiresIn ?? DefaultExpiration;

        var entity = new FollowerInviteEntity
        {
            Id = Guid.CreateVersion7(),
            OwnerSubjectId = ownerSubjectId,
            TokenHash = tokenHash,
            Scopes = scopeList,
            Label = label,
            ExpiresAt = DateTime.UtcNow.Add(expiration),
            MaxUses = maxUses,
            UseCount = 0,
            CreatedAt = DateTime.UtcNow,
            LimitTo24Hours = limitTo24Hours,
        };

        _dbContext.FollowerInvites.Add(entity);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FollowerInviteAudit: {Event} invite_id={InviteId} owner_id={OwnerId} scopes={Scopes} expires_at={ExpiresAt} limit_24h={Limit24Hours}",
            "invite_created", entity.Id, ownerSubjectId, string.Join(" ", scopeList), entity.ExpiresAt, limitTo24Hours);

        // Build invite URL
        var baseUrl = _oidcOptions.BaseUrl?.TrimEnd('/') ?? "";
        var inviteUrl = $"{baseUrl}/invite/{token}";

        return new FollowerInviteResult
        {
            Id = entity.Id,
            Token = token,
            InviteUrl = inviteUrl,
            ExpiresAt = entity.ExpiresAt,
        };
    }

    /// <inheritdoc />
    public async Task<FollowerInviteInfo?> GetInviteByTokenAsync(
        string token,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var tokenHash = _jwtService.HashRefreshToken(token);

        var entity = await _dbContext.FollowerInvites
            .Include(i => i.Owner)
            .Where(i => i.TokenHash == tokenHash)
            .FirstOrDefaultAsync(ct);

        if (entity == null)
            return null;

        return MapToInfo(entity);
    }

    /// <inheritdoc />
    public async Task<AcceptInviteResult> AcceptInviteAsync(
        string token,
        Guid followerSubjectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
            return AcceptInviteResult.Failed("invalid_token", "Invite token is required.");

        var tokenHash = _jwtService.HashRefreshToken(token);

        var entity = await _dbContext.FollowerInvites
            .Include(i => i.Owner)
            .Where(i => i.TokenHash == tokenHash)
            .FirstOrDefaultAsync(ct);

        if (entity == null)
            return AcceptInviteResult.Failed("invalid_token", "Invite not found or has been revoked.");

        if (entity.IsExpired)
            return AcceptInviteResult.Failed("expired", "This invite has expired.");

        if (entity.IsRevoked)
            return AcceptInviteResult.Failed("revoked", "This invite has been revoked.");

        if (entity.IsExhausted)
            return AcceptInviteResult.Failed("exhausted", "This invite has reached its maximum uses.");

        if (entity.OwnerSubjectId == followerSubjectId)
            return AcceptInviteResult.Failed("self_follow", "You cannot follow yourself.");

        // Check if already following
        var existingGrant = await _grantService.GetActiveFollowerGrantAsync(
            entity.OwnerSubjectId,
            followerSubjectId,
            ct);

        if (existingGrant != null)
            return AcceptInviteResult.Failed("already_following", "You are already following this user.");

        // Create the follower grant with invite linkage, passing through the 24h limit setting
        var grant = await _grantService.CreateFollowerGrantAsync(
            entity.OwnerSubjectId,
            followerSubjectId,
            entity.Scopes,
            entity.Label,
            entity.Id,
            limitTo24Hours: entity.LimitTo24Hours,
            ct);

        // Increment use count
        entity.UseCount++;
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FollowerInviteAudit: {Event} invite_id={InviteId} owner_id={OwnerId} follower_id={FollowerId} grant_id={GrantId}",
            "invite_accepted", entity.Id, entity.OwnerSubjectId, followerSubjectId, grant.Id);

        return AcceptInviteResult.Succeeded(grant.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FollowerInviteInfo>> GetInvitesForOwnerAsync(
        Guid ownerSubjectId,
        CancellationToken ct = default)
    {
        var entities = await _dbContext.FollowerInvites
            .Include(i => i.Owner)
            .Include(i => i.CreatedGrants)
                .ThenInclude(g => g.FollowerSubject)
            .Where(i => i.OwnerSubjectId == ownerSubjectId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(MapToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task RevokeInviteAsync(
        Guid inviteId,
        Guid ownerSubjectId,
        CancellationToken ct = default)
    {
        var entity = await _dbContext.FollowerInvites
            .Where(i => i.Id == inviteId && i.OwnerSubjectId == ownerSubjectId)
            .FirstOrDefaultAsync(ct);

        if (entity == null)
            return; // Silent failure for non-existent or unauthorized

        if (entity.RevokedAt.HasValue)
            return; // Already revoked

        entity.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FollowerInviteAudit: {Event} invite_id={InviteId} owner_id={OwnerId}",
            "invite_revoked", inviteId, ownerSubjectId);
    }

    private static FollowerInviteInfo MapToInfo(FollowerInviteEntity entity)
    {
        return new FollowerInviteInfo
        {
            Id = entity.Id,
            OwnerSubjectId = entity.OwnerSubjectId,
            OwnerName = entity.Owner?.Name,
            OwnerEmail = entity.Owner?.Email,
            Scopes = entity.Scopes,
            Label = entity.Label,
            ExpiresAt = entity.ExpiresAt,
            MaxUses = entity.MaxUses,
            UseCount = entity.UseCount,
            CreatedAt = entity.CreatedAt,
            IsValid = entity.IsValid,
            IsExpired = entity.IsExpired,
            IsRevoked = entity.IsRevoked,
            LimitTo24Hours = entity.LimitTo24Hours,
            UsedBy = entity.CreatedGrants
                .Where(g => g.RevokedAt == null)
                .Select(g => new InviteUsage
                {
                    FollowerSubjectId = g.FollowerSubjectId ?? Guid.Empty,
                    FollowerName = g.FollowerSubject?.Name,
                    FollowerEmail = g.FollowerSubject?.Email,
                    UsedAt = g.CreatedAt,
                })
                .ToList(),
        };
    }
}
