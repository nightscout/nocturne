using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages guest access links backed by <see cref="OAuthGrantEntity"/> rows
/// with <c>grant_type = "guest"</c>. Codes are short alphanumeric strings
/// (ABC-DEFG format) hashed with SHA-256 before storage.
/// </summary>
public class GuestLinkService : IGuestLinkService
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(48);
    private const int MaxActiveLinks = 5;
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 7;

    private static readonly HashSet<string> AllowedGuestScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        OAuthScopes.EntriesRead, OAuthScopes.TreatmentsRead, OAuthScopes.DeviceStatusRead,
        OAuthScopes.ProfileRead, OAuthScopes.HeartRateRead, OAuthScopes.StepCountRead,
        OAuthScopes.NotificationsRead, OAuthScopes.ReportsRead, OAuthScopes.IdentityRead,
        OAuthScopes.HealthRead,
    };

    private static readonly List<string> DefaultScopes =
        [OAuthScopes.HealthRead, OAuthScopes.ProfileRead, OAuthScopes.ReportsRead];

    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<GuestLinkService> _logger;

    public GuestLinkService(
        NocturneDbContext dbContext,
        ILogger<GuestLinkService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GuestLinkCreationResult> CreateGuestLinkAsync(
        Guid dataOwnerSubjectId,
        Guid createdBySubjectId,
        string label,
        string baseUrl,
        IEnumerable<string>? scopes = null,
        CancellationToken ct = default)
    {
        var scopeList = (scopes ?? DefaultScopes).ToList();

        foreach (var scope in scopeList)
        {
            if (!AllowedGuestScopes.Contains(scope))
            {
                throw new ArgumentException(
                    $"Scope '{scope}' is not allowed for guest links. Only read scopes are permitted.");
            }
        }

        var activeCount = await GetActiveCountAsync(dataOwnerSubjectId, ct);
        if (activeCount >= MaxActiveLinks)
        {
            throw new InvalidOperationException(
                $"Maximum of {MaxActiveLinks} active guest links reached for this data owner.");
        }

        var code = GenerateCode();
        var hash = HashCode(code);
        var now = DateTime.UtcNow;

        var entity = new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = dataOwnerSubjectId,
            CreatedBySubjectId = createdBySubjectId,
            GrantType = OAuthGrantTypes.Guest,
            Scopes = scopeList,
            Label = label,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = now + LinkLifetime,
        };

        _dbContext.OAuthGrants.Add(entity);
        await _dbContext.SaveChangesAsync(ct);

        var info = MapToInfo(entity);
        var formatted = FormatCode(code);
        var fullUrl = $"{baseUrl.TrimEnd('/')}/guest/{formatted}";

        _logger.LogInformation(
            "Guest link created for data owner {DataOwnerSubjectId} by {CreatedBySubjectId}, expires {ExpiresAt}",
            dataOwnerSubjectId, createdBySubjectId, entity.ExpiresAt);

        return new GuestLinkCreationResult(formatted, fullUrl, info);
    }

    /// <inheritdoc />
    public async Task<GuestLinkActivationResult> ActivateAsync(
        string code,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var normalized = code.Replace("-", "").ToUpperInvariant();
        var hash = HashCode(normalized);
        var now = DateTime.UtcNow;

        var grant = await _dbContext.OAuthGrants
            .FirstOrDefaultAsync(g =>
                g.TokenHash == hash
                && g.GrantType == OAuthGrantTypes.Guest
                && g.RevokedAt == null
                && g.ActivatedAt == null
                && g.ExpiresAt > now, ct);

        if (grant is null)
        {
            return new GuestLinkActivationResult(false, null, "Invalid or expired code");
        }

        grant.ActivatedAt = now;
        grant.ActivatedIp = ipAddress;
        grant.ActivatedUserAgent = userAgent;
        await _dbContext.SaveChangesAsync(ct);

        var session = new GuestSessionInfo(
            grant.Id,
            grant.SubjectId,
            grant.Scopes.AsReadOnly(),
            grant.Label,
            grant.ExpiresAt!.Value);

        _logger.LogInformation("Guest link {GrantId} activated from {IpAddress}", grant.Id, ipAddress);

        return new GuestLinkActivationResult(true, session, null);
    }

    /// <inheritdoc />
    public async Task<GuestSessionInfo?> ValidateSessionAsync(Guid grantId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var grant = await _dbContext.OAuthGrants
            .AsNoTracking()
            .FirstOrDefaultAsync(g =>
                g.Id == grantId
                && g.GrantType == OAuthGrantTypes.Guest
                && g.RevokedAt == null
                && g.ExpiresAt > now
                && g.ActivatedAt != null, ct);

        if (grant is null) return null;

        return new GuestSessionInfo(
            grant.Id,
            grant.SubjectId,
            grant.Scopes.AsReadOnly(),
            grant.Label,
            grant.ExpiresAt!.Value);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GuestLinkInfo>> GetGuestLinksAsync(
        Guid dataOwnerSubjectId,
        CancellationToken ct = default)
    {
        var grants = await _dbContext.OAuthGrants
            .Where(g => g.SubjectId == dataOwnerSubjectId && g.GrantType == OAuthGrantTypes.Guest)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);

        return grants.Select(MapToInfo).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid grantId, Guid requestingSubjectId, CancellationToken ct = default)
    {
        var grant = await _dbContext.OAuthGrants
            .FirstOrDefaultAsync(g =>
                g.Id == grantId
                && g.GrantType == OAuthGrantTypes.Guest
                && g.RevokedAt == null, ct);

        if (grant is null) return false;

        if (grant.SubjectId != requestingSubjectId && grant.CreatedBySubjectId != requestingSubjectId)
        {
            _logger.LogWarning(
                "Subject {RequestingSubjectId} attempted to revoke guest grant {GrantId} owned by {SubjectId}",
                requestingSubjectId, grantId, grant.SubjectId);
            return false;
        }

        grant.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Guest link {GrantId} revoked by {RequestingSubjectId}", grantId, requestingSubjectId);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> GetActiveCountAsync(Guid dataOwnerSubjectId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _dbContext.OAuthGrants
            .CountAsync(g =>
                g.SubjectId == dataOwnerSubjectId
                && g.GrantType == OAuthGrantTypes.Guest
                && g.RevokedAt == null
                && g.ExpiresAt > now, ct);
    }

    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);

        var sb = new StringBuilder(CodeLength);
        for (var i = 0; i < CodeLength; i++)
        {
            sb.Append(CodeAlphabet[bytes[i] % CodeAlphabet.Length]);
        }

        return sb.ToString();
    }

    private static string FormatCode(string code)
    {
        return $"{code[..3]}-{code[3..]}";
    }

    private static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code.ToUpperInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static GuestLinkInfo MapToInfo(OAuthGrantEntity entity)
    {
        var now = DateTime.UtcNow;
        var status = entity.RevokedAt.HasValue
            ? GuestLinkStatus.Revoked
            : entity.ExpiresAt <= now
                ? GuestLinkStatus.Expired
                : entity.ActivatedAt.HasValue
                    ? GuestLinkStatus.Active
                    : GuestLinkStatus.Pending;

        return new GuestLinkInfo
        {
            Id = entity.Id,
            DataOwnerSubjectId = entity.SubjectId,
            CreatedBySubjectId = entity.CreatedBySubjectId ?? entity.SubjectId,
            Label = entity.Label ?? string.Empty,
            Scopes = entity.Scopes,
            CreatedAt = entity.CreatedAt,
            ExpiresAt = entity.ExpiresAt ?? entity.CreatedAt + LinkLifetime,
            ActivatedAt = entity.ActivatedAt,
            ActivatedIp = entity.ActivatedIp,
            RevokedAt = entity.RevokedAt,
            Status = status,
        };
    }
}
