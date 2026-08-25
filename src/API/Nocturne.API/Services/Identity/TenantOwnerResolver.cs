using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="ITenantOwnerResolver"/>
public class TenantOwnerResolver : ITenantOwnerResolver
{
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;

    public TenantOwnerResolver(IDbContextFactory<NocturneDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task<string?> GetOwnerSubjectIdAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        // Pinned to the tenant asked about: the callers are background services
        // (metadata publishing, compression-low detection, share-link notifications) with no
        // ambient tenant, so the context carries no pin of its own.
        await using var context = await _contextFactory.CreateTenantPinnedContextAsync(
            tenantId, cancellationToken);

        var ownerSubjectId = await context.TenantMembers.AsNoTracking()
            .Where(tm => tm.TenantId == tenantId
                && tm.MemberRoles.Any(mr => mr.TenantRole.Slug == RoleSeeds.Owner))
            .Select(tm => tm.SubjectId)
            .FirstOrDefaultAsync(cancellationToken);

        return ownerSubjectId == Guid.Empty ? null : ownerSubjectId.ToString();
    }
}
