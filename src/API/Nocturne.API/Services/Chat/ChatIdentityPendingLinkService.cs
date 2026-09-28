using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Chat;

/// <summary>
/// Manages short-lived state tokens for the two-step chat identity link flow:
/// the <c>/connect</c> slash command and the OAuth2 finalise hop.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are 32 random bytes encoded as 64-character uppercase hex strings. Each token row
/// is deleted on first successful consumption (<see cref="TryConsumeAsync"/>), making the
/// token single-use. A token abandoned before consumption leaves its row behind;
/// <see cref="ChatIdentityPendingLinkCleanupService"/> sweeps those via
/// <see cref="CleanupExpiredAsync"/>.
/// </para>
/// <para>
/// <see cref="TryConsumeAsync"/> wraps the read-delete-commit sequence in a user-initiated
/// transaction via the Npgsql retry execution strategy so the entire block can be safely
/// retried on transient failures without double-consuming a token.
/// </para>
/// </remarks>
/// <seealso cref="ChatIdentityDirectoryService"/>
/// <seealso cref="ChatIdentityService"/>
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

        var consumed = await db.ExecuteInTransactionAsync<ChatIdentityPendingLinkEntity?>(async attemptCt =>
        {
            var row = await db.ChatIdentityPendingLinks
                .FirstOrDefaultAsync(p => p.Token == token, attemptCt);

            if (row is null || row.ExpiresAt < DateTime.UtcNow)
                return null;

            db.ChatIdentityPendingLinks.Remove(row);
            await db.SaveChangesAsync(attemptCt);
            return row;
        }, ct: ct);

        if (consumed is not null)
        {
            logger.LogInformation(
                "Consumed pending link token for {Platform}:{PlatformUserId} source={Source}",
                consumed.Platform,
                consumed.PlatformUserId,
                consumed.Source);
        }

        return consumed;
    }

    /// <summary>
    /// Deletes all expired rows. Swept periodically by
    /// <see cref="ChatIdentityPendingLinkCleanupService"/>.
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
