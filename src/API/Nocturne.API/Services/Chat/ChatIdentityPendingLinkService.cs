using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Chat;

/// <summary>
/// Manages short-lived state tokens for the chat identity link flow
/// (/connect slash command and OAuth2 finalize hop).
/// </summary>
public sealed class ChatIdentityPendingLinkService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    ILogger<ChatIdentityPendingLinkService> logger)
{
    /// <summary>
    /// Token lifetime. 10 minutes from creation.
    /// </summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Creates a new pending link row with a cryptographically random 64-char hex token.
    /// </summary>
    /// <param name="platform">The chat platform (e.g. "discord").</param>
    /// <param name="platformUserId">The chat platform's user id.</param>
    /// <param name="tenantSlug">Optional tenant slug hint; null means any tenant.</param>
    /// <param name="source">Either "connect-slash" or "oauth2-finalize".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> CreateAsync(
        string platform,
        string platformUserId,
        string? tenantSlug,
        string source,
        CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var entity = new ChatIdentityPendingLinkEntity
        {
            Token = token,
            Platform = platform,
            PlatformUserId = platformUserId,
            TenantSlug = tenantSlug,
            Source = source,
            CreatedAt = now,
            ExpiresAt = now.Add(TokenLifetime),
        };

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.ChatIdentityPendingLinks.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created pending link token for {Platform}:{PlatformUserId} source={Source}",
            platform,
            platformUserId,
            source);

        return token;
    }

    /// <summary>
    /// Atomically looks up the row by token, validates it hasn't expired, deletes it,
    /// and returns it. Subsequent calls with the same token return null.
    /// Returns null if the token doesn't exist OR has expired.
    /// </summary>
    public async Task<ChatIdentityPendingLinkEntity?> TryConsumeAsync(string token, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // NpgsqlRetryingExecutionStrategy requires user-initiated transactions
        // to be wrapped in strategy.ExecuteAsync so the entire block can be
        // retried as a unit on transient failures.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var row = await db.ChatIdentityPendingLinks
                .FirstOrDefaultAsync(p => p.Token == token, ct);

            if (row is null || row.ExpiresAt < DateTime.UtcNow)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            db.ChatIdentityPendingLinks.Remove(row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "Consumed pending link token for {Platform}:{PlatformUserId} source={Source}",
                row.Platform,
                row.PlatformUserId,
                row.Source);

            return row;
        });
    }

    // TODO(v1.1): wire CleanupExpiredAsync into a scheduled IHostedService.
    /// <summary>
    /// Deletes all expired rows. Call periodically from a background sweep
    /// (cleanup wiring is deferred to v1.1 per plan).
    /// </summary>
    public async Task<int> CleanupExpiredAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var deleted = await db.ChatIdentityPendingLinks
            .Where(p => p.ExpiresAt < DateTime.UtcNow)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            logger.LogInformation("Cleaned up {Count} expired pending link tokens", deleted);
        }

        return deleted;
    }
}
