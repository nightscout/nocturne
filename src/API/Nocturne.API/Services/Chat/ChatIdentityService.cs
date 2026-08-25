using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Chat;

/// <summary>
/// Tenant-scoped facade over <see cref="ChatIdentityDirectoryService"/> and
/// <see cref="ChatIdentityPendingLinkService"/>. Handles pending-token claim flows,
/// direct link creation, and read-only pending link lookups.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClaimPendingLinkAsync"/> validates that the consumed token belongs to the
/// requested tenant: if the token's <c>TenantSlug</c> hint is set it must match the tenant's
/// slug (case-insensitive), otherwise any tenant may claim it. This check prevents a token
/// issued in a Discord DM targeted at tenant A from being redeemed against tenant B.
/// </para>
/// <para>
/// <see cref="CreateDirectLinkAsync"/> bypasses the pending-token flow entirely and is
/// intended for administrative link creation (e.g. from the management UI).
/// </para>
/// <para>
/// <see cref="GetPendingAsync"/> is a read-only lookup safe for the OAuth2 authorise page;
/// it does NOT consume the token.
/// </para>
/// </remarks>
/// <seealso cref="ChatIdentityDirectoryService"/>
/// <seealso cref="ChatIdentityPendingLinkService"/>
public sealed class ChatIdentityService(
    ChatIdentityDirectoryService directory,
    ChatIdentityPendingLinkService pendingLinks,
    IDbContextFactory<NocturneDbContext> contextFactory,
    ILogger<ChatIdentityService> logger)
{
    /// <summary>Returns all active chat identity links for the specified tenant.</summary>
    public Task<IReadOnlyList<ChatIdentityDirectoryEntry>> GetByTenantAsync(
        Guid tenantId, CancellationToken ct)
        => directory.GetByTenantAsync(tenantId, ct);

    /// <inheritdoc cref="ChatIdentityDirectoryService.GetDefaultAsync"/>
    public Task<ChatIdentityDirectoryEntry?> GetDefaultAsync(
        string platform, string platformUserId, CancellationToken ct)
        => directory.GetDefaultAsync(platform, platformUserId, ct);

    /// <summary>Consumes a pending link token and creates a directory entry linking the chat platform user to the tenant.</summary>
    public async Task<ChatIdentityDirectoryEntry> ClaimPendingLinkAsync(
        Guid tenantId, Guid userId, string token, CancellationToken ct)
    {
        var pending = await pendingLinks.TryConsumeAsync(token, ct)
            ?? throw new InvalidOperationException("Token expired or already used");

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Slug, t.DisplayName })
            .FirstAsync(ct);

        if (pending.TenantSlug is not null &&
            !string.Equals(pending.TenantSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Token does not belong to this tenant");
        }

        var entry = await directory.CreateLinkAsync(
            pending.Platform,
            pending.PlatformUserId,
            tenantId,
            userId,
            suggestedLabel: tenant.Slug,
            suggestedDisplayName: tenant.DisplayName,
            ct);

        logger.LogInformation(
            "Claimed pending link token -> tenant {TenantId}, label {Label}",
            tenantId, entry.Label);

        return entry;
    }

    /// <summary>Creates a chat identity link directly, bypassing the pending-token flow.</summary>
    public async Task<ChatIdentityDirectoryEntry> CreateDirectLinkAsync(
        Guid tenantId, Guid userId, string platform, string platformUserId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Slug, t.DisplayName })
            .FirstAsync(ct);

        return await directory.CreateLinkAsync(
            platform, platformUserId, tenantId, userId, tenant.Slug, tenant.DisplayName, ct);
    }

    /// <summary>Designates a chat identity link as the default for the platform user.</summary>
    public Task SetDefaultAsync(Guid tenantId, Guid linkId, CancellationToken ct)
        => directory.SetDefaultAsync(linkId, RequireTenant(tenantId), ct);

    /// <summary>Renames the label on a chat identity link.</summary>
    public Task RenameLabelAsync(Guid tenantId, Guid linkId, string newLabel, CancellationToken ct)
        => directory.RenameLabelAsync(linkId, RequireTenant(tenantId), newLabel, ct);

    /// <summary>Updates the display name on a chat identity link.</summary>
    public Task UpdateDisplayNameAsync(Guid tenantId, Guid linkId, string newDisplayName, CancellationToken ct)
        => directory.UpdateDisplayNameAsync(linkId, RequireTenant(tenantId), newDisplayName, ct);

    /// <summary>Permanently removes a chat identity link.</summary>
    /// <exception cref="KeyNotFoundException">The link does not belong to <paramref name="tenantId"/>.</exception>
    public async Task RevokeAsync(Guid tenantId, Guid linkId, CancellationToken ct)
    {
        var deleted = await directory.RevokeAsync(linkId, RequireTenant(tenantId), ct);
        if (deleted == 0)
            throw new KeyNotFoundException($"Chat identity directory link {linkId} not found");
    }

    /// <summary>
    /// Returns the tenant every mutation on this facade is scoped to. An unresolved tenant is
    /// rejected rather than passed through as an unscoped (cross-tenant) operation.
    /// </summary>
    private static Guid RequireTenant(Guid tenantId)
        => tenantId != Guid.Empty
            ? tenantId
            : throw new InvalidOperationException(
                "Chat identity link management requires a resolved tenant.");

    /// <summary>
    /// Read-only lookup for the authorize page. Does NOT consume the token.
    /// </summary>
    public async Task<ChatIdentityPendingLinkView?> GetPendingAsync(string token, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.ChatIdentityPendingLinks.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Token == token, ct);
        if (row is null || row.ExpiresAt < DateTime.UtcNow) return null;
        return new ChatIdentityPendingLinkView
        {
            Platform = row.Platform,
            PlatformUserId = row.PlatformUserId,
            TenantSlug = row.TenantSlug,
            Source = row.Source,
        };
    }
}

/// <summary>
/// Read-only projection of a <c>ChatIdentityPendingLinkEntity</c> row returned by
/// <see cref="ChatIdentityService.GetPendingAsync"/> for display on the OAuth2 authorise page.
/// Does not expose the raw token value.
/// </summary>
public class ChatIdentityPendingLinkView
{
    public string Platform { get; set; } = string.Empty;
    public string PlatformUserId { get; set; } = string.Empty;
    public string? TenantSlug { get; set; }
    public string Source { get; set; } = string.Empty;
}
