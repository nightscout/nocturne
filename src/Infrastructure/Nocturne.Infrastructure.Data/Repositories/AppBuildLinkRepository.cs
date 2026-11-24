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
    /// The replace reads the winner's row in a fresh context, as <c>SettingsRepository</c> does.
    /// </remarks>
    public async Task SaveAsync(AppBuildLink link, CancellationToken ct)
    {
        if (await ReplaceAsync(link, ct))
            return;

        try
        {
            await using var db = await contexts.CreateAsync(ct);
            var row = new AppBuildLinkEntity { Id = Guid.CreateVersion7() };
            AppBuildLinkMapper.Apply(link, row);
            db.AppBuildLinks.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            if (!await ReplaceAsync(link, ct))
                throw;
        }
    }

    private async Task<bool> ReplaceAsync(AppBuildLink link, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.AppBuildLinks.FirstOrDefaultAsync(row => row.TargetId == link.TargetId, ct);
        if (row is null)
            return false;

        AppBuildLinkMapper.Apply(link, row);
        await db.SaveChangesAsync(ct);
        return true;
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
