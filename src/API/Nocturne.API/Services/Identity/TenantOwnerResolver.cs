using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;

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
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var ownerSubjectId = await context.TenantMembers.AsNoTracking()
            .Where(tm => tm.TenantId == tenantId
                && tm.MemberRoles.Any(mr => mr.TenantRole.Slug == TenantPermissions.SeedRoles.Owner))
            .Select(tm => tm.SubjectId)
            .FirstOrDefaultAsync(cancellationToken);

        return ownerSubjectId == Guid.Empty ? null : ownerSubjectId.ToString();
    }
}
