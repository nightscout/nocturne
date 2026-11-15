using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.AppBuilds;
using Nocturne.Core.Models.AppBuilds;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

public sealed class AppBuildLinkRepository(INocturneDbContextFactory contexts) : IAppBuildLinkRepository
{
    public async Task<IReadOnlyList<AppBuildLink>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var rows = await db.AppBuildLinks.AsNoTracking().OrderBy(row => row.TargetId).ToListAsync(ct);
        return rows.Select(AppBuildLinkMapper.ToDomainModel).ToList();
    }

    public async Task<AppBuildLink?> GetAsync(string targetId, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.AppBuildLinks.AsNoTracking().FirstOrDefaultAsync(row => row.TargetId == targetId, ct);
        return row is null ? null : AppBuildLinkMapper.ToDomainModel(row);
    }

    /// <remarks>
    /// Two first links for one target can race to insert. The loser of
    /// <c>ux_app_build_links_tenant_target</c> replaces the winner's row instead, as a later link would.
    /// </remarks>
    public async Task SaveAsync(AppBuildLink link, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.AppBuildLinks.FirstOrDefaultAsync(row => row.TargetId == link.TargetId, ct);
        if (row is not null)
        {
            AppBuildLinkMapper.Apply(link, row);
            await db.SaveChangesAsync(ct);
            return;
        }

        var inserted = db.AppBuildLinks.Add(new AppBuildLinkEntity { Id = Guid.CreateVersion7() });
        AppBuildLinkMapper.Apply(link, inserted.Entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            inserted.State = EntityState.Detached;
            var winner = await db.AppBuildLinks.FirstAsync(row => row.TargetId == link.TargetId, ct);
            AppBuildLinkMapper.Apply(link, winner);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> SetRepositoryAsync(string targetId, string repositoryFullName, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.AppBuildLinks.FirstOrDefaultAsync(row => row.TargetId == targetId, ct);
        if (row is null) return false;

        row.RepositoryFullName = repositoryFullName;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(string targetId, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.AppBuildLinks.FirstOrDefaultAsync(row => row.TargetId == targetId, ct);
        if (row is null) return false;

        db.AppBuildLinks.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
