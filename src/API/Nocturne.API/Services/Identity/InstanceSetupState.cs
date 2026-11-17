using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="IInstanceSetupState"/>
public sealed class InstanceSetupState : IInstanceSetupState
{
    private readonly ITenantRepository _tenants;
    private readonly ISubjectRepository _subjects;

    public InstanceSetupState(ITenantRepository tenants, ISubjectRepository subjects)
    {
        _tenants = tenants;
        _subjects = subjects;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Asked per tenant under that tenant's own pin, then OR'd. The equivalent single query over
    /// every tenant's memberships has no tenant to be pinned to, and a membership hidden from it
    /// would read as "no credentialed member exists" — re-opening setup on a configured instance.
    /// </remarks>
    public async Task<bool> IsSetupCompleteAsync(CancellationToken ct = default)
    {
        foreach (var tenantId in await _tenants.ListIdsAsync(activeOnly: false, ct))
        {
            if (await TenantHasCredentialedMemberAsync(tenantId, ct))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>System-pinned in the repository: setup state is asked on public-share requests too.</remarks>
    public Task<bool> TenantHasCredentialedMemberAsync(Guid tenantId, CancellationToken ct = default) =>
        _subjects.TenantHasCredentialedMemberAsync(tenantId, ct);
}
