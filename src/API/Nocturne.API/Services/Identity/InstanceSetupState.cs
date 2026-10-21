using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="IInstanceSetupState"/>
public sealed class InstanceSetupState : IInstanceSetupState
{
    private readonly INocturneDbContextFactory _dbFactory;

    public InstanceSetupState(INocturneDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
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
    /// <remarks>
    /// System-pinned (<see cref="INocturneDbContextFactory.CreateSystemPinnedAsync"/>): setup state is asked on public-share
    /// requests too.
    /// </remarks>
    public async Task<bool> TenantHasCredentialedMemberAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateSystemPinnedAsync(tenantId, ct);

        return await context.TenantMembers.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m =>
                context.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                context.WorkingOidcIdentities().Any(o => o.SubjectId == m.SubjectId), ct);
    }
}
