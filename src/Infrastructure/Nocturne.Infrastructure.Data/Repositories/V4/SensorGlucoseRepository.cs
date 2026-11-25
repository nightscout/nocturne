using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.Queries;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing sensor glucose (CGM) records in the database. A deduplication
/// participant on top of the sync-key upsert and keyed delete of
/// <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>, so it keeps only the <c>GetAsync</c>
/// overload that adds a keyset cursor, a patient device and glucose streams to the base read, the legacy entries projection, the tenant
/// last-reading watermark and the source/time-range deletes.
/// </summary>
public class SensorGlucoseRepository : SyncUpsertRepositoryBase<SensorGlucose, SensorGlucoseEntity>, ISensorGlucoseRepository
{
    private readonly IGlucoseReadingBroadcaster? _readingBroadcaster;

    /// <summary>
    /// Initializes a new instance of the <see cref="SensorGlucoseRepository"/> class.
    /// </summary>
    public SensorGlucoseRepository(
        INocturneDbContextFactory contextFactory,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<SensorGlucoseRepository> logger,
        IRecordChangePublisher? changes = null,
        IDataEventSink<Entry>? entrySink = null,
        IGlucoseReadingBroadcaster? readingBroadcaster = null
    )
        : base(contextFactory, auditContext, logger, changes, entrySink, linker)
    {
        _readingBroadcaster = readingBroadcaster;
    }

    /// <summary>
    /// Relays the created readings newer than the tenant's last reading, so a restore, a gap-fill
    /// or a late upload of older history never reaches a client as the current reading.
    /// </summary>
    /// <remarks>
    /// Runs inside the write, before <see cref="AdvanceTenantLastReadingAsync"/> moves the tenant's
    /// <c>LastReadingAt</c> past this batch, so the column still holds the newest reading before it.
    /// </remarks>
    protected override async Task OnLiveVisibleCreatedAsync(
        IReadOnlyList<SensorGlucose> visibleCreated, CancellationToken ct)
    {
        if (_readingBroadcaster is null)
            return;

        DateTime? lastReadingAt;
        try
        {
            await using var ctx = await ContextFactory.CreateAsync(ct);
            lastReadingAt = await ctx.Tenants
                .Where(t => t.Id == ctx.TenantId)
                .Select(t => t.LastReadingAt)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (StoreFailure.Is(ex) || ex is InvalidOperationException or TimeoutException)
        {
            Logger.LogWarning(ex, "Could not read the tenant's last reading; glucose_reading not relayed");
            return;
        }

        var newer = lastReadingAt is { } last
            ? visibleCreated.Where(r => r.Timestamp > last).ToList()
            : visibleCreated;
        if (newer.Count > 0)
            await _readingBroadcaster.BroadcastAsync(newer, ct);
    }

    /// <inheritdoc />
    public Task<IEnumerable<SensorGlucose>> GetForEntriesAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        DateTime? afterTimestamp = null, Guid? afterId = null,
        CancellationToken ct = default, Guid? patientDeviceId = null,
        GlucoseStreams streams = GlucoseStreams.All) =>
        ReadForEntriesAsync(from, to, device, source, limit, offset, descending,
            afterTimestamp, afterId, patientDeviceId, streams, orderByValue: false, ct);

    /// <inheritdoc />
    public Task<IEnumerable<SensorGlucose>> GetForEntriesByValueAsync(
        DateTime? from, DateTime? to, string? source, int limit, int offset, bool descending,
        CancellationToken ct = default, GlucoseStreams streams = GlucoseStreams.All) =>
        ReadForEntriesAsync(from, to, device: null, source, limit, offset, descending,
            afterTimestamp: null, afterId: null, patientDeviceId: null, streams, orderByValue: true, ct);

    private async Task<IEnumerable<SensorGlucose>> ReadForEntriesAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit, int offset, bool descending,
        DateTime? afterTimestamp, Guid? afterId, Guid? patientDeviceId,
        GlucoseStreams streams, bool orderByValue, CancellationToken ct)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await BuildReadQuery(ctx, from, to, device, source, limit, offset,
                descending, afterTimestamp, afterId, patientDeviceId, streams, orderByValue)
            .Select(e => new SensorGlucose
            {
                Id = e.Id,
                LegacyId = e.LegacyId,
                Timestamp = e.Timestamp,
                CreatedAt = e.SysCreatedAt,
                ModifiedAt = e.SysUpdatedAt,
                Device = e.Device,
                App = e.App,
                DataSource = e.DataSource,
                UtcOffset = e.UtcOffset,
                PatientDeviceId = e.PatientDeviceId,
                Mgdl = e.Mgdl,
                Direction = SensorGlucoseMapper.ParseDirection(e.Direction),
                TrendRate = e.TrendRate,
                Noise = e.Noise,
                Filtered = e.Filtered,
                Unfiltered = e.Unfiltered,
                Delta = e.Delta,
                Rssi = e.Rssi,
            })
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    protected override Entry? ProjectToLegacyEntry(SensorGlucose model) => EntryProjection.FromSensorGlucose(model);

    /// <summary>
    /// Canonical-stream gate for the single-stream surfaces: a live write reaches v1/v3 realtime
    /// clients and the Home Assistant relay only as a canonical read shows it, so neither a duplicate
    /// copy nor another stream's losing reading interleaves into their feed. Runs after the write's
    /// dedup, so it reads the flags both left. Failures fail open: a broadcast is better than a silent
    /// drop.
    /// </summary>
    protected override async Task<IReadOnlyList<SensorGlucose>> FilterLegacyProjectionAsync(
        IReadOnlyList<SensorGlucose> models, CancellationToken ct)
    {
        try
        {
            await using var ctx = await ContextFactory.CreateAsync(ct);
            var ids = models.Select(m => m.Id).ToArray();
            var shown = (await ctx.SensorGlucose.AsNoTracking()
                    .ExcludeDuplicates()
                    .Where(e => ids.Contains(e.Id) && !e.IsSecondaryStream)
                    .Select(e => e.Id)
                    .ToListAsync(ct))
                .ToHashSet();
            return models.Where(m => shown.Contains(m.Id)).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (StoreFailure.Is(ex) || ex is InvalidOperationException or TimeoutException)
        {
            Logger.LogWarning(ex, "Canonical stream gate failed; broadcasting unfiltered");
            return models;
        }
    }

    /// <summary>Saves the write and recomputes the buckets it left and reached, in one transaction.</summary>
    protected override Task<int> SaveWriteAsync(
        NocturneDbContext ctx, SensorGlucoseEntity written, DateTime? vacatedAt, CancellationToken ct) =>
        ctx.ExecuteInTransactionAsync(async token =>
        {
            await GlucoseStreamFlags.LockAsync(ctx, token);
            var saved = await ctx.SaveChangesAsync(token);
            await GlucoseStreamFlags.RecomputeAsync(
                ctx, vacatedAt is { } before ? [written.Timestamp, before] : [written.Timestamp], token);
            return saved;
        }, ct: ct);

    /// <inheritdoc />
    protected override Task BeginBulkWriteAsync(NocturneDbContext ctx, CancellationToken ct) =>
        GlucoseStreamFlags.LockAsync(ctx, ct);

    /// <inheritdoc />
    protected override Task OnBulkSavedAsync(
        NocturneDbContext ctx, IReadOnlyList<SensorGlucoseEntity> written, IReadOnlyList<DateTime> vacatedAt,
        CancellationToken ct) =>
        GlucoseStreamFlags.RecomputeAsync(ctx, [.. written.Select(e => e.Timestamp), .. vacatedAt], ct);

    /// <inheritdoc />
    protected override SensorGlucoseEntity ToEntity(SensorGlucose model) => SensorGlucoseMapper.ToEntity(model);

    /// <inheritdoc />
    protected override SensorGlucose ToDomain(SensorGlucoseEntity entity) => SensorGlucoseMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(SensorGlucoseEntity target, SensorGlucose source) => SensorGlucoseMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets sensor glucose records based on filter criteria.
    /// Hides the non-primary copies of duplicate groups.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="afterTimestamp">Keyset cursor timestamp. When paired with <paramref name="afterId"/>, replaces offset-based pagination.</param>
    /// <param name="afterId">Keyset cursor record ID (tiebreaker). When paired with <paramref name="afterTimestamp"/>, replaces offset-based pagination.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="patientDeviceId">Matched against the registered patient-device link and combined
    /// with <paramref name="device"/> when both are set. Last so positional callers ending at
    /// <paramref name="ct"/> are unaffected.</param>
    /// <param name="streams">Which of the tenant's concurrent CGM streams to return.</param>
    /// <returns>A collection of sensor glucose records.</returns>
    public async Task<IEnumerable<SensorGlucose>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        DateTime? afterTimestamp = null,
        Guid? afterId = null,
        CancellationToken ct = default,
        Guid? patientDeviceId = null,
        GlucoseStreams streams = GlucoseStreams.All
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await BuildReadQuery(ctx, from, to, device, source, limit, offset,
            descending, afterTimestamp, afterId, patientDeviceId, streams).ToListAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(
        DateTime? from, DateTime? to, string? device, string? source, Guid? patientDeviceId,
        GlucoseStreams streams = GlucoseStreams.All, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await Matching(
            ctx.SensorGlucose.AsNoTracking(), from, to, device, source,
            query => WithAttribution(query, patientDeviceId, streams))
            .CountAsync(ct);
    }

    public async Task<IEnumerable<SensorGlucose>> GetForAnalyticsAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        DateTime? afterTimestamp = null, Guid? afterId = null,
        CancellationToken ct = default, Guid? patientDeviceId = null,
        GlucoseStreams streams = GlucoseStreams.All)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await BuildReadQuery(ctx, from, to, device, source, limit, offset,
                descending, afterTimestamp, afterId, patientDeviceId, streams)
            .Select(e => new SensorGlucose
            {
                Id = e.Id,
                Timestamp = e.Timestamp,
                Mgdl = e.Mgdl,
                PatientDeviceId = e.PatientDeviceId,
                DataSource = e.DataSource,
                Device = e.Device,
                UtcOffset = e.UtcOffset,
                Direction = SensorGlucoseMapper.ParseDirection(e.Direction),
                TrendRate = e.TrendRate,
                Noise = e.Noise,
            })
            .ToListAsync(ct);
    }

    private IQueryable<SensorGlucoseEntity> BuildReadQuery(
        NocturneDbContext ctx, DateTime? from, DateTime? to, string? device, string? source,
        int limit, int offset, bool descending,
        DateTime? afterTimestamp, Guid? afterId, Guid? patientDeviceId,
        GlucoseStreams streams, bool orderByValue = false)
    {
        return PagedRead(
            ctx.SensorGlucose.AsNoTracking(), from, to, device, source, limit, offset, descending,
            query => WithAttribution(query, patientDeviceId, streams),
            afterTimestamp, afterId,
            query => orderByValue
                ? descending
                    ? query.OrderByDescending(e => e.Mgdl).ThenByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
                    : query.OrderBy(e => e.Mgdl).ThenBy(e => e.Timestamp).ThenBy(e => e.Id)
                : query.InObservationOrder(descending));
    }

    private static IQueryable<SensorGlucoseEntity> WithAttribution(
        IQueryable<SensorGlucoseEntity> query, Guid? patientDeviceId, GlucoseStreams streams)
    {
        if (patientDeviceId.HasValue)
            query = query.Where(e => e.PatientDeviceId == patientDeviceId.Value);
        if (streams != GlucoseStreams.All)
            query = query.Where(e => !e.IsSecondaryStream);
        if (streams == GlucoseStreams.CanonicalExcludingEphemeral)
            query = query.Where(e => e.DataSource == null
                || (e.DataSource != DataSources.DemoService && e.DataSource != DataSources.Testing));
        return query;
    }

    /// <inheritdoc />
    public override async Task<SyncUpsert<SensorGlucose>> UpsertAsync(
        SensorGlucose model, WriteOrigin origin, CancellationToken ct = default)
    {
        var written = await base.UpsertAsync(model, origin, ct);
        await AdvanceTenantLastReadingAsync([written.Record], ct);
        return written;
    }

    /// <inheritdoc />
    protected override async Task<BulkWrite<SensorGlucose>> BulkWriteAsync(
        List<SensorGlucose> records, WriteOrigin origin, bool updateByLegacyId, CancellationToken ct)
    {
        var written = await base.BulkWriteAsync(records, origin, updateByLegacyId, ct);
        await AdvanceTenantLastReadingAsync([.. written], ct);
        return written;
    }

    /// <summary>
    /// Advances the tenant's LastReadingAt to the newest written reading timestamp (never
    /// backwards). Maintained here — the one chokepoint every sensor glucose write passes
    /// through — so uploader POSTs, connector publishes, and imports all keep it current;
    /// the alert sweep and tenant overview read the column as the cheap "when did CGM data
    /// last arrive" signal without querying the readings table. Reading time, not ingestion
    /// time: a backfill of old history must not make a silent sensor look fresh.
    /// </summary>
    private async Task AdvanceTenantLastReadingAsync(
        IReadOnlyCollection<SensorGlucose> written, CancellationToken ct)
    {
        if (written.Count == 0)
            return;

        var newest = written.Max(r => r.Timestamp);
        if (newest == default)
            return;

        // Clamp to wall clock: advance-only means a single future-dated reading (device clock
        // skew, a double-applied timezone offset) would otherwise pin the column ahead of now
        // for good — and the staleness/signal-loss evaluators compare "now - LastReadingAt",
        // so a pinned-future value silently disables the alerts that watch for silence.
        var now = DateTime.UtcNow;
        if (newest > now)
            newest = now;

        try
        {
            await using var ctx = await ContextFactory.CreateAsync(ct);
            if (ctx.TenantId == Guid.Empty)
                return;

            await ctx.Tenants
                .Where(t => t.Id == ctx.TenantId
                    && (t.LastReadingAt == null || t.LastReadingAt < newest))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastReadingAt, newest), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to advance tenant LastReadingAt");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Queries raw storage without the duplicate filter: a reading whose copies
    /// are all linked non-primary is still a stored duplicate, and hiding it here caused uploads
    /// from a second source to re-insert their whole window on every cycle.
    /// </remarks>
    public override async Task<SensorGlucose?> FindStoredDuplicateAsync(
        string? device, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await StoredDuplicateQuery(ctx, device is null ? null : [device], from, to)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : SensorGlucoseMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SensorGlucose>> FindStoredDuplicateCandidatesAsync(
        IReadOnlyCollection<string>? devices, DateTime from, DateTime to, int limit,
        CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await StoredDuplicateQuery(ctx, devices, from, to)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel).ToList();
    }

    /// <summary>
    /// The duplicate probe's query, shared by the single-entry and whole-batch forms so both see
    /// the same rows in the same order. Deliberately without the duplicate filter.
    /// </summary>
    private static IQueryable<SensorGlucoseEntity> StoredDuplicateQuery(
        NocturneDbContext ctx, IReadOnlyCollection<string>? devices, DateTime from, DateTime to)
    {
        var query = ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp < to);
        if (devices is { Count: 1 })
        {
            // One device is the overwhelmingly common case (a single uploader): keep it an
            // equality so the plan stays the index seek the multi-device `= ANY` cannot be.
            var device = devices.First();
            query = query.Where(e => e.Device == device);
        }
        else if (devices is { Count: > 1 })
        {
            // `= ANY($1)`, generic-planned once auto-prepare has run it ten times on a connection. The
            // list is a chunk's devices, a handful, which a custom plan searches end to end as well
            // (ArrayFilterPlans.HashedArrayLength).
            query = query.Where(e => e.Device != null && devices.Contains(e.Device));
        }
        return query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(RecordSelector selector, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx, ctx.SensorGlucose.Selected(selector), selector.Scope, DuplicateDelete.PromoteSurvivor, origin, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SensorGlucose>> GetAttributedReadingTimesAsync(
        Guid patientDeviceId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.PatientDeviceId == patientDeviceId && e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .Select(e => new SensorGlucose { Timestamp = e.Timestamp, UtcOffset = e.UtcOffset })
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A range of <c>ix_sensor_glucose_tenant_timestamp</c>, which unlike the partial list indexes
    /// keeps the duplicate copies, filtered by stream and data source; the own-group test probes
    /// <c>ix_linked_records_tenant_type_id</c>.
    /// </remarks>
    public async Task<SensorGlucose?> GetStreamPredecessorAsync(
        SensorGlucose reading, DateTime since, CancellationToken ct = default)
    {
        var key = RecordTypeKeys.Key(RecordType.SensorGlucose);
        var at = reading.Timestamp;
        var id = reading.Id;

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var dataSource = reading.DataSource ?? "";
        var query = ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.Timestamp >= since && e.Timestamp <= at && e.Id != id && e.Mgdl > 0
                && (e.DataSource ?? "") == dataSource);
        if (reading.PatientDeviceId is { } deviceId)
        {
            query = query.Where(e => e.PatientDeviceId == deviceId);
        }
        else
        {
            var device = reading.Device ?? "";
            query = query.Where(e => e.PatientDeviceId == null && (e.Device ?? "") == device);
        }

        var ownGroup = ctx.LinkedRecords
            .Where(l => l.RecordType == key && l.RecordId == id)
            .Select(l => l.CanonicalId);
        var entity = await query
            .Where(e => !ctx.LinkedRecords.Any(l =>
                l.RecordType == key && l.RecordId == e.Id && ownGroup.Contains(l.CanonicalId)))
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : SensorGlucoseMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<GlucoseStreamArrival>> GetStreamFirstArrivalsAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var streams = ctx.SensorGlucose.AsNoTracking()
            .GroupBy(e => new { e.WriterGrantId, e.DataSource, e.Device })
            .Select(g => new { g.Key.WriterGrantId, g.Key.DataSource, g.Key.Device, First = g.Min(e => e.SysCreatedAt) });
        var rows = await ctx.WriteWithHistoryClampLiftedAsync(token => streams.ToListAsync(token), ct);
        return rows.Select(r => new GlucoseStreamArrival(r.WriterGrantId, r.DataSource, r.Device, r.First)).ToList();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<GlucoseStreamReading>> GetStreamReadingsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to)
            .Select(e => new GlucoseStreamReading(e.Id, e.Timestamp, e.WriterGrantId, e.DataSource, e.Device))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(SensorGlucose Reading, Guid? DuplicateOf)>> GetHiddenAsync(
        DateTime from, DateTime to, int limit, DateTime? afterTimestamp, Guid? afterId,
        CancellationToken ct = default)
    {
        var key = RecordTypeKeys.Key(RecordType.SensorGlucose);

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to && (e.IsDuplicate || e.IsSecondaryStream));
        if (afterTimestamp is { } cursorAt && afterId is { } cursorId)
            query = query.Where(e => e.Timestamp > cursorAt || (e.Timestamp == cursorAt && e.Id > cursorId));

        var rows = await query
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .Take(limit)
            .Select(e => new
            {
                Entity = e,
                DuplicateOf = e.IsDuplicate
                    ? ctx.LinkedRecords
                        .Where(p => p.RecordType == key && p.IsPrimary
                            && ctx.LinkedRecords.Any(l =>
                                l.RecordType == key && l.RecordId == e.Id && l.CanonicalId == p.CanonicalId))
                        .Select(p => (Guid?)p.RecordId)
                        .FirstOrDefault()
                    : null,
            })
            .ToListAsync(ct);
        return rows.Select(r => (SensorGlucoseMapper.ToDomainModel(r.Entity), r.DuplicateOf)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredSource>> GetDiscoveredSourcesAsync(DateTime since, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        // Project the aggregate into an anonymous type so it translates to SQL GROUP BY; the record
        // is constructed client-side (EF can't translate a positional-record constructor projection).
        var grouped = await ctx.SensorGlucose
            .AsNoTracking()
            .Where(e => e.PatientDeviceId == null && e.Timestamp >= since)
            .GroupBy(e => new { e.DataSource, e.Device })
            .Select(g => new
            {
                g.Key.DataSource,
                g.Key.Device,
                Count = g.Count(),
                LastSeen = g.Max(e => e.Timestamp),
            })
            .ToListAsync(ct);
        return grouped
            .Select(g => new DiscoveredSource(g.DataSource, g.Device, g.Count, g.LastSeen))
            .OrderByDescending(d => d.LastSeen)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SensorGlucose>> GetUnattributedAsync(DateTime? from, DateTime? to, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.GetUnattributedAsync<SensorGlucoseEntity>(from, to, limit, ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A reading's device is its stream, so the back-stamp requests the tenant's stream recompute in
    /// the same transaction (<see cref="GlucoseStreamFlags"/>).
    /// </remarks>
    public async Task<int> SetPatientDeviceIdsAsync(IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.ExecuteInTransactionAsync(async token =>
        {
            var stamped = await ctx.SetPatientDeviceIdsAsync<SensorGlucoseEntity>(patientDeviceIdByRecordId, token);
            if (stamped > 0)
                await GlucoseStreamFlags.RequestHistoryAsync(ctx, token);
            return stamped;
        }, ct: ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid? PatientDeviceId, int Count)>> CountByPatientDeviceAsync(
        DateTime from, DateTime to, Guid? patientDeviceId = null, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsNoTracking().ExcludeDuplicates()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to);
        if (patientDeviceId is { } deviceId)
            query = query.Where(e => e.PatientDeviceId == deviceId);
        return (await query
                .GroupBy(e => e.PatientDeviceId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .Select(g => (g.Key, g.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HistoryRecord<SensorGlucose>>> GetCanonicalModifiedSinceAsync(
        long cursorMills, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await HistoryPage.GetAsync(
            ctx,
            ctx.SensorGlucose.IncludingDeleted().AsNoTracking()
                .Where(e => e.DeletedAt != null || (!e.IsDuplicate && !e.IsSecondaryStream)),
            e => e.SysUpdatedAt,
            e => e.Id,
            cursorMills,
            limit,
            Logger,
            nameof(SensorGlucose),
            ct);

        return entities.Select(e => new HistoryRecord<SensorGlucose>(ToDomain(e), e.DeletedAt is not null)).ToList();
    }

    /// <inheritdoc />
    public async Task<int> RecomputeStreamsAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await GlucoseStreamFlags.RecomputeHistoryAsync(ctx, GlucoseStreamFlags.HistoryPageSize, ct);
    }

    /// <summary>
    /// Deletes all sensor glucose records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or null for no lower bound.</param>
    /// <param name="to">Inclusive end, or null for no upper bound.</param>
    /// <param name="origin">Whether the delete is a live event; see <see cref="V4RepositoryBase{TModel,TEntity}.RaiseBroadcastAsync"/>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.InWindow(from, to);

        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx, query, $"timestamp={from:O}..{to:O}", DuplicateDelete.PromoteSurvivor, origin, ct);
    }
}
