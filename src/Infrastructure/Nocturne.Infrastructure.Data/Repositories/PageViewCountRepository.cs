using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Telemetry;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary><see cref="IPageViewCountStore"/> over <c>page_view_counts</c>, one platform context per call.</summary>
public class PageViewCountRepository(INocturneDbContextFactory contexts) : IPageViewCountStore
{
    /// <inheritdoc/>
    public async Task AddAsync(IReadOnlyCollection<PageViewCount> increments, CancellationToken ct)
    {
        if (increments.Count == 0)
            return;

        var dates = increments.Select(i => i.Date).ToArray();
        var routes = increments.Select(i => i.RouteId).ToArray();
        var counts = increments.Select(i => i.Count).ToArray();

        await using var db = await contexts.CreatePlatformAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO page_view_counts (date, route_id, count)
            SELECT * FROM unnest({dates}::date[], {routes}::text[], {counts}::bigint[])
            ON CONFLICT (date, route_id) DO UPDATE SET count = page_view_counts.count + EXCLUDED.count
            """,
            ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PageViewCount>> ReadAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.PageViewCounts
            .AsNoTracking()
            .Where(c => c.Date >= from && c.Date <= to)
            .OrderBy(c => c.Date)
            .ThenBy(c => c.RouteId)
            .Select(c => new PageViewCount(c.Date, c.RouteId, c.Count))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteBeforeAsync(DateOnly cutoff, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.PageViewCounts.Where(c => c.Date < cutoff).ExecuteDeleteAsync(ct);
    }
}
