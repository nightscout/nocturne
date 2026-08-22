using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="IInstanceSetupState"/>
public sealed class InstanceSetupState : IInstanceSetupState
{
    private readonly IDbContextFactory<NocturneDbContext> _dbFactory;

    public InstanceSetupState(IDbContextFactory<NocturneDbContext> dbFactory)
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
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var tenantIds = await context.Tenants.AsNoTracking()
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tenantId in tenantIds)
        {
            if (await HasCredentialedMemberAsync(context, tenantId, ct))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool> TenantHasCredentialedMemberAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        return await HasCredentialedMemberAsync(context, tenantId, ct);
    }

    /// <summary>
    /// Whether <paramref name="tenantId"/> has a member holding a passkey or a linked OIDC
    /// identity, read under that tenant's pin.
    /// </summary>
    /// <remarks>
    /// The caller's context is re-pinned, so it must be one the caller owns — never the
    /// request-scoped context, which the rest of the request would then find pinned elsewhere.
    /// </remarks>
    private static async Task<bool> HasCredentialedMemberAsync(
        NocturneDbContext context, Guid tenantId, CancellationToken ct)
    {
        await context.PinTenantAsync(tenantId, ct);

        return await context.TenantMembers.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m =>
                context.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                context.SubjectOidcIdentities.Any(o => o.SubjectId == m.SubjectId), ct);
    }
}
