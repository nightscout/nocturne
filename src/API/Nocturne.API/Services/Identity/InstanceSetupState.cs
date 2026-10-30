using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="IInstanceSetupState"/>
public sealed class InstanceSetupState : IInstanceSetupState
{
    private readonly INocturneDbContextFactory _dbFactory;
    private readonly ISubjectRepository _subjects;

    public InstanceSetupState(INocturneDbContextFactory dbFactory, ISubjectRepository subjects)
    {
        _dbFactory = dbFactory;
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
        List<Guid> tenantIds;
        await using (var context = await _dbFactory.CreatePlatformAsync(ct))
        {
            tenantIds = await context.Tenants.AsNoTracking()
                .Select(t => t.Id)
                .ToListAsync(ct);
        }

        foreach (var tenantId in tenantIds)
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
