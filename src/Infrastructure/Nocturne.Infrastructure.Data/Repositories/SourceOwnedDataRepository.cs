using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Services;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ISourceOwnedDataReader"/> and <see cref="ISourceOwnedDataPurge"/> over
/// <see cref="NocturneDbContext"/>, one context of the scope's reader per call.
/// </summary>
/// <remarks>
/// Registered scoped: one instance backs both interfaces. The purge routes every table through the
/// strongest audited path its entity supports, each table in its own transaction. The glucose tables
/// are deleted by their V4 repositories; only their re-import release runs here. Capability matrix
/// (entity interfaces decide the path):
/// <list type="bullet">
/// <item>The auditable+soft-deletable records (Bolus, CarbIntake, BolusCalculation,
///   DeviceEvent, BGCheck, Note, ApsSnapshot, TempBasal, StateSpan, ProfileActivation,
///   TherapySettings and the four schedules) take the audited soft-delete path,
///   which stamps the <c>deleted_by_user</c> flag the soft-delete dedup reads to block re-import
///   (<see cref="SoftDeleteDedupExtensions"/>).</item>
/// <item>Heart rate, step counts and body weights are soft-deletable but not auditable: the same
///   path soft-deletes and stamps them and records the bulk summary. Their create path reads the
///   stamp on a row holding the record's sync key or primary key, so it blocks their re-import the
///   same way; once released, a record keyed by its primary key revives its row in place.</item>
/// </list>
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

    /// <summary>
    /// What a source owns in one table: the buckets discovery groups it into (none for a table
    /// discovery does not list), a count by source, the profile names a purge must refresh, and the
    /// delete and re-import release.
    /// </summary>
    private interface IOwnedTable
    {
        string Type { get; }

        /// <summary>The model type the table's rows are served as, which a write to it reports.</summary>
        Type Model { get; }

        Task<List<SourceBucket>> BucketsAsync(
            NocturneDbContext db, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct);

        Task<long> CountAsync(NocturneDbContext db, string source, CancellationToken ct);

        Task<List<string>> ProfileNamesAsync(NocturneDbContext db, string source, CancellationToken ct);

        Task<int> SoftDeleteAsync(
            NocturneDbContext db, string source, IAuditContext? audit, string scope, CancellationToken ct);

        Task<int> AllowReimportAsync(
            NocturneDbContext db, string source, IAuditContext? audit, string scope, CancellationToken ct);
    }

    private sealed class OwnedTable<T>(
        string type,
        Type model,
        Func<NocturneDbContext, IQueryable<T>> rows,
        Func<string, Expression<Func<T, bool>>> ownedBy,
        Func<string, IQueryable<T>, DateTime, DateTime, CancellationToken, Task<List<SourceBucket>>>? buckets = null,
        Expression<Func<T, string>>? profileName = null)
        : IOwnedTable where T : class, ITenantScoped, ISoftDeletable
    {
        public string Type => type;

        public Type Model => model;

        public Task<List<SourceBucket>> BucketsAsync(
            NocturneDbContext db, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct) =>
            buckets is null
                ? Task.FromResult(new List<SourceBucket>())
                : buckets(type, rows(db), windowStart, last24HoursDate, ct);

        public Task<long> CountAsync(NocturneDbContext db, string source, CancellationToken ct) =>
            rows(db).Where(ownedBy(source)).LongCountAsync(ct);

        public Task<List<string>> ProfileNamesAsync(NocturneDbContext db, string source, CancellationToken ct) =>
            profileName is null
                ? Task.FromResult(new List<string>())
                : rows(db).Where(ownedBy(source)).Select(profileName).Distinct().ToListAsync(ct);

        public Task<int> SoftDeleteAsync(
            NocturneDbContext db, string source, IAuditContext? audit, string scope, CancellationToken ct) =>
            db.AuditedSoftDeleteAsync(rows(db).Where(ownedBy(source)), audit, scope, ct);

        public Task<int> AllowReimportAsync(
            NocturneDbContext db, string source, IAuditContext? audit, string scope, CancellationToken ct) =>
            db.AuditedAllowReimportAsync(rows(db).Where(ownedBy(source)), audit, scope, ct);
    }

    private static OwnedTable<T> TimeSeries<T>(
        string type, Type model, Func<NocturneDbContext, IQueryable<T>> rows)
        where T : class, ITenantScoped, ISoftDeletable, ISourcedEntity, IObservationTimestamped =>
        new(type, model, rows, SourceFilter.For<T>, TimeSeriesBuckets);

    private static readonly IOwnedTable[] OwnedTables =
    [
        TimeSeries<BolusEntity>(SourceDataTypes.Boluses, typeof(Bolus), db => db.Boluses),
        TimeSeries<CarbIntakeEntity>(SourceDataTypes.CarbIntake, typeof(CarbIntake), db => db.CarbIntakes),
        TimeSeries<BolusCalculationEntity>(SourceDataTypes.BolusCalculations, typeof(BolusCalculation), db => db.BolusCalculations),
        TimeSeries<DeviceEventEntity>(SourceDataTypes.DeviceEvents, typeof(DeviceEvent), db => db.DeviceEvents),
        TimeSeries<BGCheckEntity>(SourceDataTypes.BGChecks, typeof(BGCheck), db => db.BGChecks),
        TimeSeries<NoteEntity>(SourceDataTypes.Notes, typeof(Note), db => db.Notes),
        TimeSeries<ApsSnapshotEntity>(SourceDataTypes.DeviceStatus, typeof(ApsSnapshot), db => db.ApsSnapshots),
        TimeSeries<TempBasalEntity>(SourceDataTypes.TempBasals, typeof(TempBasal), db => db.TempBasals),
        new OwnedTable<StateSpanEntity>(
            SourceDataTypes.StateSpans, typeof(StateSpan), db => db.StateSpans, source => s => s.Source == source, StateSpanBuckets),
        new OwnedTable<ProfileActivationEntity>(
            SourceDataTypes.StateSpans, typeof(ProfileActivation), db => db.ProfileActivations, SourceFilter.For<ProfileActivationEntity>),
        TimeSeries<HeartRateEntity>(SourceDataTypes.HeartRate, typeof(HeartRate), db => db.HeartRates),
        TimeSeries<StepCountEntity>(SourceDataTypes.Steps, typeof(StepCount), db => db.StepCounts),
        new OwnedTable<BodyWeightEntity>(
            SourceDataTypes.BodyWeight, typeof(BodyWeight), db => db.BodyWeights, SourceFilter.For<BodyWeightEntity>, BodyWeightBuckets),
        new OwnedTable<TherapySettingsEntity>(
            SourceDataTypes.Profiles, typeof(TherapySettings), db => db.TherapySettings, SourceFilter.For<TherapySettingsEntity>,
            profileName: e => e.ProfileName),
        new OwnedTable<BasalScheduleEntity>(
            SourceDataTypes.Profiles, typeof(BasalSchedule), db => db.BasalSchedules, SourceFilter.For<BasalScheduleEntity>,
            profileName: e => e.ProfileName),
        new OwnedTable<CarbRatioScheduleEntity>(
            SourceDataTypes.Profiles, typeof(CarbRatioSchedule), db => db.CarbRatioSchedules, SourceFilter.For<CarbRatioScheduleEntity>,
            profileName: e => e.ProfileName),
        new OwnedTable<SensitivityScheduleEntity>(
            SourceDataTypes.Profiles, typeof(SensitivitySchedule), db => db.SensitivitySchedules, SourceFilter.For<SensitivityScheduleEntity>,
            profileName: e => e.ProfileName),
        new OwnedTable<TargetRangeScheduleEntity>(
            SourceDataTypes.Profiles, typeof(TargetRangeSchedule), db => db.TargetRangeSchedules, SourceFilter.For<TargetRangeScheduleEntity>,
            profileName: e => e.ProfileName),
    ];

    private static SourceHandle HandleOf(bool fromDataSource) =>
        fromDataSource ? SourceHandle.DataSource : SourceHandle.Device;

    private static async Task<List<SourceBucket>> TimeSeriesBuckets<T>(
        string type, IQueryable<T> rows, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct)
        where T : class, ISourcedEntity, IObservationTimestamped
    {
        var stats = await rows
            .Where(e => e.Timestamp >= windowStart)
            .GroupBy(e => e.DataSource ?? e.Device)
            .Select(g => new
            {
                Key = g.Key,
                FromDataSource = g.Max(x => x.DataSource) != null,
                Count = g.LongCount(),
                Count24H = g.Count(x => x.Timestamp >= last24HoursDate),
                Latest = g.Max(x => x.Timestamp),
                Oldest = (DateTime?)g.Min(x => x.Timestamp),
            })
            .ToListAsync(ct);

        return stats
            .Select(s => new SourceBucket(
                type, s.Key, s.Count, s.Count24H, s.Latest, s.Oldest, HandleOf(s.FromDataSource)))
            .ToList();
    }

    private static async Task<List<SourceBucket>> BodyWeightBuckets(
        string type, IQueryable<BodyWeightEntity> rows, DateTime windowStart, DateTime last24HoursDate,
        CancellationToken ct)
    {
        var fromMills = new DateTimeOffset(windowStart, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var last24HoursMills = new DateTimeOffset(last24HoursDate, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var stats = await rows
            .Where(e => e.Mills >= fromMills)
            .GroupBy(e => e.DataSource ?? e.Device)
            .Select(g => new
            {
                Key = g.Key,
                FromDataSource = g.Max(x => x.DataSource) != null,
                Count = g.LongCount(),
                Count24H = g.Count(x => x.Mills >= last24HoursMills),
                Latest = g.Max(x => x.Mills),
                Oldest = g.Min(x => x.Mills),
            })
            .ToListAsync(ct);

        static DateTime Utc(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
        return stats
            .Select(s => new SourceBucket(
                type, s.Key, s.Count, s.Count24H, Utc(s.Latest), Utc(s.Oldest), HandleOf(s.FromDataSource)))
            .ToList();
    }

    // StateSpan records one undifferentiated origin: its writers populate Source from the
    // reported device string (DeviceStatusDecomposer) or from the row's data source, falling back
    // to the entering party (TreatmentDecomposer). It therefore names neither handle in
    // particular and contributes no evidence about which one its bucket's key is.
    private static async Task<List<SourceBucket>> StateSpanBuckets(
        string type, IQueryable<StateSpanEntity> rows, DateTime windowStart, DateTime last24HoursDate,
        CancellationToken ct)
    {
        var stats = await rows
            .Where(s => s.StartTimestamp >= windowStart)
            .GroupBy(s => s.Source)
            .Select(g => new
            {
                Key = g.Key,
                Count = g.LongCount(),
                Count24H = g.Count(x => x.StartTimestamp >= last24HoursDate),
                Latest = g.Max(x => x.StartTimestamp),
                Oldest = (DateTime?)g.Min(x => x.StartTimestamp),
            })
            .ToListAsync(ct);

        return stats
            .Select(s => new SourceBucket(type, s.Key, s.Count, s.Count24H, s.Latest, s.Oldest, null))
            .ToList();
    }

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
        var buckets = await TimeSeriesBuckets(
            SourceDataTypes.ManualBG, db.MeterGlucose, windowStart, last24Hours, ct);
        foreach (var table in OwnedTables)
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

        Add(SourceDataTypes.Glucose, await db.SensorGlucose.FromSource(source).LongCountAsync(ct));
        Add(SourceDataTypes.ManualBG, await db.MeterGlucose.FromSource(source).LongCountAsync(ct));
        Add(SourceDataTypes.Calibrations, await db.Calibrations.FromSource(source).LongCountAsync(ct));
        foreach (var table in OwnedTables)
            Add(table.Type, await table.CountAsync(db, source, ct));
        return counts;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<SourceTypeStats>> StatsBySourceAsync(
        string source, DateTime last24Hours, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);

        var glucose = await db
            .SensorGlucose.FromSource(source)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.LongCount(),
                Last24H = g.Count(sg => sg.Timestamp >= last24Hours),
                Latest = g.Max(sg => (DateTime?)sg.Timestamp),
                Oldest = g.Min(sg => (DateTime?)sg.Timestamp),
            })
            .FirstOrDefaultAsync(ct);

        var spans = await db
            .StateSpans.Where(s => s.Source == source)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.LongCount(),
                Last24H = g.Count(s => s.StartTimestamp >= last24Hours),
                Latest = g.Max(s => (DateTime?)s.StartTimestamp),
                Oldest = g.Min(s => (DateTime?)s.StartTimestamp),
            })
            .FirstOrDefaultAsync(ct);

        async Task<SourceTypeStats> CountAsync<T>(string type, IQueryable<T> rows)
            where T : class, ISourcedEntity, IObservationTimestamped
        {
            var total = await rows.FromSource(source).LongCountAsync(ct);
            var recent = total > 0
                ? await rows.FromSource(source).Where(e => e.Timestamp >= last24Hours).CountAsync(ct)
                : 0;
            return new SourceTypeStats(type, total, recent);
        }

        var meterGlucose = await CountAsync(SourceDataTypes.ManualBG, db.MeterGlucose);
        var boluses = await CountAsync(SourceDataTypes.Boluses, db.Boluses);
        var carbIntakes = await CountAsync(SourceDataTypes.CarbIntake, db.CarbIntakes);
        var bolusCalculations = await CountAsync(SourceDataTypes.BolusCalculations, db.BolusCalculations);
        var notes = await CountAsync(SourceDataTypes.Notes, db.Notes);
        var deviceEvents = await CountAsync(SourceDataTypes.DeviceEvents, db.DeviceEvents);
        var deviceStatus = await CountAsync(SourceDataTypes.DeviceStatus, db.ApsSnapshots);
        var bgChecks = await CountAsync(SourceDataTypes.BGChecks, db.BGChecks);
        var tempBasals = await CountAsync(SourceDataTypes.TempBasals, db.TempBasals);

        return
        [
            new SourceTypeStats(
                SourceDataTypes.Glucose, glucose?.Total ?? 0, glucose?.Last24H ?? 0, glucose?.Latest, glucose?.Oldest),
            meterGlucose,
            boluses,
            carbIntakes,
            bolusCalculations,
            notes,
            deviceEvents,
            bgChecks,
            tempBasals,
            new SourceTypeStats(
                SourceDataTypes.StateSpans, spans?.Total ?? 0, spans?.Last24H ?? 0, spans?.Latest, spans?.Oldest),
            deviceStatus,
        ];
    }

    /// <inheritdoc/>
    public virtual Task<DateTime?> LatestTreatmentTimestampAsync(string source, CancellationToken ct) =>
        TreatmentTimestampAsync(source, latest: true, ct);

    /// <inheritdoc/>
    public virtual Task<DateTime?> OldestTreatmentTimestampAsync(string source, CancellationToken ct) =>
        TreatmentTimestampAsync(source, latest: false, ct);

    private async Task<DateTime?> TreatmentTimestampAsync(string source, bool latest, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);

        Task<DateTime?> EdgeAsync<T>(IQueryable<T> rows) where T : class, ISourcedEntity, IObservationTimestamped
        {
            var owned = rows.AsNoTracking().FromSource(source);
            return (latest ? owned.OrderByDescending(e => e.Timestamp) : owned.OrderBy(e => e.Timestamp))
                .Select(e => (DateTime?)e.Timestamp)
                .FirstOrDefaultAsync(ct);
        }

        var timestamps = new[]
        {
            await EdgeAsync(db.Boluses),
            await EdgeAsync(db.CarbIntakes),
            await EdgeAsync(db.BGChecks),
            await EdgeAsync(db.Notes),
            await EdgeAsync(db.DeviceEvents),
            await EdgeAsync(db.TempBasals),
            await EdgeAsync(db.BolusCalculations),
        }.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty();

        var edge = latest ? timestamps.Max() : timestamps.Min();
        return edge == default ? null : edge;
    }

    /// <inheritdoc/>
    public virtual async Task<SourcePurgeResult> SoftDeleteBySourceAsync(
        string source, string scope, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var deleted = new Dictionary<string, long>();
        var profileNames = new HashSet<string>();
        var written = new HashSet<Type>();
        try
        {
            foreach (var table in OwnedTables)
            {
                profileNames.UnionWith(await table.ProfileNamesAsync(db, source, ct));
                var count = await table.SoftDeleteAsync(db, source, _audit, scope, ct);
                if (count <= 0) continue;

                deleted[table.Type] = deleted.GetValueOrDefault(table.Type) + count;
                written.Add(table.Model);
            }
        }
        finally
        {
            await ReportWrittenAsync(written);
        }

        return new SourcePurgeResult(deleted, profileNames);
    }

    /// <inheritdoc/>
    public virtual async Task AllowReimportBySourceAsync(string source, string scope, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var written = new HashSet<Type>();
        try
        {
            if (await db.AuditedAllowReimportAsync(db.SensorGlucose.FromSource(source), _audit, scope, ct) > 0)
                written.Add(typeof(SensorGlucose));
            if (await db.AuditedAllowReimportAsync(db.MeterGlucose.FromSource(source), _audit, scope, ct) > 0)
                written.Add(typeof(MeterGlucose));
            if (await db.AuditedAllowReimportAsync(db.Calibrations.FromSource(source), _audit, scope, ct) > 0)
                written.Add(typeof(Calibration));
            foreach (var table in OwnedTables)
            {
                if (await table.AllowReimportAsync(db, source, _audit, scope, ct) > 0)
                    written.Add(table.Model);
            }
        }
        finally
        {
            await ReportWrittenAsync(written);
        }
    }
}
