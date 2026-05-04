using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFirstPartyTokenRepository"/> for first-party refresh tokens.
/// </summary>
public class EfFirstPartyTokenRepository : IFirstPartyTokenRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfFirstPartyTokenRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    public EfFirstPartyTokenRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task CreateAsync(RefreshTokenRecord record, CancellationToken ct = default)
    {
        var entity = new RefreshTokenEntity
        {
            Id = record.Id,
            TokenHash = record.TokenHash,
            SubjectId = record.SubjectId,
            OidcSessionId = record.OidcSessionId,
            DeviceDescription = record.DeviceDescription,
            IpAddress = record.IpAddress,
            UserAgent = record.UserAgent,
            IssuedAt = record.IssuedAt,
            ExpiresAt = record.ExpiresAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.RefreshTokens.Add(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var entity = await _context.RefreshTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == tokenHash)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return null;

        return ToRecord(entity);
    }

    /// <inheritdoc />
    public async Task RevokeAsync(
        Guid tokenId,
        string reason,
        Guid? replacedByTokenId = null,
        CancellationToken ct = default)
    {
        var entity = await _context.RefreshTokens
            .Where(t => t.Id == tokenId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return;

        entity.RevokedAt = DateTime.UtcNow;
        entity.RevokedReason = reason;
        entity.ReplacedByTokenId = replacedByTokenId;
        entity.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllForSubjectAsync(
        Guid subjectId,
        string reason,
        CancellationToken ct = default)
    {
        var tokens = await _context.RefreshTokens
            .Where(t => t.SubjectId == subjectId && t.RevokedAt == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
            token.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(ct);

        return tokens.Count;
    }

    /// <inheritdoc />
    public async Task<int> RevokeByOidcSessionAsync(
        string oidcSessionId,
        string reason,
        CancellationToken ct = default)
    {
        var tokens = await _context.RefreshTokens
            .Where(t => t.OidcSessionId == oidcSessionId && t.RevokedAt == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
            token.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(ct);

        return tokens.Count;
    }

    /// <inheritdoc />
    public async Task UpdateLastUsedAsync(string tokenHash, CancellationToken ct = default)
    {
        await _context.RefreshTokens
            .Where(t => t.TokenHash == tokenHash)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.LastUsedAt, DateTime.UtcNow)
                    .SetProperty(t => t.UpdatedAt, DateTime.UtcNow),
                ct);
    }

    /// <inheritdoc />
    public async Task<int> PruneExpiredAsync(DateTime cutoff, CancellationToken ct = default)
    {
        return await _context.RefreshTokens
            .Where(t => t.ExpiresAt < cutoff || (t.RevokedAt != null && t.RevokedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    public async Task<List<RefreshTokenInfo>> GetActiveSessionsAsync(
        Guid subjectId,
        CancellationToken ct = default)
    {
        return await _context.RefreshTokens
            .AsNoTracking()
            .Where(t => t.SubjectId == subjectId && t.RevokedAt == null && t.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(t => t.LastUsedAt ?? t.IssuedAt)
            .Select(t => new RefreshTokenInfo
            {
                Id = t.Id,
                DeviceDescription = t.DeviceDescription,
                IpAddress = t.IpAddress,
                IssuedAt = t.IssuedAt,
                LastUsedAt = t.LastUsedAt,
                ExpiresAt = t.ExpiresAt,
                IsCurrent = false
            })
            .ToListAsync(ct);
    }

    private static RefreshTokenRecord ToRecord(RefreshTokenEntity entity) =>
        new(
            Id: entity.Id,
            TokenHash: entity.TokenHash,
            SubjectId: entity.SubjectId,
            OidcSessionId: entity.OidcSessionId,
            DeviceDescription: entity.DeviceDescription,
            IpAddress: entity.IpAddress,
            UserAgent: entity.UserAgent,
            IssuedAt: entity.IssuedAt,
            ExpiresAt: entity.ExpiresAt,
            RevokedAt: entity.RevokedAt,
            RevokedReason: entity.RevokedReason,
            ReplacedByTokenId: entity.ReplacedByTokenId,
            LastUsedAt: entity.LastUsedAt);
}
