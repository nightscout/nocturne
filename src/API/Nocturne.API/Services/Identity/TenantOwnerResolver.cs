using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="ITenantOwnerResolver"/>
public class TenantOwnerResolver : ITenantOwnerResolver
{
    private readonly INocturneDbContextFactory _contextFactory;

    public TenantOwnerResolver(INocturneDbContextFactory contextFactory)
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
        await using var context = await _contextFactory.CreateForTenantAsync(
            tenantId, cancellationToken);

        var ownerSubjectId = await context.TenantMembers.AsNoTracking()
            .OwnersOf(tenantId)
            .Select(tm => tm.SubjectId)
            .FirstOrDefaultAsync(cancellationToken);

        return ownerSubjectId == Guid.Empty ? null : ownerSubjectId.ToString();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetOwnerSubjectIdsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateForTenantAsync(
            tenantId, cancellationToken);

        var owners = await context.TenantMembers.AsNoTracking()
            .OwnersOf(tenantId)
            .Select(tm => tm.SubjectId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return owners.Select(id => id.ToString()).ToList();
    }
}
