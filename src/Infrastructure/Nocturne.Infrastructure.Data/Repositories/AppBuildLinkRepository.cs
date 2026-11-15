using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.AppBuilds;
using Nocturne.Core.Models.AppBuilds;
using Nocturne.Infrastructure.Data.Entities;
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

    public async Task SaveAsync(AppBuildLink link, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.AppBuildLinks.FirstOrDefaultAsync(row => row.TargetId == link.TargetId, ct);
        if (row is null)
        {
            row = new AppBuildLinkEntity { Id = Guid.CreateVersion7() };
            db.AppBuildLinks.Add(row);
        }

        AppBuildLinkMapper.Apply(link, row);
        await db.SaveChangesAsync(ct);
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
