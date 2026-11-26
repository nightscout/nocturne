using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Telemetry;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using NpgsqlTypes;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary><see cref="IPageViewCountStore"/> over <c>page_view_counts</c>, one platform context per call.</summary>
public class PageViewCountRepository(INocturneDbContextFactory contexts) : IPageViewCountStore
{
    /// <inheritdoc/>
    public async Task AddAsync(IReadOnlyCollection<PageViewCount> increments, CancellationToken ct)
    {
        if (increments.Count == 0)
            return;

        await using var db = await contexts.CreatePlatformAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO page_view_counts (date, route_id, count)
            SELECT * FROM unnest(@dates, @routes, @counts)
            ON CONFLICT (date, route_id) DO UPDATE SET count = page_view_counts.count + EXCLUDED.count
            """,
            [
                new NpgsqlParameter("dates", NpgsqlDbType.Array | NpgsqlDbType.Date)
                {
                    Value = increments.Select(i => i.Date).ToArray(),
                },
                new NpgsqlParameter("routes", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = increments.Select(i => i.RouteId).ToArray(),
                },
                new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                {
                    Value = increments.Select(i => i.Count).ToArray(),
                },
            ],
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
