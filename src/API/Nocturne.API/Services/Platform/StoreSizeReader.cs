using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Platform;

/// <inheritdoc cref="IStoreSizeReader"/>
public sealed class StoreSizeReader(
    INocturneDbContextFactory contexts,
    ITenantCache cache,
    ICategoryReadContext readContext,
    ITenantAccessor tenantAccessor,
    IReaderCachePartition partition,
    ILogger<StoreSizeReader> logger) : IStoreSizeReader
{
    private const string CacheKey = "dbstats:size";

    private const string FailureKey = "dbstats:size:failed";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(1);

    public async Task<DbStats> ReadAsync(CancellationToken ct = default)
    {
        if (readContext.IsShare || partition.Current is null)
            return new DbStats();

        if (cache.TryGetLocal<bool>(FailureKey, out _))
            return new DbStats();

        var tenantId = tenantAccessor.TenantId;

        try
        {
            return await cache.GetOrSetAsync(CacheKey, () => EstimateAsync(tenantId, ct), CacheTtl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Store size could not be read; reporting zero for {Window}", FailureTtl);
            cache.SetLocal(FailureKey, true, FailureTtl);
            return new DbStats();
        }
    }

    public async Task<DbStats> EstimateAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateForTenantAsync(tenantId, ct);
        return await EstimateAsync(db, ct);
    }

    public async Task<DatabaseSize> ReadDatabaseSizeAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.Database
            .SqlQueryRaw<DatabaseSize>(
                """
                SELECT pg_database_size(current_database()) AS "TotalBytes",
                       (SELECT COALESCE(SUM(pg_relation_size(c.oid)), 0)::bigint
                        FROM pg_class c WHERE c.relkind = 'i') AS "IndexBytes"
                """)
            .SingleAsync(ct);
    }

    /// <summary>
    /// The share of a table's relation that is attributed to a tenant. Row level security makes
    /// the tenant's own row count the only one a tenant context can read, so the table's total
    /// comes from the planner's <paramref name="reltuples"/>. That is -1 on a table never analysed,
    /// 0 on an empty one and stale after a bulk load: where it cannot be a denominator, the
    /// tenant is the table's only possible owner.
    /// </summary>
    internal static double ShareOf(long tenantRows, double reltuples) =>
        reltuples <= 0 || tenantRows > reltuples ? 1.0 : Math.Clamp(tenantRows / reltuples, 0.0, 1.0);

    internal static async Task<DbStats> EstimateAsync(DbContext db, CancellationToken ct)
    {
        var helper = db.GetService<ISqlGenerationHelper>();
        var tables = ShareRlsPolicy.TenantScopedTableNames(db.Model);
        if (tables.Count == 0)
            return new DbStats();

        var counts = string.Join(
            " UNION ALL ",
            tables.Select(t => $"SELECT '{t.Replace("'", "''")}' AS name, count(*) AS rows FROM {helper.DelimitIdentifier(t)}"));

        var sql = $"""
            SELECT t.rows AS "TenantRows",
                   c.reltuples::float8 AS "Reltuples",
                   pg_total_relation_size(c.oid) AS "TotalBytes",
                   pg_indexes_size(c.oid) AS "IndexBytes",
                   (SELECT count(*) FROM pg_index i WHERE i.indrelid = c.oid) AS "IndexCount"
            FROM ({counts}) t
            JOIN pg_class c ON c.oid = to_regclass(quote_ident(t.name))
            """;

        var rows = await db.Database.SqlQueryRaw<TableSize>(sql).ToListAsync(ct);

        var stats = new DbStats { Collections = rows.Count };
        foreach (var row in rows)
        {
            var share = ShareOf(row.TenantRows, row.Reltuples);
            stats.DataSize += (long)((row.TotalBytes - row.IndexBytes) * share);
            stats.IndexSize += (long)(row.IndexBytes * share);
            stats.Indexes += (int)row.IndexCount;
        }

        return stats;
    }

    private sealed class TableSize
    {
        public long TenantRows { get; init; }

        public double Reltuples { get; init; }

        public long TotalBytes { get; init; }

        public long IndexBytes { get; init; }

        public long IndexCount { get; init; }
    }
}
