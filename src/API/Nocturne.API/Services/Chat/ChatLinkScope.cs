namespace Nocturne.API.Services.Chat;

/// <summary>
/// Which rows a by-id operation on <see cref="ChatIdentityDirectoryService"/> may reach.
/// </summary>
/// <remarks>
/// <para>
/// <c>chat_identity_directory</c> is a global table with no query filter and no RLS policy, so an
/// id on its own reaches every tenant. <see cref="Unscoped"/> is for the instance-key chat bots,
/// which resolve and revoke by chat account from the apex host and carry no tenant.
/// </para>
/// <para>
/// <see cref="ForOwner"/> is for a tenant-authenticated caller. Tenant membership alone is not
/// ownership of a chat identity: a co-member's row routes a chat account the caller does not
/// control, and both <see cref="ChatIdentityDirectoryService.SetDefaultAsync"/> and the successor
/// promotion inside <see cref="ChatIdentityDirectoryService.RevokeAsync"/> repoint that account's
/// default across every tenant it is linked to.
/// </para>
/// </remarks>
public sealed class ChatLinkScope
{
    private ChatLinkScope((Guid TenantId, Guid SubjectId)? owner) => Owner = owner;

    /// <summary>
    /// The tenant and subject the caller is confined to, or <c>null</c> for a cross-tenant caller.
    /// </summary>
    public (Guid TenantId, Guid SubjectId)? Owner { get; }

    /// <summary>Reaches a row in any tenant, owned by any subject.</summary>
    public static ChatLinkScope Unscoped { get; } = new(null);

    /// <summary>
    /// Reaches only the row <paramref name="subjectId"/> owns in <paramref name="tenantId"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Either id is <see cref="Guid.Empty"/>. Such a scope matches no row, so without this the
    /// caller could not tell an unresolved tenant or subject from a link that does not exist.
    /// </exception>
    public static ChatLinkScope ForOwner(Guid tenantId, Guid subjectId)
        => tenantId != Guid.Empty && subjectId != Guid.Empty
            ? new((tenantId, subjectId))
            : throw new InvalidOperationException(
                "A chat identity link operation requires a resolved tenant and owning subject.");
}
