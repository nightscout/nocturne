using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Services;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using static Nocturne.Infrastructure.Data.Services.Deduplication.DuplicateMatching;

namespace Nocturne.Infrastructure.Data;

/// <summary>What a V4 record table holds, which decides the lists over the tables it is in.</summary>
internal enum V4RecordKind
{
    Glucose,
    Treatment,
    StateSpan,
    DeviceStatus,
    Profile,
    Activity,
}

/// <summary>
/// Every V4 record table a source owns, each declared once with the facts the per-table lists over
/// them differ on. Each of those lists is derived here or from <see cref="All"/>, so a table added
/// to <see cref="All"/> reaches every list its facts put it in.
/// </summary>
internal static class V4RecordTables
{
    /// <summary>
    /// The tables in their declaration order, which is the order of a source's counts, discovery
    /// buckets and purge counts: each reaches a response as an ordered map.
    /// </summary>
    internal static readonly IReadOnlyList<V4RecordTable> All =
    [
        TimeSeries<SensorGlucoseEntity>(SourceDataTypes.Glucose, typeof(SensorGlucose), V4RecordKind.Glucose, db => db.SensorGlucose, discovered: false)
            .LinkedAs(RecordType.SensorGlucose, MatchCriteriaMapper.From),
        TimeSeries<MeterGlucoseEntity>(SourceDataTypes.ManualBG, typeof(MeterGlucose), V4RecordKind.Glucose, db => db.MeterGlucose),
        TimeSeries<CalibrationEntity>(SourceDataTypes.Calibrations, typeof(Calibration), V4RecordKind.Glucose, db => db.Calibrations, discovered: false),
        TimeSeries<BolusEntity>(SourceDataTypes.Boluses, typeof(Bolus), V4RecordKind.Treatment, db => db.Boluses)
            .LinkedAs(RecordType.Bolus, MatchCriteriaMapper.From),
        TimeSeries<CarbIntakeEntity>(SourceDataTypes.CarbIntake, typeof(CarbIntake), V4RecordKind.Treatment, db => db.CarbIntakes)
            .LinkedAs(RecordType.CarbIntake, MatchCriteriaMapper.From),
        TimeSeries<BolusCalculationEntity>(SourceDataTypes.BolusCalculations, typeof(BolusCalculation), V4RecordKind.Treatment, db => db.BolusCalculations)
            .LinkedAs(RecordType.BolusCalculation, MatchCriteriaMapper.From),
        TimeSeries<DeviceEventEntity>(SourceDataTypes.DeviceEvents, typeof(DeviceEvent), V4RecordKind.Treatment, db => db.DeviceEvents)
            .LinkedAs(RecordType.DeviceEvent, MatchCriteriaMapper.From),
        TimeSeries<BGCheckEntity>(SourceDataTypes.BGChecks, typeof(BGCheck), V4RecordKind.Treatment, db => db.BGChecks)
            .LinkedAs(RecordType.BGCheck, MatchCriteriaMapper.From),
        TimeSeries<NoteEntity>(SourceDataTypes.Notes, typeof(Note), V4RecordKind.Treatment, db => db.Notes)
            .LinkedAs(RecordType.Note, MatchCriteriaMapper.From),
        TimeSeries<ApsSnapshotEntity>(SourceDataTypes.DeviceStatus, typeof(ApsSnapshot), V4RecordKind.DeviceStatus, db => db.ApsSnapshots),
        TimeSeries<TempBasalEntity>(SourceDataTypes.TempBasals, typeof(TempBasal), V4RecordKind.Treatment, db => db.TempBasals)
            .LinkedAs(RecordType.TempBasal, MatchCriteriaMapper.From),
        new V4RecordTable<StateSpanEntity>(
                SourceDataTypes.StateSpans, typeof(StateSpan), V4RecordKind.StateSpan, db => db.StateSpans,
                source => s => s.Source == source, s => s.Source, s => s.StartTimestamp, newestStamp: null,
                StateSpanBuckets)
            .WithDeduplication(table => new DeduplicatedTable<StateSpanEntity>(
                RecordType.StateSpan, table, s => s.CreatedAt, MatchCriteriaMapper.From,
                s => new SpanEnd(s.SupersededById, s.EndTimestamp),
                DuplicateGroupPrimaries.ById, touch: null)),
        TimeSeries<ProfileActivationEntity>(SourceDataTypes.StateSpans, typeof(ProfileActivation), V4RecordKind.StateSpan, db => db.ProfileActivations, discovered: false),
        TimeSeries<HeartRateEntity>(SourceDataTypes.HeartRate, typeof(HeartRate), V4RecordKind.Activity, db => db.HeartRates),
        TimeSeries<StepCountEntity>(SourceDataTypes.Steps, typeof(StepCount), V4RecordKind.Activity, db => db.StepCounts),
        new V4RecordTable<BodyWeightEntity>(
            SourceDataTypes.BodyWeight, typeof(BodyWeight), V4RecordKind.Activity, db => db.BodyWeights,
            SourceFilter.For<BodyWeightEntity>, e => e.DataSource, eventTime: null, NewestStamp, BodyWeightBuckets),
        Profile<TherapySettingsEntity>(typeof(TherapySettings), db => db.TherapySettings),
        Profile<BasalScheduleEntity>(typeof(BasalSchedule), db => db.BasalSchedules),
        Profile<CarbRatioScheduleEntity>(typeof(CarbRatioSchedule), db => db.CarbRatioSchedules),
        Profile<SensitivityScheduleEntity>(typeof(SensitivitySchedule), db => db.SensitivitySchedules),
        Profile<TargetRangeScheduleEntity>(typeof(TargetRangeSchedule), db => db.TargetRangeSchedules),
    ];

    /// <summary>
    /// Sourced, soft-deletable entities that are not in <see cref="All"/>, each with the reason.
    /// No list over the record tables reaches them.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Type, string> Unregistered = new Dictionary<Type, string>
    {
        [typeof(BasalInjectionEntity)] = "not yet owned by a source: no source purge, count, stats or demo purge reaches it",
        [typeof(PumpSnapshotEntity)] = "not yet owned by a source: a source purge leaves its rows",
        [typeof(UploaderSnapshotEntity)] = "not yet owned by a source: a source purge leaves its rows",
    };

    /// <summary>
    /// Tables of a counted kind that a source's stats and the data overview do not count yet, each
    /// with the reason.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Type, string> Uncounted = new Dictionary<Type, string>
    {
        [typeof(CalibrationEntity)] = "calibrations are not yet counted in a source's stats or in the overview",
        [typeof(ProfileActivationEntity)] = "profile activations are not yet counted with the state spans that share their type, in a source's stats or in the overview",
    };

    /// <summary>
    /// The tables a source's stats list, in the order the stats are listed, which reaches the
    /// source's type breakdown as an ordered map.
    /// </summary>
    internal static readonly IReadOnlyList<TimedRecordTable> Counted = TimedOrdered(
        All.Where(IsCounted),
        typeof(SensorGlucoseEntity), typeof(MeterGlucoseEntity), typeof(BolusEntity), typeof(CarbIntakeEntity),
        typeof(BolusCalculationEntity), typeof(NoteEntity), typeof(DeviceEventEntity), typeof(BGCheckEntity),
        typeof(TempBasalEntity), typeof(StateSpanEntity), typeof(ApsSnapshotEntity));

    /// <summary>
    /// The tables the data overview aggregates, in the order that fixes the insertion order of
    /// <see cref="DailySummaryDay.Counts"/>, which survives into the serialised response.
    /// </summary>
    internal static readonly IReadOnlyList<TimedRecordTable> Overview = TimedOrdered(
        All.Where(IsCounted),
        typeof(SensorGlucoseEntity), typeof(MeterGlucoseEntity), typeof(BolusEntity), typeof(CarbIntakeEntity),
        typeof(BolusCalculationEntity), typeof(NoteEntity), typeof(DeviceEventEntity), typeof(StateSpanEntity),
        typeof(ApsSnapshotEntity), typeof(BGCheckEntity), typeof(TempBasalEntity));

    /// <summary>
    /// The tables the overview does not attribute to a data source, each with the reason. A source
    /// filter drops such a table rather than narrowing it.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Type, string> UnattributedInOverview = new Dictionary<Type, string>
    {
        [typeof(ApsSnapshotEntity)] = "carries a DataSource column the overview does not surface",
    };

    /// <summary>
    /// The tables deduplication links, in the order a full run passes over them, which it reports as
    /// its phases.
    /// </summary>
    internal static readonly IReadOnlyList<DeduplicatedTable> Deduplicated = [.. Ordered(
        All.Where(t => t.Deduplication is not null),
        typeof(SensorGlucoseEntity), typeof(BolusEntity), typeof(CarbIntakeEntity), typeof(BGCheckEntity),
        typeof(DeviceEventEntity), typeof(NoteEntity), typeof(BolusCalculationEntity), typeof(TempBasalEntity),
        typeof(StateSpanEntity)).Select(t => t.Deduplication!)];

    /// <summary>
    /// The treatment tables, whose rows a source's treatment totals and first and last treatment times
    /// span.
    /// </summary>
    internal static readonly IReadOnlyList<TimedRecordTable> Treatments = CountedTreatments();

    /// <summary>A treatment total is summed from the stats, so every treatment table has to be counted.</summary>
    private static TimedRecordTable[] CountedTreatments()
    {
        TimedRecordTable[] treatments = [.. All.Where(t => t.Kind == V4RecordKind.Treatment).Select(TimedOf)];
        var uncounted = treatments.Except(Counted).Select(t => t.Table.Entity.Name).ToList();
        return uncounted.Count == 0
            ? treatments
            : throw new InvalidOperationException($"Treatment tables missing from the stats: {string.Join(", ", uncounted)}.");
    }

    /// <summary>The newest-stamp reads of the tables behind each legacy collection the V4 records hold.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<Func<NocturneDbContext, CancellationToken, Task<DateTime?>>>> LastModified =
        new Dictionary<string, IReadOnlyList<Func<NocturneDbContext, CancellationToken, Task<DateTime?>>>>
        {
            [ApplicationConstants.ApiRoutes.Native.Entries] = StampsOf(V4RecordKind.Glucose),
            [ApplicationConstants.ApiRoutes.Native.Treatments] = StampsOf(V4RecordKind.Treatment),
            [ApplicationConstants.ApiRoutes.Native.Profile] = StampsOf(V4RecordKind.Profile),
            [ApplicationConstants.ApiRoutes.Native.DeviceStatus] = StampsOf(V4RecordKind.DeviceStatus),
        };

    private static readonly Dictionary<RecordType, DeduplicatedTable> ByRecordType =
        Deduplicated.ToDictionary(t => t.RecordType);

    private static readonly Dictionary<Type, RecordType> RecordTypes =
        All.Where(t => t.Deduplication is not null).ToDictionary(t => t.Entity, t => t.Deduplication!.RecordType);

    /// <summary>The deduplicated table of <paramref name="recordType"/>, or null for a value outside the enum.</summary>
    internal static DeduplicatedTable? Of(RecordType recordType) => ByRecordType.GetValueOrDefault(recordType);

    /// <summary>The record type rows of <paramref name="entity"/> are linked under, or null.</summary>
    internal static RecordType? RecordTypeOf(Type entity) =>
        RecordTypes.TryGetValue(entity, out var recordType) ? recordType : null;

    /// <summary>The tables of the given kinds, in declaration order.</summary>
    internal static IReadOnlyList<V4RecordTable> OfKind(params V4RecordKind[] kinds) =>
        [.. All.Where(t => kinds.Contains(t.Kind))];

    private static bool IsCounted(V4RecordTable table) =>
        table.Kind is not (V4RecordKind.Profile or V4RecordKind.Activity) && !Uncounted.ContainsKey(table.Entity);

    private static TimedRecordTable TimedOf(V4RecordTable table) =>
        table.Timed ?? throw new InvalidOperationException($"{table.Entity.Name} is listed by its event time but has none.");

    private static IReadOnlyList<TimedRecordTable> TimedOrdered(IEnumerable<V4RecordTable> members, params Type[] order) =>
        [.. Ordered(members, order).Select(TimedOf)];

    private static IReadOnlyList<Func<NocturneDbContext, CancellationToken, Task<DateTime?>>> StampsOf(V4RecordKind kind) =>
        [.. All.Where(t => t.Kind == kind).Select(t => t.LastModifiedAsync
            ?? throw new InvalidOperationException($"{t.Entity.Name} moves a collection's stamp but has no SysUpdatedAt."))];

    private static Task<DateTime?> NewestStamp<T>(IQueryable<T> rows, CancellationToken ct)
        where T : class, ISystemTimestamped =>
        rows.IncludingDeleted().AsNoTracking()
            .OrderByDescending(e => e.SysUpdatedAt)
            .Select(e => (DateTime?)e.SysUpdatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// <paramref name="members"/> in <paramref name="order"/>. Throws when the two name different
    /// tables, so a table whose facts put it in a list cannot be left out of that list's order.
    /// </summary>
    private static IReadOnlyList<V4RecordTable> Ordered(IEnumerable<V4RecordTable> members, params Type[] order)
    {
        var byEntity = members.ToDictionary(t => t.Entity);
        if (!byEntity.Keys.ToHashSet().SetEquals(order) || order.Length != byEntity.Count)
            throw new InvalidOperationException(
                $"The order names {string.Join(", ", order.Select(t => t.Name))} but the members are "
                + $"{string.Join(", ", byEntity.Keys.Select(t => t.Name))}.");
        return [.. order.Select(entity => byEntity[entity])];
    }

    private static V4RecordTable<T> TimeSeries<T>(
        string dataType, Type model, V4RecordKind kind, Expression<Func<NocturneDbContext, DbSet<T>>> set,
        bool discovered = true)
        where T : class, ITenantScoped, ISoftDeletable, IIdentified, ISourcedEntity, IObservationTimestamped, ISystemTimestamped =>
        new(dataType, model, kind, set, SourceFilter.For<T>, e => e.DataSource, e => e.Timestamp, NewestStamp,
            discovered ? TimeSeriesBuckets : null);

    private static V4RecordTable<T> Profile<T>(Type model, Expression<Func<NocturneDbContext, DbSet<T>>> set)
        where T : class, IProfileScopedEntity, ISystemTimestamped =>
        new(SourceDataTypes.Profiles, model, V4RecordKind.Profile, set, SourceFilter.For<T>, e => e.DataSource,
            e => e.Timestamp, NewestStamp, buckets: null);

    private static V4RecordTable<T> LinkedAs<T>(
        this V4RecordTable<T> table, RecordType recordType, Func<T, MatchCriteria> criteria)
        where T : class, ITenantScoped, ISoftDeletable, IDuplicateFlagged, ISystemTimestamped =>
        table.WithDeduplication(t => new DeduplicatedTable<T>(
            recordType, t, e => e.SysCreatedAt, criteria, spanEnd: null,
            DuplicateGroupPrimaries.ByUpdate, DuplicateGroupPrimaries.Touch));

    /// <summary>
    /// The property <paramref name="member"/> reads. One query shape serves every entity type, so a
    /// column arrives as a property name rather than an expression to compose into it.
    /// </summary>
    internal static string MemberName<TEntity, TValue>(Expression<Func<TEntity, TValue>> member) =>
        member.Body is MemberExpression access
            ? access.Member.Name
            : throw new ArgumentException(
                "The expression must be a simple member access (e => e.Timestamp): the query reads "
                + "that property by name.",
                nameof(member));

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
}

/// <summary>One table of <see cref="V4RecordTables"/>: its facts, and its reads and writes by source.</summary>
internal abstract class V4RecordTable(Type entity, string name, string dataType, Type model, V4RecordKind kind)
{
    public Type Entity { get; } = entity;

    /// <summary>The context's <c>DbSet</c> property, reported as the table's deduplication phase.</summary>
    public string Name { get; } = name;

    /// <summary>The <see cref="SourceDataTypes"/> key its rows are counted under.</summary>
    public string DataType { get; } = dataType;

    /// <summary>The model type the table's rows are served as, which a write to it reports.</summary>
    public Type Model { get; } = model;

    public V4RecordKind Kind { get; } = kind;

    /// <summary>The table read by its rows' event time, or null when its rows carry none.</summary>
    public abstract TimedRecordTable? Timed { get; }

    /// <summary>
    /// The newest <c>SysUpdatedAt</c>, deleted rows included, or null when the rows carry no such stamp.
    /// </summary>
    public abstract Func<NocturneDbContext, CancellationToken, Task<DateTime?>>? LastModifiedAsync { get; }

    /// <summary>How deduplication reads and writes the table, or null when it links none of its rows.</summary>
    public DeduplicatedTable? Deduplication { get; protected set; }

    /// <summary>The buckets discovery groups the table into, none for a table discovery does not list.</summary>
    public abstract Task<List<SourceBucket>> BucketsAsync(
        NocturneDbContext db, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct);

    public abstract Task<long> CountAsync(NocturneDbContext db, string source, CancellationToken ct);

    public abstract Task<int> SoftDeleteAsync(
        NocturneDbContext db, string source, IAuditContext? audit, string scope, CancellationToken ct);

    public abstract Task<int> AllowReimportAsync(
        NocturneDbContext db, string source, IAuditContext? audit, string scope, CancellationToken ct);

    /// <summary>Hard-deletes the source's rows, soft-deleted ones included (<see cref="PurgeExtensions"/>).</summary>
    public abstract Task<int> PurgeAsync(NocturneDbContext db, string source, CancellationToken ct);
}

/// <inheritdoc cref="V4RecordTable"/>
/// <remarks>
/// <c>ownedBy</c> selects the rows a source owns and <c>source</c> is the column a row's source is
/// read from. <c>eventTime</c> is when a row's event happened, null for a table read by its Unix
/// milliseconds alone, and <c>newestStamp</c> reads the newest <c>SysUpdatedAt</c>, null for a table
/// whose rows carry none.
/// </remarks>
internal sealed class V4RecordTable<T> : V4RecordTable
    where T : class, ITenantScoped, ISoftDeletable, IIdentified
{
    private readonly Func<string, Expression<Func<T, bool>>> _ownedBy;
    private readonly Func<string, IQueryable<T>, DateTime, DateTime, CancellationToken, Task<List<SourceBucket>>>? _buckets;

    public V4RecordTable(
        string dataType,
        Type model,
        V4RecordKind kind,
        Expression<Func<NocturneDbContext, DbSet<T>>> set,
        Func<string, Expression<Func<T, bool>>> ownedBy,
        Expression<Func<T, string?>> source,
        Expression<Func<T, DateTime>>? eventTime,
        Func<IQueryable<T>, CancellationToken, Task<DateTime?>>? newestStamp,
        Func<string, IQueryable<T>, DateTime, DateTime, CancellationToken, Task<List<SourceBucket>>>? buckets)
        : base(typeof(T), V4RecordTables.MemberName(set), dataType, model, kind)
    {
        Set = set.Compile();
        Source = source;
        _ownedBy = ownedBy;
        _buckets = buckets;
        TimedTable = eventTime is null ? null : new TimedRecordTable<T>(this, eventTime);
        LastModifiedAsync = newestStamp is null ? null : (db, ct) => newestStamp(Set(db), ct);
    }

    public Func<NocturneDbContext, DbSet<T>> Set { get; }

    public Expression<Func<T, string?>> Source { get; }

    public TimedRecordTable<T>? TimedTable { get; }

    public override TimedRecordTable? Timed => TimedTable;

    public override Func<NocturneDbContext, CancellationToken, Task<DateTime?>>? LastModifiedAsync { get; }

    /// <summary>
    /// Links the table into deduplication, which reads rows by their event time. Throws while the
    /// registry initialises for a table whose rows carry none.
    /// </summary>
    public V4RecordTable<T> WithDeduplication(Func<TimedRecordTable<T>, DeduplicatedTable> deduplication)
    {
        Deduplication = deduplication(
            TimedTable ?? throw new InvalidOperationException($"{typeof(T).Name} has no event time to deduplicate by."));
        return this;
    }

    public Expression<Func<T, bool>> OwnedBy(string by) => _ownedBy(by);

    public IQueryable<T> Owned(NocturneDbContext db, string by) => Set(db).Where(_ownedBy(by));

    public override Task<List<SourceBucket>> BucketsAsync(
        NocturneDbContext db, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct) =>
        _buckets is null
            ? Task.FromResult(new List<SourceBucket>())
            : _buckets(DataType, Set(db), windowStart, last24HoursDate, ct);

    public override Task<long> CountAsync(NocturneDbContext db, string by, CancellationToken ct) =>
        Owned(db, by).LongCountAsync(ct);

    public override Task<int> SoftDeleteAsync(
        NocturneDbContext db, string by, IAuditContext? audit, string scope, CancellationToken ct) =>
        db.AuditedSoftDeleteAsync(Owned(db, by), audit, scope, ct);

    public override Task<int> AllowReimportAsync(
        NocturneDbContext db, string by, IAuditContext? audit, string scope, CancellationToken ct) =>
        db.AuditedAllowReimportAsync(Owned(db, by), audit, scope, ct);

    public override Task<int> PurgeAsync(NocturneDbContext db, string by, CancellationToken ct) =>
        Set(db).PurgeAsync(_ownedBy(by), ct);
}

/// <summary>
/// A <see cref="V4RecordTable"/> whose rows carry an event time: what a source's stats, its treatment
/// edges and the data overview read.
/// </summary>
internal abstract class TimedRecordTable(V4RecordTable table)
{
    public V4RecordTable Table { get; } = table;

    /// <summary>
    /// The source's count and the part of it from <paramref name="last24Hours"/> on, with its latest
    /// and oldest times when <paramref name="extent"/>.
    /// </summary>
    public abstract Task<SourceTypeStats> StatsAsync(
        NocturneDbContext db, string source, DateTime last24Hours, bool extent, CancellationToken ct);

    /// <summary>The source's latest event time, or its oldest, or null when it owns no row.</summary>
    public abstract Task<DateTime?> EdgeAsync(NocturneDbContext db, string source, bool latest, CancellationToken ct);

    /// <summary>The table as the data overview reads it, attributed to its data source or not.</summary>
    public abstract IDataOverviewTable ToOverview(bool attributed);
}

/// <inheritdoc cref="TimedRecordTable"/>
internal sealed class TimedRecordTable<T>(V4RecordTable<T> table, Expression<Func<T, DateTime>> eventTime)
    : TimedRecordTable(table)
    where T : class, ITenantScoped, ISoftDeletable, IIdentified
{
    private readonly string _eventTimeName = V4RecordTables.MemberName(eventTime);

    public new V4RecordTable<T> Table => table;

    public Expression<Func<T, DateTime>> EventTime => eventTime;

    public override async Task<SourceTypeStats> StatsAsync(
        NocturneDbContext db, string by, DateTime last24Hours, bool extent, CancellationToken ct)
    {
        var name = _eventTimeName;
        if (extent)
        {
            var span = await table.Owned(db, by)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Total = g.LongCount(),
                    Last24H = g.Count(e => EF.Property<DateTime>(e, name) >= last24Hours),
                    Latest = g.Max(e => (DateTime?)EF.Property<DateTime>(e, name)),
                    Oldest = g.Min(e => (DateTime?)EF.Property<DateTime>(e, name)),
                })
                .FirstOrDefaultAsync(ct);
            return new SourceTypeStats(table.DataType, span?.Total ?? 0, span?.Last24H ?? 0, span?.Latest, span?.Oldest);
        }

        var total = await table.Owned(db, by).LongCountAsync(ct);
        var recent = total > 0
            ? await table.Owned(db, by).Where(e => EF.Property<DateTime>(e, name) >= last24Hours).CountAsync(ct)
            : 0;
        return new SourceTypeStats(table.DataType, total, recent);
    }

    public override Task<DateTime?> EdgeAsync(NocturneDbContext db, string by, bool latest, CancellationToken ct)
    {
        var name = _eventTimeName;
        var owned = table.Set(db).AsNoTracking().Where(table.OwnedBy(by));
        return (latest ? owned.OrderByDescending(eventTime) : owned.OrderBy(eventTime))
            .Select(e => (DateTime?)EF.Property<DateTime>(e, name))
            .FirstOrDefaultAsync(ct);
    }

    public override IDataOverviewTable ToOverview(bool attributed) =>
        new DataOverviewTable<T>(table.DataType, table.Set, eventTime, attributed ? table.Source : null);
}

/// <summary>
/// How deduplication reads and writes one table: the record info matching reads, and the duplicate
/// flag and primary stamp the group writes go through.
/// </summary>
internal abstract class DeduplicatedTable(RecordType recordType)
{
    public RecordType RecordType { get; } = recordType;

    /// <summary>
    /// Stamps promoted copies with the given instant, or null when the table's rows carry no
    /// <c>SysUpdatedAt</c>.
    /// </summary>
    public abstract Func<NocturneDbContext, IReadOnlyList<Guid>, DateTime, CancellationToken, Task<int>>? Touch { get; }

    /// <summary>
    /// A new full-run phase of the table. <see cref="LinkedRecordTypes.TypePhases"/> builds each once,
    /// after the registry has initialised, so the registry never calls into deduplication while it does.
    /// </summary>
    public abstract LinkedRecordTypes.TypePhase CreatePhase();

    public abstract Task<Dictionary<Guid, RecordInfo>> RecordInfoAsync(
        NocturneDbContext db, HashSet<Guid> ids, CancellationToken ct);

    public abstract Task<int> DeriveFlagsAsync(NocturneDbContext ctx, Guid[] ids, CancellationToken ct);

    public abstract IAsyncEnumerable<DisagreeingRow[]> DisagreeingPagesAsync(
        NocturneDbContext ctx, int pageSize, CancellationToken ct);

    public abstract Task<HashSet<Guid>> LiveIdsAsync(NocturneDbContext ctx, Guid[] ids, CancellationToken ct);
}

/// <inheritdoc cref="DeduplicatedTable"/>
/// <remarks>
/// <c>createdAt</c> is the insert stamp <see cref="LinkedRecordTypes.SingletonLinkRetention"/> is
/// measured on. <c>spanEnd</c> is how a row ended, for a table of spans; every other row reads as the
/// default. <c>pages</c> is the walk <see cref="DuplicateGroupPrimaries.DisagreeingPagesAsync"/> takes
/// over the table, and <c>touch</c> stamps a promoted copy, null for a table with no
/// <c>SysUpdatedAt</c>.
/// </remarks>
internal sealed class DeduplicatedTable<T>(
    RecordType recordType,
    TimedRecordTable<T> timed,
    Expression<Func<T, DateTime>> createdAt,
    Func<T, MatchCriteria> criteria,
    Func<T, SpanEnd>? spanEnd,
    Func<NocturneDbContext, DbSet<T>, RecordType, int, CancellationToken, IAsyncEnumerable<DisagreeingRow[]>> pages,
    Func<IQueryable<T>, IReadOnlyList<Guid>, DateTime, CancellationToken, Task<int>>? touch)
    : DeduplicatedTable(recordType)
    where T : class, ITenantScoped, ISoftDeletable, IDuplicateFlagged
{
    private V4RecordTable<T> Table => timed.Table;

    public override Func<NocturneDbContext, IReadOnlyList<Guid>, DateTime, CancellationToken, Task<int>>? Touch { get; } =
        touch is null ? null : (ctx, ids, touchAt, ct) => touch(timed.Table.Set(ctx), ids, touchAt, ct);

    public override LinkedRecordTypes.TypePhase CreatePhase() => LinkedRecordTypes.Phase(
        RecordType, Table.Name, db => Table.Set(db), timed.EventTime, static e => e.Id, createdAt,
        Table.Source.Compile(), criteria);

    public override Task<Dictionary<Guid, RecordInfo>> RecordInfoAsync(
        NocturneDbContext db, HashSet<Guid> ids, CancellationToken ct) =>
        LinkedRecordTypes.LoadAsync<T>(
            db, e => new RecordInfo(criteria(e), e.DeletedAt != null, spanEnd?.Invoke(e) ?? default), ids, ct);

    public override Task<int> DeriveFlagsAsync(NocturneDbContext ctx, Guid[] ids, CancellationToken ct) =>
        DuplicateGroupPrimaries.Derive(ctx, Table.Set(ctx), RecordType, ids, ct);

    public override IAsyncEnumerable<DisagreeingRow[]> DisagreeingPagesAsync(
        NocturneDbContext ctx, int pageSize, CancellationToken ct) =>
        pages(ctx, Table.Set(ctx), RecordType, pageSize, ct);

    public override Task<HashSet<Guid>> LiveIdsAsync(NocturneDbContext ctx, Guid[] ids, CancellationToken ct) =>
        DuplicateGroupPrimaries.Live(Table.Set(ctx), ids, ct);
}
