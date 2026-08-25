using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Chat;

/// <summary>
/// CRUD and routing service for the global chat identity directory. Handles
/// multi-link scenarios for a single platform user (one link per tenant),
/// label collision auto-suffixing, and transactional default-flag swaps.
/// </summary>
/// <remarks>
/// <para>
/// A single platform user (identified by platform + platformUserId) may be linked to multiple
/// Nocturne tenants. Each link has a human-readable label that must be unique within the
/// platform user's set of links. When the suggested label collides an integer suffix is
/// appended, <c>-2</c> through <c>-999</c> (e.g. <c>home-2</c>); an exception is thrown once
/// every one of those is taken. The suffix is chosen from a read that is not serialized with
/// the insert, so <see cref="CreateLinkAsync"/> also retries on the unique-index rejection a
/// concurrent create can produce.
/// </para>
/// <para>
/// The first link created for a platform user is automatically marked as default
/// (<c>IsDefault = true</c>). Subsequent calls to <see cref="SetDefaultAsync"/> clear the
/// previous default and set the new one atomically inside a user-initiated transaction wrapped
/// by the Npgsql retry execution strategy, enabling safe replay on transient failures.
/// <see cref="RevokeAsync"/> closes the same loop: a platform user left holding a single link has
/// no choice to make, so that survivor becomes the default — the same unambiguous call the
/// first-link rule makes, and what stops deleting the default from leaving none. Two or more
/// survivors is a real choice between tenants, which the bot asks the user to make rather than
/// guessing for them. That promotion is unscoped because one link per tenant per platform user
/// puts every survivor in a different tenant from the revoked link.
/// </para>
/// <para>
/// Each method opens its own <see cref="Microsoft.EntityFrameworkCore.DbContext"/> from the
/// factory to stay compatible with the transactional retry pattern and avoid concurrency issues
/// with shared contexts.
/// </para>
/// </remarks>
/// <seealso cref="ChatIdentityService"/>
/// <seealso cref="ChatIdentityPendingLinkService"/>
public sealed class ChatIdentityDirectoryService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    ILogger<ChatIdentityDirectoryService> logger)
{
    /// <summary>
    /// Insert attempts <see cref="CreateLinkAsync"/> makes before letting the rejection reach the
    /// caller. Only a unique-index rejection is retried at all, so this bounds repeatedly losing a
    /// real race rather than replaying a failure a re-read cannot fix.
    /// </summary>
    private const int MaxCreateAttempts = 5;

    /// <summary>Returns all active directory entries for a platform user, ordered by creation date.</summary>
    public async Task<IReadOnlyList<ChatIdentityDirectoryEntry>> GetCandidatesAsync(
        string platform, string platformUserId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ChatIdentityDirectory
            .Where(d => d.Platform == platform
                        && d.PlatformUserId == platformUserId
                        && d.IsActive)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the active entry a bare bot invocation resolves to for a platform user, or
    /// <c>null</c> when that platform user has no default. The entry may belong to any tenant.
    /// </summary>
    public async Task<ChatIdentityDirectoryEntry?> GetDefaultAsync(
        string platform, string platformUserId, CancellationToken ct)
        => (await GetCandidatesAsync(platform, platformUserId, ct))
            .FirstOrDefault(d => d.IsDefault);

    /// <summary>Returns all active directory entries for a tenant.</summary>
    public async Task<IReadOnlyList<ChatIdentityDirectoryEntry>> GetByTenantAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ChatIdentityDirectory
            .Where(d => d.TenantId == tenantId && d.IsActive)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns a directory entry by its ID, or null if not found.
    /// </summary>
    /// <param name="id">The directory entry ID.</param>
    /// <param name="tenantScope">
    /// Tenant the entry must belong to, or <c>null</c> for a deliberately cross-tenant lookup
    /// (the instance-key directory endpoints the chat bots call from the apex host).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ChatIdentityDirectoryEntry?> GetByIdAsync(Guid id, Guid? tenantScope, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ChatIdentityDirectory
            .Where(ScopedToId(id, tenantScope))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Creates a directory link between a chat platform user and a tenant, auto-suffixing the label
    /// if it collides. Returns the existing entry when one already links this platform user to this
    /// tenant — the pre-check ignores <c>IsActive</c> to match the unique index, which does too.
    /// </summary>
    public async Task<ChatIdentityDirectoryEntry> CreateLinkAsync(
        string platform, string platformUserId, Guid tenantId, Guid nocturneUserId,
        string suggestedLabel, string suggestedDisplayName, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);

            var existing = await db.ChatIdentityDirectory
                .FirstOrDefaultAsync(d => d.Platform == platform
                                          && d.PlatformUserId == platformUserId
                                          && d.TenantId == tenantId, ct);
            if (existing is not null)
            {
                return existing;
            }

            var existingLabels = await db.ChatIdentityDirectory
                .Where(d => d.Platform == platform && d.PlatformUserId == platformUserId)
                .Select(d => d.Label)
                .ToListAsync(ct);

            var resolvedLabel = ResolveUniqueLabel(existingLabels, suggestedLabel);
            var isFirst = existingLabels.Count == 0;

            var entry = new ChatIdentityDirectoryEntry
            {
                Platform = platform,
                PlatformUserId = platformUserId,
                TenantId = tenantId,
                NocturneUserId = nocturneUserId,
                Label = resolvedLabel,
                DisplayName = suggestedDisplayName,
                IsDefault = isFirst,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };

            db.ChatIdentityDirectory.Add(entry);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (attempt < MaxCreateAttempts && ex.IsUniqueViolation())
            {
                // The reads above are not serialized with the insert, so a concurrent create for the
                // same platform user can take the label (ux_directory_user_label), the tenant slot
                // (ux_directory_user_tenant) or the default flag (ux_directory_user_one_default) in
                // between. Every one of those resolves the same way: re-read on a fresh context and
                // try again — a taken label moves to the next suffix, a taken tenant slot returns
                // the winner's row, a taken default flag leaves IsDefault false. Anything else the
                // database rejects is deterministic, and the filter above lets it straight out.
                logger.LogWarning(ex,
                    "Chat identity directory insert for {Platform}:{PlatformUserId} -> tenant {TenantId} was rejected on attempt {Attempt}; retrying with a fresh read",
                    platform, platformUserId, tenantId, attempt);
                continue;
            }

            logger.LogInformation(
                "Created chat identity directory link {LinkId} for {Platform}:{PlatformUserId} -> tenant {TenantId} with label '{Label}' (default={IsDefault})",
                entry.Id, platform, platformUserId, tenantId, resolvedLabel, isFirst);

            return entry;
        }
    }

    /// <summary>Designates a link as the default for the platform user, clearing the previous default in a transaction.</summary>
    /// <param name="linkId">The directory entry to make default.</param>
    /// <param name="tenantScope">Tenant the entry must belong to, or <c>null</c> for a cross-tenant caller.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetDefaultAsync(Guid linkId, Guid? tenantScope, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var target = await db.ChatIdentityDirectory.Where(ScopedToId(linkId, tenantScope)).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Chat identity directory link {linkId} not found");

        // NpgsqlRetryingExecutionStrategy requires user-initiated transactions
        // to be wrapped in strategy.ExecuteAsync so the entire block can be
        // retried as a unit on transient failures.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Cleared across tenants on purpose: ux_directory_user_one_default permits at most
            // one default row per (platform, platform_user_id) for the whole table, because the
            // default is which tenant a bare bot command resolves to for that chat account.
            await db.ChatIdentityDirectory
                .Where(d => d.Platform == target.Platform
                            && d.PlatformUserId == target.PlatformUserId
                            && d.Id != target.Id
                            && d.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsDefault, false), ct);

            target.IsDefault = true;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        logger.LogInformation(
            "Set chat identity directory link {LinkId} as default for {Platform}:{PlatformUserId}",
            linkId, target.Platform, target.PlatformUserId);
    }

    /// <summary>Renames a link's label, throwing if the new label collides with an existing one.</summary>
    /// <param name="linkId">The directory entry to rename.</param>
    /// <param name="tenantScope">Tenant the entry must belong to, or <c>null</c> for a cross-tenant caller.</param>
    /// <param name="newLabel">The new routing label.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RenameLabelAsync(Guid linkId, Guid? tenantScope, string newLabel, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var target = await db.ChatIdentityDirectory.Where(ScopedToId(linkId, tenantScope)).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Chat identity directory link {linkId} not found");

        target.Label = newLabel;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex,
                "Label rename collision: link {LinkId} could not be renamed to '{NewLabel}'",
                linkId, newLabel);
            throw new InvalidOperationException(
                $"Label '{newLabel}' is already in use", ex);
        }
    }

    /// <summary>Updates the display name shown to the chat platform user for a link.</summary>
    /// <param name="linkId">The directory entry to update.</param>
    /// <param name="tenantScope">Tenant the entry must belong to, or <c>null</c> for a cross-tenant caller.</param>
    /// <param name="newDisplayName">The new display name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateDisplayNameAsync(Guid linkId, Guid? tenantScope, string newDisplayName, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var target = await db.ChatIdentityDirectory.Where(ScopedToId(linkId, tenantScope)).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Chat identity directory link {linkId} not found");

        target.DisplayName = newDisplayName;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Permanently deletes a directory link, promoting the platform user's sole surviving link to
    /// default.
    /// </summary>
    /// <param name="linkId">The directory entry to delete.</param>
    /// <param name="tenantScope">Tenant the entry must belong to, or <c>null</c> for a cross-tenant caller.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows deleted: 0 when the entry does not exist in that scope.</returns>
    public async Task<int> RevokeAsync(Guid linkId, Guid? tenantScope, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var target = await db.ChatIdentityDirectory.AsNoTracking()
            .Where(ScopedToId(linkId, tenantScope))
            .FirstOrDefaultAsync(ct);
        if (target is null)
        {
            return 0;
        }

        var deleted = await db.ChatIdentityDirectory
            .Where(ScopedToId(linkId, tenantScope))
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return deleted;
        }

        var survivors = await GetCandidatesAsync(target.Platform, target.PlatformUserId, ct);
        if (survivors is [{ IsDefault: false } survivor])
        {
            await SetDefaultAsync(survivor.Id, tenantScope: null, ct);
        }

        return deleted;
    }

    /// <summary>
    /// Predicate matching one directory entry by ID, additionally constrained to a tenant when
    /// <paramref name="tenantScope"/> is supplied. <c>chat_identity_directory</c> is a global
    /// table with no query filter and no RLS policy — the bots resolve it cross-tenant from the
    /// apex host — so an ID on its own is never enough for a tenant-authenticated caller.
    /// </summary>
    private static Expression<Func<ChatIdentityDirectoryEntry, bool>> ScopedToId(Guid id, Guid? tenantScope)
        => tenantScope is { } tenantId
            ? d => d.Id == id && d.TenantId == tenantId
            : d => d.Id == id;

    private static string ResolveUniqueLabel(
        IReadOnlyCollection<string> existingLabels, string suggested)
    {
        if (!existingLabels.Contains(suggested))
        {
            return suggested;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{suggested}-{i}";
            if (!existingLabels.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not resolve a unique label: '{suggested}' and every suffix from -2 to -999 are taken");
    }
}
