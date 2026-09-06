using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Demo;
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
        // A demo tenant's visitor account is shared: anyone can obtain a session for it without
        // signing up, and GET /api/v4/account/sessions lists every session for the subject —
        // including IpAddress — to any member of it. Recording the caller's address would show
        // each visitor where everyone else currently using the demo connects from, and let them
        // revoke each other.
        //
        // Enforced here rather than at the callers because there are several and they do not
        // agree: the sign-in endpoints pass no address deliberately, but rotation carries the old
        // row's values forward and POST /api/auth/oidc/refresh is [AllowAnonymous], so the web
        // app's first automatic token refresh put the real client address back within one
        // access-token lifetime of any visit. Scrubbing at the single row-creating sink means no
        // path can repopulate them, including one added later.
        var isDemoSubject = await _context.Subjects
            .AsNoTracking()
            .Where(s => s.Id == record.SubjectId)
            .Select(s => (bool?)s.IsDemoSubject)
            .FirstOrDefaultAsync(ct);

        var scrub = isDemoSubject is true;

        if (scrub)
            await TrimDemoSessionsAsync(record.SubjectId, ct);

        var entity = new RefreshTokenEntity
        {
            Id = record.Id,
            TokenHash = record.TokenHash,
            SubjectId = record.SubjectId,
            OidcSessionId = record.OidcSessionId,
            DeviceDescription = record.DeviceDescription,
            IpAddress = scrub ? null : record.IpAddress,
            UserAgent = scrub ? null : record.UserAgent,
            IssuedAt = record.IssuedAt,
            ExpiresAt = record.ExpiresAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.RefreshTokens.Add(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Drops the demo subject's dead and surplus session rows, leaving room for the caller to add
    /// one without exceeding <see cref="DemoSessionLimits.MaxLiveSessions"/>.
    /// </summary>
    /// <remarks>
    /// Here rather than only on the sign-in path because the sign-in path is not the only one that
    /// writes a row. <c>POST /api/auth/oidc/refresh</c> is anonymous, carries no rate limit, and
    /// rotates a row per call — so a visitor who signs in once and then loops refresh never touches
    /// the sign-in trim again, and the table grows without bound on an account anyone can obtain
    /// without signing up. Deletes rather than revokes: a revoked row still occupies the table.
    /// <para>
    /// Expired and revoked rows go first, so a live session is only displaced once there is nothing
    /// dead left to clear. Concurrent callers can each observe a count under the cap and each
    /// insert, so the true ceiling is the cap plus the number of simultaneous writers — bounded,
    /// which is the property being bought here.
    /// </para>
    /// </remarks>
    private async Task TrimDemoSessionsAsync(Guid subjectId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Ordered newest-first with a tiebreaker, so "which rows are surplus" does not depend on
        // how the provider breaks ties between rows issued in the same instant — which visitors
        // arriving together routinely are.
        var rows = await _context.RefreshTokens
            .Where(t => t.SubjectId == subjectId)
            .OrderByDescending(t => t.IssuedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new { t.Id, IsDead = t.RevokedAt != null || t.ExpiresAt <= now })
            .ToListAsync(ct);

        var doomed = rows.Where(r => r.IsDead).Select(r => r.Id).ToList();

        // Only what is still live counts against the cap, and one slot is left for the row the
        // caller is about to add.
        doomed.AddRange(rows
            .Where(r => !r.IsDead)
            .Skip(DemoSessionLimits.MaxLiveSessions - 1)
            .Select(r => r.Id));

        if (doomed.Count == 0)
            return;

        // Loaded and removed through the change tracker rather than with ExecuteDelete: this runs
        // inside the same SaveChanges as the insert, so the trim and the new row commit together,
        // and it does not depend on a provider that implements bulk delete. The set it loads is
        // bounded by the cap.
        var entities = await _context.RefreshTokens
            .Where(t => doomed.Contains(t.Id))
            .ToListAsync(ct);

        _context.RefreshTokens.RemoveRange(entities);
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
    public async Task<bool> TryMarkRotatedAsync(
        Guid tokenId,
        Guid replacedByTokenId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Single atomic UPDATE ... WHERE id = @id AND revoked_at IS NULL. The database admits
        // exactly one concurrent caller while the row is still active, so parallel refresh
        // requests cannot each mint a successor and fork the token chain.
        var affected = await _context.RefreshTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.RevokedReason, "Rotated")
                    .SetProperty(t => t.ReplacedByTokenId, (Guid?)replacedByTokenId)
                    // Rotation is the token being exchanged — stamp last use here since the
                    // rotation path never reaches UpdateLastUsedAsync.
                    .SetProperty(t => t.LastUsedAt, now)
                    .SetProperty(t => t.UpdatedAt, now),
                ct);

        return affected == 1;
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
    public async Task<int> RevokeSessionForSubjectAsync(
        Guid subjectId,
        string sessionId,
        string reason,
        CancellationToken ct = default)
    {
        Guid.TryParse(sessionId, out var legacyTokenId);

        var tokens = await _context.RefreshTokens
            .Where(t => t.SubjectId == subjectId
                && t.RevokedAt == null
                && (t.OidcSessionId == sessionId
                    || (t.OidcSessionId == null && t.Id == legacyTokenId)))
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
    public async Task<int> RevokeOtherSessionsForSubjectAsync(
        Guid subjectId,
        string currentSessionId,
        string reason,
        CancellationToken ct = default)
    {
        Guid.TryParse(currentSessionId, out var legacyTokenId);

        var tokens = await _context.RefreshTokens
            .Where(t => t.SubjectId == subjectId && t.RevokedAt == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var count = 0;
        foreach (var token in tokens)
        {
            var isCurrent = token.OidcSessionId == currentSessionId
                || (token.OidcSessionId == null && token.Id == legacyTokenId);
            if (isCurrent)
                continue;

            token.RevokedAt = now;
            token.RevokedReason = reason;
            token.UpdatedAt = now;
            count++;
        }

        await _context.SaveChangesAsync(ct);

        return count;
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
                OidcSessionId = t.OidcSessionId,
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
