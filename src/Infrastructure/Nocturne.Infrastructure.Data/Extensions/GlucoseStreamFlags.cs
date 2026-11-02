using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one writer of <see cref="SensorGlucoseEntity.IsSecondaryStream"/>: keeps it equal, on every
/// visible reading (live, not a duplicate), to whether <see cref="CanonicalGlucoseStream.Select"/> over
/// that reading's whole bucket drops it.
/// </summary>
/// <remarks>
/// <para>
/// A bucket's winner depends on every visible reading in it and on the tenant's CGM devices, so it is
/// recomputed in the transaction of every change to either: a reading written, moved, deleted or
/// restored, and a duplicate flag moved (<see cref="DuplicateGroupPrimaries.SetPrimariesAsync"/>). A
/// change too wide to recompute inline, a device change and an attribution back-stamp request the
/// tenant's history walk instead (<see cref="ISensorGlucoseRepository.RecomputeStreamsJob"/>), which also runs daily and repairs
/// whatever a path missed.
/// </para>
/// <para>
/// Every recompute holds the tenant's stream lock (<see cref="LockAsync"/>) from before it reads the
/// buckets to commit: two writers of different streams into one bucket cannot see each other's
/// uncommitted rows, so without it both would keep their own readings. The order is dedup write lock,
/// stream lock, then readings and links. A write, the dedup link insert and merge, the audit repair
/// and the history walk take the stream lock before their first write to a reading or a glucose
/// link. A soft delete or a restore takes it only once its own rows are written (at its repoint's
/// first link change or its recompute), so a delete stalled on a row never holds up the tenant's
/// ingest; one too wide to recompute inline takes it never (<see cref="DeferIfWideAsync"/>). That
/// leaves one cycle: a stream-lock holder (a write, the walk page, a merge's or link insert's
/// derive, the audit repair) updates a reading a concurrent delete or restore holds, or links its
/// repoint then waits for. PostgreSQL breaks it as a deadlock, and the retrying transaction it
/// aborts runs again. A deleted or duplicate row keeps
/// whatever flag it had; it is recomputed when it becomes visible again. The clamp is lifted
/// (<see cref="HistoryClampLiftExtensions"/>): a clamped member's write reaches buckets the clamp
/// would show only in part.
/// </para>
/// </remarks>
internal static class GlucoseStreamFlags
{
    /// <summary>Rows a delete or restore recomputes inline; above it the tenant's history walk is requested.</summary>
    internal const int InlineRowLimit = DuplicateGroupPrimaries.SetPrimariesChunkSize;

    /// <summary>Rows the history walk reads per page.</summary>
    internal const int HistoryPageSize = 10_000;

    /// <summary>Ids one flag statement writes.</summary>
    private const int WriteChunkSize = 10_000;

    /// <summary>Buckets apart two touched buckets may be and still be read in one range.</summary>
    private const int MaxRunBucketGap = 12;

    internal const int StreamLockClass = 0x4753_5452;

    private static readonly long BucketTicks = CanonicalGlucoseStream.BucketSize.Ticks;

    /// <summary>The context whose stream flags <see cref="DeferIfWideAsync"/> handed to the walk.</summary>
    private static readonly AsyncLocal<NocturneDbContext?> Deferred = new();

    /// <summary>
    /// For a delete or restore of more than <see cref="InlineRowLimit"/> readings: requests the
    /// tenant's history walk in the caller's transaction and, until the result is disposed, makes this
    /// context's stream locks and recomputes do nothing, so the repoint's chunks neither recompute
    /// work the walk redoes nor hold the tenant's ingest behind the stream lock. Null otherwise.
    /// </summary>
    /// <remarks>
    /// Not <see langword="async"/>: the deferral is set in the caller's execution context, which an
    /// async method would restore on return.
    /// </remarks>
    public static Task<IDisposable?> DeferIfWideAsync(
        NocturneDbContext ctx, int rowCount, CancellationToken ct)
    {
        if (rowCount <= InlineRowLimit || Deferred.Value == ctx)
            return Task.FromResult<IDisposable?>(null);

        var previous = Deferred.Value;
        Deferred.Value = ctx;
        return RequestedAsync(ctx, new Restore(() => Deferred.Value = previous), ct);
    }

    private static async Task<IDisposable?> RequestedAsync(NocturneDbContext ctx, IDisposable deferral, CancellationToken ct)
    {
        await RequestHistoryAsync(ctx, ct);
        return deferral;
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>
    /// Recomputes the buckets of the rows <paramref name="ids"/> name, deleted or not, or requests the
    /// tenant's history walk when they are more than <see cref="InlineRowLimit"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on a relational context.</exception>
    public static async Task AfterRowsChangedAsync(
        NocturneDbContext ctx, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0 || Deferred.Value == ctx)
            return;
        if (ids.Count > InlineRowLimit)
        {
            await RequestHistoryAsync(ctx, ct);
            return;
        }

        var idArray = ids.ToArray();
        var timestamps = await ctx.WriteWithHistoryClampLiftedAsync(
            token => ctx.SensorGlucose.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]).AsNoTracking()
                .Where(e => idArray.Contains(e.Id))
                .Select(e => e.Timestamp)
                .ToListAsync(token),
            ct);
        await RecomputeAsync(ctx, timestamps, ct);
    }

    /// <summary>
    /// Recomputes every bucket one of <paramref name="timestamps"/> falls in.
    /// </summary>
    /// <returns>The number of readings whose flag changed.</returns>
    /// <exception cref="InvalidOperationException">No transaction is open on a relational context.</exception>
    public static Task<int> RecomputeAsync(
        NocturneDbContext ctx, IEnumerable<DateTime> timestamps, CancellationToken ct)
    {
        if (ctx.Database.IsRelational() && ctx.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Stream flags change in the transaction of the write that moved them; none is open.");

        var runs = Runs(timestamps.Select(t => t.Ticks / BucketTicks));
        if (runs.Count == 0 || Deferred.Value == ctx)
            return Task.FromResult(0);

        return ctx.WriteWithHistoryClampLiftedAsync(async token =>
        {
            await LockAsync(ctx, token);

            var pool = await VisibleAsync(ctx, runs, token);

            var devices = await DevicesIfContestedAsync(ctx, pool, token);
            return await WriteAsync(ctx, Differing(pool, devices), token);
        }, ct);
    }

    /// <summary>
    /// Walks the tenant's whole history in bucket-aligned pages, oldest first, and recomputes every page
    /// whose stored flags differ from the rule, each in its own transaction. A page that already agrees
    /// takes no lock and writes nothing.
    /// </summary>
    /// <returns>The number of readings whose flag changed.</returns>
    public static async Task<int> RecomputeHistoryAsync(
        NocturneDbContext ctx, int pageSize, CancellationToken ct)
    {
        IReadOnlyList<PatientDevice>? devices = null;
        DateTime? from = null;
        var changed = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var query = ctx.SensorGlucose.AsNoTracking().ExcludeDuplicates();
            if (from is { } start)
                query = query.Where(e => e.Timestamp >= start);
            var page = await Project(query.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).Take(pageSize)).ToListAsync(ct);
            if (page.Count == 0)
                return changed;

            var full = page.Count == pageSize;
            var lastBucket = page[^1].Timestamp.Ticks / BucketTicks;
            if (full)
            {
                var complete = page.Where(r => r.Timestamp.Ticks / BucketTicks < lastBucket).ToList();
                // A bucket wider than a page is read whole, by its range.
                page = complete.Count > 0 ? complete : await VisibleAsync(ctx, [(At(lastBucket), At(lastBucket + 1))], ct);
            }

            if (devices is null && CanonicalGlucoseStream.HasMultipleStreams(page.Select(r => r.Reading).ToList()))
                devices = await DevicesAsync(ctx, ct);

            var stale = Differing(page, devices ?? []);
            if (stale.Count > 0)
                changed += await ctx.ExecuteInTransactionAsync(
                    token => RecomputeAsync(ctx, stale.Select(r => r.Timestamp), token), ct: ct);

            if (!full)
                return changed;
            from = At(page[^1].Timestamp.Ticks / BucketTicks + 1);
        }
    }

    /// <summary>
    /// Takes the tenant's stream lock for the caller's transaction; a transaction that already holds
    /// it takes nothing more. PostgreSQL only.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    public static Task LockAsync(NocturneDbContext ctx, CancellationToken ct) =>
        Deferred.Value == ctx ? Task.CompletedTask : ctx.LockForTransactionAsync(StreamLockClass, ctx.TenantId, ct);

    /// <summary>Requests the tenant's history walk in the caller's transaction. PostgreSQL only.</summary>
    public static Task RequestHistoryAsync(NocturneDbContext ctx, CancellationToken ct) =>
        ctx.Database.IsNpgsql()
            ? JobLedgerRepository.RequestInTransactionAsync(ctx, ISensorGlucoseRepository.RecomputeStreamsJob, ct)
            : Task.CompletedTask;

    /// <summary>
    /// The visible readings in any of <paramref name="runs"/>, in one statement whose shape does not
    /// depend on how many runs there are: the bounds go in as two arrays, and each run is a range
    /// read of the visible-row index. SQLite has no arrays, so it reads the span of the runs and keeps
    /// the rows inside one.
    /// </summary>
    private static async Task<List<StreamRow>> VisibleAsync(
        NocturneDbContext ctx, IReadOnlyList<(DateTime From, DateTime To)> runs, CancellationToken ct)
    {
        if (!ctx.Database.IsNpgsql())
        {
            var (first, last) = (runs[0].From, runs[^1].To);
            var span = await Project(ctx.SensorGlucose.AsNoTracking().ExcludeDuplicates()
                    .Where(e => e.Timestamp >= first && e.Timestamp < last))
                .ToListAsync(ct);
            return span.Where(r => runs.Any(run => r.Timestamp >= run.From && r.Timestamp < run.To)).ToList();
        }

        var froms = runs.Select(r => r.From).ToArray();
        var tos = runs.Select(r => r.To).ToArray();
        var tenant = ctx.TenantId;
        return await Project(ctx.SensorGlucose.FromSql($"""
                SELECT g.* FROM unnest({froms}::timestamptz[], {tos}::timestamptz[]) AS run(from_at, to_at)
                CROSS JOIN LATERAL (
                    SELECT * FROM sensor_glucose
                    WHERE tenant_id = {tenant} AND deleted_at IS NULL AND NOT is_duplicate
                      AND timestamp >= run.from_at AND timestamp < run.to_at) AS g
                """).AsNoTracking().ExcludeDuplicates())
            .ToListAsync(ct);
    }

    private static IQueryable<StreamRow> Project(IQueryable<SensorGlucoseEntity> rows) =>
        rows.Select(e => new StreamRow(e.Id, e.Timestamp, e.PatientDeviceId, e.DataSource, e.Device, e.IsSecondaryStream));

    /// <summary>The tenant's devices when <paramref name="pool"/> holds more than one stream, else none.</summary>
    private static async Task<IReadOnlyList<PatientDevice>> DevicesIfContestedAsync(
        NocturneDbContext ctx, IReadOnlyList<StreamRow> pool, CancellationToken ct) =>
        CanonicalGlucoseStream.HasMultipleStreams(pool.Select(r => r.Reading).ToList())
            ? await DevicesAsync(ctx, ct)
            : [];

    private static async Task<IReadOnlyList<PatientDevice>> DevicesAsync(NocturneDbContext ctx, CancellationToken ct) =>
        (await ctx.PatientDevices.AsNoTracking().ToListAsync(ct)).Select(PatientDeviceMapper.ToDomainModel).ToList();

    /// <summary>The rows of <paramref name="pool"/> whose stored flag differs from the rule.</summary>
    private static List<StreamRow> Differing(IReadOnlyList<StreamRow> pool, IReadOnlyList<PatientDevice> devices)
    {
        var readings = pool.Select(r => r.Reading).ToList();
        var kept = CanonicalGlucoseStream.Select(readings, devices).Select(r => r.Id).ToHashSet();
        return pool.Where(r => r.IsSecondaryStream == kept.Contains(r.Id)).ToList();
    }

    private static async Task<int> WriteAsync(NocturneDbContext ctx, List<StreamRow> differing, CancellationToken ct)
    {
        var written = 0;
        foreach (var rows in differing.GroupBy(r => !r.IsSecondaryStream))
        {
            var secondary = rows.Key;
            foreach (var chunk in rows.Select(r => r.Id).Order().Chunk(WriteChunkSize))
            {
                written += await ctx.SensorGlucose
                    .Where(e => chunk.Contains(e.Id) && e.IsSecondaryStream != secondary)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsSecondaryStream, secondary), ct);
            }
        }
        return written;
    }

    /// <summary>The bucket runs covering <paramref name="buckets"/>, as half-open time ranges.</summary>
    private static List<(DateTime From, DateTime To)> Runs(IEnumerable<long> buckets)
    {
        var ordered = buckets.Distinct().Order().ToList();
        var runs = new List<(DateTime, DateTime)>();
        if (ordered.Count == 0)
            return runs;

        var start = ordered[0];
        var end = start;
        foreach (var bucket in ordered.Skip(1))
        {
            if (bucket - end > MaxRunBucketGap)
            {
                runs.Add((At(start), At(end + 1)));
                start = bucket;
            }
            end = bucket;
        }
        runs.Add((At(start), At(end + 1)));
        return runs;
    }

    private static DateTime At(long bucket) => new(bucket * BucketTicks, DateTimeKind.Utc);

    private sealed record StreamRow(
        Guid Id, DateTime Timestamp, Guid? PatientDeviceId, string? DataSource, string? Device, bool IsSecondaryStream)
    {
        public SensorGlucose Reading { get; } = new()
        {
            Id = Id,
            Timestamp = Timestamp,
            PatientDeviceId = PatientDeviceId,
            DataSource = DataSource,
            Device = Device,
        };
    }
}
