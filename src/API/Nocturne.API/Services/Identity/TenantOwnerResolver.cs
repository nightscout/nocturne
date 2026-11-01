using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="ITenantOwnerResolver"/>
/// <remarks>
/// The callers are background services (metadata publishing, compression-low detection,
/// share-link notifications) with no ambient tenant, so the repository's tenant pin is what
/// gives the read its reach.
/// </remarks>
public class TenantOwnerResolver(ITenantMembershipRepository members) : ITenantOwnerResolver
{
    /// <inheritdoc />
    public async Task<string?> GetOwnerSubjectIdAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var owners = await members.GetOwnerSubjectIdsAsync(tenantId, cancellationToken);
        return owners.Count == 0 ? null : owners[0].ToString();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetOwnerSubjectIdsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var owners = await members.GetOwnerSubjectIdsAsync(tenantId, cancellationToken);
        return owners.Distinct().Select(id => id.ToString()).ToList();
    }
}
