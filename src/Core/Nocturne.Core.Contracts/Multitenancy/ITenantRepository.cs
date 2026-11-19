using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Stores tenants: the directory every request and background sweep resolves a tenant from, and
/// the settings a tenant's owner edits. Memberships, roles and membership requests are on
/// <see cref="ITenantMembershipRepository"/>.
/// </summary>
/// <remarks>
/// <c>tenants</c> is not tenant-scoped: it carries no tenant policy, no category and no audit
/// trail, so a context's pin changes nothing it reads or writes there. Every method runs as the
/// scope's reader, or on the caller's unit. A background service, middleware or singleton holds a
/// root-resolved repository, which reads as no one, the same rows.
/// </remarks>
public interface ITenantRepository
{
    /// <summary>Stores a tenant and returns its id.</summary>
    Task<Guid> CreateAsync(NewTenant tenant, CancellationToken ct);

    /// <summary>The tenant with <paramref name="id"/>, with its demo configuration; null when there is none.</summary>
    Task<TenantRecord?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Those of <paramref name="ids"/> that exist.</summary>
    Task<IReadOnlyList<TenantRecord>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>The tenant whose slug is <paramref name="slug"/>; null when there is none.</summary>
    Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct);

    /// <summary>The tenant whose live public share token has digest <paramref name="tokenHash"/>; null when none has.</summary>
    Task<TenantRecord?> FindByShareTokenHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>Whether any tenant's share token has digest <paramref name="tokenHash"/>.</summary>
    Task<bool> ShareTokenHashExistsAsync(string tokenHash, CancellationToken ct);

    /// <summary>Whether the tenant <paramref name="id"/> exists and is active.</summary>
    Task<bool> IsActiveAsync(Guid id, CancellationToken ct);

    /// <summary>Whether a tenant with <paramref name="slug"/> exists.</summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct);

    /// <summary>
    /// The one active tenant a single-tenant install serves from its apex, or null when there is
    /// none or more than one. A demo tenant is not counted.
    /// </summary>
    Task<TenantRecord?> FindSoleTenantAsync(CancellationToken ct);

    /// <summary>Up to <paramref name="take"/> tenants that are not the demo, oldest first.</summary>
    Task<IReadOnlyList<TenantRecord>> ListNonDemoAsync(int take, CancellationToken ct);

    /// <summary>Whether any tenant other than the demo exists.</summary>
    Task<bool> AnyNonDemoAsync(CancellationToken ct);

    /// <summary>The demo tenant, with its demo configuration; null when none is provisioned.</summary>
    Task<TenantRecord?> FindDemoTenantAsync(CancellationToken ct);

    /// <summary>Every tenant, or with <paramref name="activeOnly"/> the active ones, oldest first.</summary>
    Task<IReadOnlyList<TenantRecord>> ListAsync(bool activeOnly, CancellationToken ct);

    /// <summary>The ids <see cref="ListAsync"/> would return, for a sweep that visits each tenant.</summary>
    Task<IReadOnlyList<Guid>> ListIdsAsync(bool activeOnly, CancellationToken ct);

    /// <summary>Applies <paramref name="edit"/> to the tenant; false when there is no such tenant.</summary>
    Task<bool> UpdateAsync(Guid id, TenantEdit edit, CancellationToken ct);

    /// <summary>Writes the share token's three columns together; false when there is no such tenant.</summary>
    Task<bool> SetShareTokenAsync(Guid id, ShareTokenState token, CancellationToken ct);

    /// <summary>Stamps when the share link was last viewed, in one statement; false when there is no such tenant.</summary>
    Task<bool> RecordShareAccessAsync(Guid id, DateTime at, CancellationToken ct);

    /// <summary>
    /// Deletes the tenant. Every tenant-scoped row goes with it by the database's cascade from
    /// <c>tenants</c>; false when there is no such tenant.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

public static class TenantRecordExtensions
{
    /// <summary>The tenant as a request resolves it.</summary>
    public static TenantContext ToContext(this TenantRecord tenant) =>
        new(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo);
}
