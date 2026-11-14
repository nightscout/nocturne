using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ISourceOwnedDataReader"/> and <see cref="ISourceOwnedDataPurge"/> over
/// <see cref="NocturneDbContext"/>, one context of the scope's reader per call.
/// </summary>
/// <remarks>
/// Registered scoped: one instance backs both interfaces. The purge (<see cref="PurgedTables"/>) routes
/// every table of <see cref="V4RecordTables"/> but the glucose ones through the strongest audited path
/// its entity supports, each table in its own transaction. The glucose tables are deleted by their V4
/// repositories; only their re-import release runs here. Entity interfaces decide the path:
/// <list type="bullet">
/// <item>An auditable, soft-deletable record takes the audited soft-delete path, which stamps the
///   <c>deleted_by_user</c> flag the soft-delete dedup reads to block re-import
///   (<see cref="SoftDeleteDedupExtensions"/>).</item>
/// <item>A soft-deletable record that is not auditable (the activity tables) takes the same path,
///   which soft-deletes and stamps it and records the bulk summary. Its create path reads the stamp on
///   a row holding the record's sync key or primary key, so it blocks re-import the same way; once
///   released, a record keyed by its primary key revives its row in place.</item>
/// </list>
/// A device status's extras row has no source, so it follows its status
/// (<see cref="V4RecordTables.StatusExtrasLeftBy"/>) after the snapshot tables, uncounted.
/// Each purge and each release tells <see cref="ILegacyReadCacheInvalidation"/> once, after its tables have
/// committed, which model types it changed, so the legacy read caches stop serving them.
/// </remarks>
public class SourceOwnedDataRepository : ISourceOwnedDataReader, ISourceOwnedDataPurge
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly IAuditContext _audit;
    private readonly ILegacyReadCacheInvalidation? _legacyReads;

    /// <summary>
    /// A module whose calls each open their own context and attribute writes to <paramref name="audit"/>.
    /// The actor is required: a purge with no actor reads as a system mutation, which stamps no
    /// <c>deleted_by_user</c> flag, so the next sync would re-import what the user deleted.
    /// </summary>
    public SourceOwnedDataRepository(
        INocturneDbContextFactory contexts, IAuditContext audit, ILegacyReadCacheInvalidation? legacyReads = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _legacyReads = legacyReads;
    }

    private Task ReportWrittenAsync(IReadOnlyCollection<Type> models) =>
        models.Count == 0 ? Task.CompletedTask : _legacyReads?.WrittenAsync(models) ?? Task.CompletedTask;

    /// <summary>The tables a source purge deletes from (<see cref="SourceOwnedDataRepository"/>).</summary>
    private static readonly IReadOnlyList<V4RecordTable> PurgedTables =
        [.. V4RecordTables.All.Where(t => t.Kind != V4RecordKind.Glucose)];

    /// <summary>The types a source's stats report the latest and oldest times of.</summary>
    private static readonly string[] ExtentTypes = [SourceDataTypes.Glucose, SourceDataTypes.StateSpans];

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<GlucoseDeviceStats>> GlucoseDevicesAsync(
        DateTime windowStart, DateTime last24Hours, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var devices = await db
            .SensorGlucose.Where(e => e.Timestamp >= windowStart && e.Device != null && e.Device != "")
            .GroupBy(e => e.Device)
            .Select(g => new
            {
                Device = g.Key!,
                DataSource = g.Max(e => e.DataSource),
                LastTimestamp = g.Max(e => e.Timestamp),
                FirstTimestamp = g.Min(e => e.Timestamp),
                TotalCount = g.LongCount(),
                Last24HCount = g.Count(e => e.Timestamp >= last24Hours),
            })
            .OrderBy(d => d.Device)
            .ToListAsync(ct);

        return devices
            .Select(d => new GlucoseDeviceStats(
                d.Device, d.DataSource, d.TotalCount, d.Last24HCount, d.LastTimestamp, d.FirstTimestamp))
            .ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<ApsDeviceLastSeen>> ApsSnapshotDevicesAsync(
        DateTime windowStart, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var devices = await db
            .ApsSnapshots.Where(ds => ds.Timestamp >= windowStart && ds.Device != null && ds.Device != "")
            .GroupBy(ds => ds.Device)
            .Select(g => new
            {
                Device = g.Key!,
                DataSource = g.Max(ds => ds.DataSource),
                Latest = g.Max(ds => ds.Timestamp),
            })
            .ToListAsync(ct);

        return devices.Select(d => new ApsDeviceLastSeen(d.Device, d.DataSource, d.Latest)).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<SourceBucket>> BucketsAsync(
        DateTime windowStart, DateTime last24Hours, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var buckets = new List<SourceBucket>();
        foreach (var table in V4RecordTables.All)
            buckets.AddRange(await table.BucketsAsync(db, windowStart, last24Hours, ct));
        return buckets;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyDictionary<string, long>> CountsBySourceAsync(
        string source, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var counts = new Dictionary<string, long>();
        void Add(string type, long count)
        {
            if (count > 0) counts[type] = counts.GetValueOrDefault(type) + count;
        }

        foreach (var table in V4RecordTables.All)
            Add(table.DataType, await table.CountAsync(db, source, ct));
        return counts;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<SourceTypeStats>> StatsBySourceAsync(
        string source, DateTime last24Hours, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var stats = new List<SourceTypeStats>();
        foreach (var table in V4RecordTables.Counted)
            stats.Add(await table.StatsAsync(db, source, last24Hours, ExtentTypes.Contains(table.Table.DataType), ct));
        return stats;
    }

    /// <inheritdoc/>
    public virtual IReadOnlyList<string> TreatmentDataTypes { get; } = [.. V4RecordTables.Treatments.Select(t => t.Table.DataType)];

    /// <inheritdoc/>
    public virtual Task<DateTime?> LatestTreatmentTimestampAsync(string source, CancellationToken ct) =>
        TreatmentTimestampAsync(source, latest: true, ct);

    /// <inheritdoc/>
    public virtual Task<DateTime?> OldestTreatmentTimestampAsync(string source, CancellationToken ct) =>
        TreatmentTimestampAsync(source, latest: false, ct);

    private async Task<DateTime?> TreatmentTimestampAsync(string source, bool latest, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var timestamps = new List<DateTime>();
        foreach (var table in V4RecordTables.Treatments)
        {
            if (await table.EdgeAsync(db, source, latest, ct) is { } edge)
                timestamps.Add(edge);
        }

        return timestamps.Count == 0 ? null : latest ? timestamps.Max() : timestamps.Min();
    }

    /// <inheritdoc/>
    public virtual async Task<SourcePurgeResult> SoftDeleteBySourceAsync(
        string source, string scope, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var deleted = new Dictionary<string, long>();
        var written = new HashSet<Type>();
        try
        {
            foreach (var table in PurgedTables)
            {
                var count = await table.SoftDeleteAsync(db, source, _audit, scope, ct);
                if (count <= 0) continue;

                deleted[table.DataType] = deleted.GetValueOrDefault(table.DataType) + count;
                written.Add(table.Model);
            }

            await db.AuditedSoftDeleteAsync(V4RecordTables.StatusExtrasLeftBy(db, source), _audit, scope, ct);
        }
        finally
        {
            await ReportWrittenAsync(written);
        }

        return new SourcePurgeResult(deleted);
    }

    /// <inheritdoc/>
    public virtual async Task AllowReimportBySourceAsync(string source, string scope, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var written = new HashSet<Type>();
        try
        {
            foreach (var table in V4RecordTables.All)
            {
                if (await table.AllowReimportAsync(db, source, _audit, scope, ct) > 0)
                    written.Add(table.Model);
            }

            await db.AuditedAllowReimportAsync(V4RecordTables.StatusExtrasLeftBy(db, source), _audit, scope, ct);
        }
        finally
        {
            await ReportWrittenAsync(written);
        }
    }
}
