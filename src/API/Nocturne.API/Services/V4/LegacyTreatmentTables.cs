using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.API.Services.V4;

/// <summary>The V4 repositories the time-range read of a <see cref="ILegacyTreatmentTable"/> goes through.</summary>
internal sealed record LegacyTreatmentRepositories(
    IBolusRepository Boluses,
    ICarbIntakeRepository CarbIntakes,
    IBGCheckRepository BGChecks,
    INoteRepository Notes,
    IDeviceEventRepository DeviceEvents,
    ITempBasalRepository TempBasals,
    IBolusCalculationRepository BolusCalculations
);

/// <summary>The time window, page size and provenance filter of one time-range projection read.</summary>
/// <remarks>
/// When <paramref name="NativeOnly"/> is <see langword="true"/>, records that mirror a legacy
/// v1/v2/v3 write (<c>LegacyId</c> set) are dropped. Treatments have no legacy table left to double
/// up against, so every caller passes <see langword="false"/>; the filter survives for callers that
/// want V4-native records only.
/// </remarks>
internal readonly record struct LegacyTreatmentRange(
    DateTime? From,
    DateTime? To,
    int Limit,
    bool NativeOnly
);

/// <summary>
/// One record fetched for projection, carrying the table that owns it, the row's primary key, the
/// creation and modification stamps of the row it came from, and whether that row is soft-deleted,
/// which only a modified-since read returns.
/// </summary>
internal readonly record struct FetchedRecord(
    ILegacyTreatmentTable Table,
    object Record,
    Guid Id,
    DateTime Created,
    DateTime Modified,
    bool Deleted = false
);

/// <summary>Food breakdown rows for the carb intakes of one projected page, keyed by carb intake.</summary>
internal sealed class CarbFoodIndex
{
    internal static readonly CarbFoodIndex Empty = new([]);

    private readonly Dictionary<Guid, List<TreatmentFood>> _byCarbIntakeId;

    internal CarbFoodIndex(IEnumerable<TreatmentFood> foods) =>
        _byCarbIntakeId = foods
            .GroupBy(f => f.CarbIntakeId)
            .ToDictionary(g => g.Key, g => g.ToList());

    internal List<TreatmentFood> For(Guid carbIntakeId) =>
        _byCarbIntakeId.GetValueOrDefault(carbIntakeId, []);
}

/// <summary>
/// One V4 record type behind the legacy treatment projection, in the terms both read surfaces need:
/// a time-range read, a modified-since read, and the legacy <see cref="Treatment"/> a standalone
/// record of that type projects into.
/// </summary>
/// <seealso cref="LegacyTreatmentTables"/>
internal interface ILegacyTreatmentTable
{
    /// <summary>Record type name, reported when a per-type fetch fails.</summary>
    string RecordType { get; }

    /// <summary>
    /// A page of records within the requested window, newest first.
    /// </summary>
    /// <remarks>
    /// <see cref="LegacyTreatmentRange.NativeOnly"/> is honoured after the page is taken, so for a
    /// type whose repository cannot push the provenance filter down (TempBasal, BolusCalculation)
    /// the page can come back short of <see cref="LegacyTreatmentRange.Limit"/>.
    /// </remarks>
    Task<IReadOnlyList<FetchedRecord>> InRangeAsync(
        LegacyTreatmentRepositories repositories,
        NocturneDbContext context,
        LegacyTreatmentRange range,
        CancellationToken ct
    );

    /// <summary>
    /// A page of records changed at or after <paramref name="cursorMills"/>, oldest first, ending on
    /// a millisecond boundary, soft-deleted rows included.
    /// </summary>
    /// <remarks>
    /// The boundary rule is <see cref="HistoryPage"/>'s. A live copy deduplication marked
    /// non-primary is left out, as every other treatment read leaves it out, so a client syncing
    /// through history is sent one copy of a dose two sources reported. A deleted copy is always
    /// delivered, so its tombstone reaches the client whatever deduplication decided about it.
    /// <para>
    /// A demotion is not sent: the demoted row's stamp does not move, and a client that already
    /// holds it keeps it. That copy stands for the same dose as the group's primary, and a
    /// tombstone for it would delete the uploader's own record on that client (AAPS invalidates its
    /// local treatment when history serves its identifier with <c>isValid: false</c>), so the client
    /// would stop counting a dose that was delivered. A primary that moves because its row was
    /// deleted is sent: the delete tombstones the old primary and stamps the promoted copy
    /// (<c>DuplicateGroupPrimaries.RepointAwayFromAsync</c>).
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<FetchedRecord>> ModifiedSinceAsync(
        NocturneDbContext context,
        long cursorMills,
        int limit,
        ILogger logger,
        CancellationToken ct
    );

    /// <summary>
    /// The live records carrying one of <paramref name="correlationIds"/>, non-primary copies left
    /// out as <see cref="ModifiedSinceAsync"/> leaves them out.
    /// </summary>
    Task<IReadOnlyList<FetchedRecord>> LiveByCorrelationAsync(
        NocturneDbContext context,
        IReadOnlyCollection<Guid> correlationIds,
        CancellationToken ct
    );

    /// <summary>Projects a record this table owns that is not part of a meal pairing.</summary>
    Treatment Project(object record, CarbFoodIndex foods);
}

/// <inheritdoc cref="ILegacyTreatmentTable"/>
internal sealed class LegacyTreatmentTable<TRecord, TEntity>(
    Func<
        LegacyTreatmentRepositories,
        LegacyTreatmentRange,
        CancellationToken,
        Task<IEnumerable<TRecord>>
    > inRange,
    Func<TRecord, string?> legacyId,
    Func<NocturneDbContext, IQueryable<TEntity>> table,
    Nocturne.Core.Models.RecordType dedupType,
    Func<TEntity, TRecord> toRecord,
    Func<TRecord, CarbFoodIndex, Treatment> project
) : ILegacyTreatmentTable
    where TRecord : class
    where TEntity : class, ISystemTimestamped, IIdentified, ISoftDeletable
{
    /// <inheritdoc />
    public string RecordType { get; } = typeof(TRecord).Name;

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> InRangeAsync(
        LegacyTreatmentRepositories repositories,
        NocturneDbContext context,
        LegacyTreatmentRange range,
        CancellationToken ct
    )
    {
        var records = await inRange(repositories, range, ct);

        if (range.NativeOnly)
            records = records.Where(r => legacyId(r) is null);

        return records
            .Select(r => new FetchedRecord(this, r, ((IV4Record)r).Id, CreatedAt(r), ModifiedAt(r)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> ModifiedSinceAsync(
        NocturneDbContext context,
        long cursorMills,
        int limit,
        ILogger logger,
        CancellationToken ct
    )
    {
        var entities = await HistoryPage.GetAsync(
            table(context).IncludingDeleted().AsNoTracking().ExcludeNonPrimaryKeepingDeleted(context, dedupType),
            e => e.SysUpdatedAt,
            e => e.Id,
            cursorMills,
            limit,
            logger,
            RecordType,
            ct);

        return entities
            .Select(e => (Record: toRecord(e), e.SysUpdatedAt, Deleted: e.DeletedAt is not null))
            .Select(x => new FetchedRecord(
                this, x.Record, ((IV4Record)x.Record).Id, CreatedAt(x.Record), x.SysUpdatedAt, x.Deleted))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> LiveByCorrelationAsync(
        NocturneDbContext context,
        IReadOnlyCollection<Guid> correlationIds,
        CancellationToken ct
    )
    {
        var ids = correlationIds.Select(id => (Guid?)id).ToList();
        var entities = await table(context)
            .AsNoTracking()
            .ExcludeNonPrimary(context, dedupType)
            .Where(e => ids.Contains(EF.Property<Guid?>(e, nameof(IV4Record.CorrelationId))))
            .ToListAsync(ct);

        return entities
            .Select(e => (Record: toRecord(e), e.SysUpdatedAt))
            .Select(x => new FetchedRecord(
                this, x.Record, ((IV4Record)x.Record).Id, CreatedAt(x.Record), x.SysUpdatedAt))
            .ToList();
    }

    // TempBasal is the one projected type that is not an IV4Record.
    private static DateTime CreatedAt(TRecord record) =>
        record is TempBasal tempBasal ? tempBasal.CreatedAt : ((IV4Record)record).CreatedAt;

    private static DateTime ModifiedAt(TRecord record) =>
        record is TempBasal tempBasal ? tempBasal.ModifiedAt : ((IV4Record)record).ModifiedAt;

    /// <inheritdoc />
    public Treatment Project(object record, CarbFoodIndex foods) => project((TRecord)record, foods);
}

/// <summary>
/// The <see cref="StateSpan"/>s of one category that <see cref="TreatmentDecomposer"/> wrote from a
/// legacy treatment, read back as that treatment. Their <see cref="StateSpanEntity.UpdatedAt"/> is the
/// modification stamp a history read pages on.
/// </summary>
internal sealed class LegacyStateSpanTable(
    StateSpanCategory category,
    Expression<Func<StateSpanEntity, bool>> fromTreatment,
    Func<StateSpanEntity, IDictionary<string, object>?, Treatment> project
) : ILegacyTreatmentTable
{
    private readonly string _category = category.ToString();

    /// <inheritdoc />
    public string RecordType { get; } = $"{nameof(StateSpan)}.{category}";

    internal IQueryable<StateSpanEntity> Rows(NocturneDbContext context) =>
        Served(context.StateSpans.AsNoTracking());

    private IQueryable<StateSpanEntity> Served(IQueryable<StateSpanEntity> spans) =>
        spans.Where(s => s.Category == _category).Where(fromTreatment);

    /// <summary>The served spans of this category whose latest delete was the user's.</summary>
    internal IQueryable<StateSpanEntity> UserTombstones(NocturneDbContext context) =>
        context.StateSpans.UserTombstones(context).Where(s => s.Category == _category).Where(fromTreatment);

    /// <summary>Spans starting inside the window, bounds inclusive.</summary>
    internal IQueryable<StateSpanEntity> InWindow(NocturneDbContext context, DateTime? from, DateTime? to)
    {
        var rows = Rows(context);
        if (from is { } lower)
            rows = rows.Where(s => s.StartTimestamp >= lower);
        if (to is { } upper)
            rows = rows.Where(s => s.StartTimestamp <= upper);
        return rows;
    }

    internal FetchedRecord Fetched(StateSpanEntity span) =>
        new(this, span, span.Id, span.CreatedAt, span.UpdatedAt);

    /// <inheritdoc />
    /// <remarks>
    /// Every span here mirrors a legacy write, so <see cref="LegacyTreatmentRange.NativeOnly"/> leaves none.
    /// </remarks>
    public async Task<IReadOnlyList<FetchedRecord>> InRangeAsync(
        LegacyTreatmentRepositories repositories,
        NocturneDbContext context,
        LegacyTreatmentRange range,
        CancellationToken ct
    )
    {
        if (range.NativeOnly)
            return [];

        var spans = await InWindow(context, range.From, range.To)
            .OrderByDescending(s => s.StartTimestamp)
            .ThenBy(s => s.Id)
            .Take(range.Limit)
            .ToListAsync(ct);

        return spans.Select(Fetched).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> ModifiedSinceAsync(
        NocturneDbContext context,
        long cursorMills,
        int limit,
        ILogger logger,
        CancellationToken ct
    )
    {
        var spans = await HistoryPage.GetAsync(
            Served(context.StateSpans.IncludingDeleted().AsNoTracking()),
            s => s.UpdatedAt, s => s.Id, cursorMills, limit, logger, RecordType, ct);

        return spans.Select(span => Fetched(span) with { Deleted = span.DeletedAt is not null }).ToList();
    }

    /// <inheritdoc />
    /// <remarks>A span carries no correlation, so it is never a meal's partner.</remarks>
    public Task<IReadOnlyList<FetchedRecord>> LiveByCorrelationAsync(
        NocturneDbContext context,
        IReadOnlyCollection<Guid> correlationIds,
        CancellationToken ct
    ) => Task.FromResult<IReadOnlyList<FetchedRecord>>([]);

    /// <inheritdoc />
    public Treatment Project(object record, CarbFoodIndex foods)
    {
        var span = (StateSpanEntity)record;
        var metadata = StateSpanMapper.ToDomainModel(span).Metadata;

        var treatment = project(span, metadata);
        treatment.Id = span.Id.ToString();
        treatment.Mills = HistoryPage.ToMilliseconds(span.StartTimestamp);
        treatment.RawTimestamp ??= TreatmentUploadedTimestamp.OfSpan(metadata);
        treatment.EnteredBy = metadata.TryReadString("enteredBy");
        treatment.Notes = metadata.TryReadString(LegacyTreatmentTables.NotesKey);
        treatment.SyncIdentifier = metadata.TryReadString(LegacyTreatmentTables.SyncIdentifierKey);
        treatment.InsulinType = metadata.TryReadString(LegacyTreatmentTables.InsulinTypeKey);
        treatment.UtcOffset = (int?)metadata.TryReadDecimal(StateSpanMetadataExtensions.UtcOffsetKey);
        return treatment;
    }
}

/// <summary>
/// Every V4 record type the legacy treatment projection covers, and the assembly of a fetched page
/// into legacy <see cref="Treatment"/> shapes. Order is significant: it fixes the order equal-timestamp
/// treatments appear in, which survives the stable sort each read surface finishes with.
/// </summary>
internal static class LegacyTreatmentTables
{
    // DeviceEventType → legacy Nightscout eventType string (reverse of TreatmentTypes.DeviceEventTypeMap)
    private static readonly Dictionary<DeviceEventType, string> DeviceEventTypeToString =
        new()
        {
            [DeviceEventType.SensorStart] = TreatmentTypes.SensorStart,
            [DeviceEventType.SensorChange] = TreatmentTypes.SensorChange,
            [DeviceEventType.SensorStop] = TreatmentTypes.SensorStop,
            [DeviceEventType.SiteChange] = TreatmentTypes.SiteChange,
            [DeviceEventType.InsulinChange] = TreatmentTypes.InsulinChange,
            [DeviceEventType.PumpBatteryChange] = TreatmentTypes.PumpBatteryChange,
            [DeviceEventType.PodChange] = TreatmentTypes.PodChange,
            [DeviceEventType.ReservoirChange] = TreatmentTypes.ReservoirChange,
            [DeviceEventType.CannulaChange] = TreatmentTypes.CannulaChange,
            [DeviceEventType.TransmitterSensorInsert] = TreatmentTypes.TransmitterSensorInsert,
        };

    /// <summary>
    /// Loop override fields with no <see cref="Treatment"/> property, kept and served with their JSON
    /// type. NightscoutKit and LoopFollow read <c>correctionRange</c> as a number array, and LoopFollow
    /// draws an override without one at <c>[targetBottom, targetTop]</c>, which Loop never uploads.
    /// </summary>
    internal static readonly string[] UploadedOverrideFields = ["correctionRange", "remoteAddress"];

    internal static readonly IReadOnlyList<LegacyStateSpanTable> StateSpanTables =
    [
        new(StateSpanCategory.Override,
            s => s.OriginalId != null && EF.Functions.JsonContains(s.MetadataJson!, TreatmentsCollectionJson),
            (s, metadata) => new Treatment
            {
                EventType = "Temporary Override",
                RawTimestamp = TreatmentUploadedTimestamp.OfSpan(metadata)
                    ?? JsonSerializer.SerializeToElement(NightscoutKitTimestamp(s.StartTimestamp)),
                Duration = UploadedDuration(metadata)
                    ?? (metadata.TryReadString("durationType") == "indefinite" ? null : DurationMinutes(s)),
                Reason = metadata.TryReadString("reason"),
                ReasonDisplay = metadata.TryReadString("reasonDisplay"),
                TargetTop = (double?)metadata.TryReadDecimal("targetTop"),
                TargetBottom = (double?)metadata.TryReadDecimal("targetBottom"),
                InsulinNeedsScaleFactor = (double?)metadata.TryReadDecimal("insulinNeedsScaleFactor"),
                DurationType = metadata.TryReadString("durationType"),
                AdditionalProperties = UploadedFields(metadata, UploadedOverrideFields),
            }),
        new(StateSpanCategory.TemporaryTarget,
            s => s.OriginalId != null
                && EF.Functions.JsonExists(s.MetadataJson!, StateSpanMetadataExtensions.UtcOffsetKey),
            (s, metadata) => new Treatment
            {
                EventType = metadata.TryReadString(UploadedEventTypeKey) ?? "Temporary Target",
                Duration = UploadedDuration(metadata)
                    ?? (s.State == nameof(TemporaryTargetState.Cancelled) ? 0 : DurationMinutes(s)),
                TargetTop = (double?)metadata.TryReadDecimal("targetTop"),
                TargetBottom = (double?)metadata.TryReadDecimal("targetBottom"),
                Reason = metadata.TryReadString("reason"),
                Units = metadata.TryReadString("units"),
            }),
        new(StateSpanCategory.Profile,
            s => s.OriginalId != null
                && EF.Functions.JsonExists(s.MetadataJson!, StateSpanMetadataExtensions.UtcOffsetKey),
            // A later switch ends an open switch by supersession; the upload was permanent, and a
            // non-zero duration would read as a temporary switch.
            (s, metadata) => new Treatment
            {
                EventType = TreatmentTypes.ProfileSwitch,
                Duration = s.SupersededById != null ? 0 : DurationMinutes(s),
                Profile = metadata.TryReadString("profileName"),
                ProfileJson = metadata.TryReadString("profileJson"),
                Percentage = (double?)metadata.TryReadDecimal("percentage"),
                Timeshift = (double?)metadata.TryReadDecimal("timeshift"),
                Reason = metadata.TryReadString("reason"),
            }),
    ];

    /// <summary>The treatment ids of every state span <see cref="StateSpanTables"/> serves.</summary>
    internal static IQueryable<string> ServedStateSpanKeys(NocturneDbContext context) =>
        StateSpanTables
            .Select(table => table.Rows(context).Select(s => s.OriginalId!))
            .Aggregate(Queryable.Concat);

    /// <summary>
    /// The notes a served state span's treatment wrote as a Note of its own before
    /// <see cref="NotesKey"/> was kept on the span: one treatment, served once, as the span.
    /// </summary>
    internal static IQueryable<NoteEntity> NotesWrittenBesideServedStateSpans(NocturneDbContext context)
    {
        var keys = ServedStateSpanKeys(context);
        return context.Notes.Where(n => n.LegacyId != null && keys.Contains(n.LegacyId));
    }

    /// <summary>
    /// Every state span <see cref="StateSpanTables"/> serves that starts inside the window, bounds
    /// inclusive, as one tracked-entity query a bulk update can run against.
    /// </summary>
    internal static IQueryable<StateSpanEntity> ServedStateSpansInWindow(
        NocturneDbContext context, DateTime? from, DateTime? to)
    {
        var served = StateSpanTables
            .Select(table => table.InWindow(context, from, to).Select(s => s.Id))
            .Aggregate(Queryable.Concat);
        return context.StateSpans.Where(s => served.Contains(s.Id));
    }

    internal static readonly IReadOnlyList<ILegacyTreatmentTable> All =
    [
        new LegacyTreatmentTable<Bolus, BolusEntity>(
            (repositories, range, ct) => repositories.Boluses.GetAsync(
                from: range.From, to: range.To, device: null, source: null,
                limit: range.Limit, offset: 0, descending: true, nativeOnly: range.NativeOnly, ct: ct),
            r => r.LegacyId,
            c => c.Boluses, RecordType.Bolus, BolusMapper.ToDomainModel,
            (r, _) => ProjectCorrectionBolus(r)),
        new LegacyTreatmentTable<CarbIntake, CarbIntakeEntity>(
            (repositories, range, ct) => repositories.CarbIntakes.GetAsync(
                from: range.From, to: range.To, device: null, source: null,
                limit: range.Limit, offset: 0, descending: true, nativeOnly: range.NativeOnly, ct: ct),
            r => r.LegacyId,
            c => c.CarbIntakes, RecordType.CarbIntake, CarbIntakeMapper.ToDomainModel,
            (r, foods) => ProjectCarbCorrection(r, foods.For(r.Id))),
        new LegacyTreatmentTable<BGCheck, BGCheckEntity>(
            (repositories, range, ct) => repositories.BGChecks.GetAsync(
                from: range.From, to: range.To, device: null, source: null,
                limit: range.Limit, offset: 0, descending: true, nativeOnly: range.NativeOnly, ct: ct),
            r => r.LegacyId,
            c => c.BGChecks, RecordType.BGCheck, BGCheckMapper.ToDomainModel,
            (r, _) => ProjectBgCheck(r)),
        new LegacyTreatmentTable<Note, NoteEntity>(
            (repositories, range, ct) => repositories.Notes.GetAsync(
                from: range.From, to: range.To, device: null, source: null,
                limit: range.Limit, offset: 0, descending: true, nativeOnly: range.NativeOnly, ct: ct),
            r => r.LegacyId,
            c => c.Notes, RecordType.Note, NoteMapper.ToDomainModel,
            (r, _) => ProjectNote(r)),
        new LegacyTreatmentTable<DeviceEvent, DeviceEventEntity>(
            (repositories, range, ct) => repositories.DeviceEvents.GetAsync(
                from: range.From, to: range.To, device: null, source: null,
                limit: range.Limit, offset: 0, descending: true, nativeOnly: range.NativeOnly, ct: ct),
            r => r.LegacyId,
            c => c.DeviceEvents, RecordType.DeviceEvent, DeviceEventMapper.ToDomainModel,
            (r, _) => ProjectDeviceEvent(r)),
        new LegacyTreatmentTable<TempBasal, TempBasalEntity>(
            (repositories, range, ct) => repositories.TempBasals.GetAsync(
                from: range.From, to: range.To, device: null, source: null,
                limit: range.Limit, offset: 0, descending: true, ct: ct),
            r => r.LegacyId,
            c => c.TempBasals, RecordType.TempBasal, TempBasalMapper.ToDomainModel,
            (r, _) => TempBasalToTreatmentMapper.ToTreatment(r)),
        new LegacyTreatmentTable<BolusCalculation, BolusCalculationEntity>(
            (repositories, range, ct) => repositories.BolusCalculations.GetAsync(
                from: range.From, to: range.To, device: null, source: null,
                limit: range.Limit, offset: 0, descending: true, ct: ct),
            r => r.LegacyId,
            c => c.BolusCalculations, RecordType.BolusCalculation, BolusCalculationMapper.ToDomainModel,
            (r, _) => ProjectBolusCalculation(r)),
        .. StateSpanTables,
    ];

    /// <summary>The tables whose records pair into a Meal Bolus.</summary>
    internal static readonly IReadOnlyList<ILegacyTreatmentTable> MealTables =
        [.. All.Where(t => t.RecordType is nameof(Bolus) or nameof(CarbIntake))];

    private const string TreatmentsCollectionJson =
        $$"""{"{{StateSpanMetadataExtensions.CollectionKey}}":"{{StateSpanMetadataExtensions.TreatmentsCollection}}"}""";

    /// <summary>
    /// Metadata key for the <c>duration</c> an override or temporary target was uploaded with, served
    /// verbatim as Nightscout does: an open-ended Loop override is uploaded with no duration and must
    /// be served with none, since NightscoutKit reads any numeric duration as a finite override.
    /// </summary>
    internal const string UploadedDurationKey = "duration";

    /// <summary>
    /// Metadata key for the <c>timestamp</c> a span's treatment was uploaded with, served verbatim:
    /// NightscoutKit drops a treatment without one. See <see cref="TreatmentUploadedTimestamp"/>.
    /// </summary>
    internal const string UploadedTimestampKey = TreatmentUploadedTimestamp.Field;

    /// <summary>
    /// The <c>timestamp</c> served for an override that has no uploaded one (every override written
    /// before <see cref="UploadedTimestampKey"/> was kept): its start, in the fractional ISO 8601 form
    /// NightscoutKit's <c>TimeFormat</c> parses. Only
    /// overrides get one: Loop and Trio are their only uploaders, while AAPS uploads temporary targets
    /// and profile switches with no <c>timestamp</c> and reads that field as a number.
    /// </summary>
    private static string NightscoutKitTimestamp(DateTime start) =>
        DateTime.SpecifyKind(start, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Metadata key for a span treatment's <c>notes</c>.</summary>
    internal const string NotesKey = "notes";

    /// <summary>Metadata key for the <c>syncIdentifier</c> NightscoutKit reads on every treatment.</summary>
    internal const string SyncIdentifierKey = "syncIdentifier";

    /// <summary>Metadata key for the <c>insulinType</c> NightscoutKit reads on every treatment.</summary>
    internal const string InsulinTypeKey = "insulinType";

    private static Dictionary<string, object>? UploadedFields(
        IDictionary<string, object>? metadata, IEnumerable<string> keys)
    {
        if (metadata is null)
            return null;

        var fields = keys
            .Where(key => metadata.TryGetValue(key, out var value) && value is not null)
            .ToDictionary(key => key, key => metadata[key]);
        return fields.Count > 0 ? fields : null;
    }

    /// <summary>Metadata key for a temporary target's <c>eventType</c> when it is not "Temporary Target".</summary>
    internal const string UploadedEventTypeKey = "eventType";

    /// <remarks>Spans written before the key was kept fall back to their stored end.</remarks>
    private static double? UploadedDuration(IDictionary<string, object>? metadata) =>
        (double?)metadata.TryReadDecimal(UploadedDurationKey);

    private static double? DurationMinutes(StateSpanEntity span) =>
        span.EndTimestamp is { } end ? (end - span.StartTimestamp).TotalMinutes : null;

    private static readonly Dictionary<ILegacyTreatmentTable, int> Order =
        All.Select((table, index) => (table, index)).ToDictionary(x => x.table, x => x.index);

    /// <summary>Position in <see cref="All"/>, the tiebreak after the modification stamp.</summary>
    internal static int OrderOf(ILegacyTreatmentTable table) => Order[table];

    /// <summary>
    /// Turns a page of rows into legacy treatments: a bolus and a carb intake sharing a correlation
    /// become one Meal Bolus, and everything else projects through its own table. Pairing only ever
    /// sees the rows it is handed, so a page must be selected before it is assembled — see
    /// <see cref="V4ToLegacyProjectionService.GetProjectedTreatmentsModifiedSinceAsync"/>.
    /// </summary>
    /// <remarks>
    /// <c>spanNotes</c> holds the text of the Note a served span's treatment wrote beside it, by
    /// treatment id, served on the span when the span keeps none itself; see
    /// <see cref="NotesWrittenBesideServedStateSpans"/>.
    /// </remarks>
    internal static List<Treatment> Assemble(
        IReadOnlyList<FetchedRecord> rows,
        CarbFoodIndex foods,
        IReadOnlyDictionary<string, string>? spanNotes = null)
    {
        var treatments = new List<Treatment>();
        var paired = PairMeals(rows, foods, treatments);

        foreach (var row in rows)
        {
            if (paired.Contains(row.Record))
                continue;

            var treatment = row.Table.Project(row.Record, foods);
            if (treatment.Notes is null
                && row.Record is StateSpanEntity { OriginalId: { } key }
                && spanNotes?.GetValueOrDefault(key) is { } notes)
                treatment.Notes = notes;

            treatments.Add(Stamp(treatment, row.Created, row.Modified, row.Deleted));
        }

        return treatments;
    }

    /// <summary>
    /// Pairs boluses and carb intakes by CorrelationId. Under N:M a single correlation may have
    /// multiple boluses and/or multiple carb intakes: the dominant-dose bolus + carb are projected
    /// as the primary Meal Bolus and returned as consumed, and any extras flow through as separate
    /// Correction Bolus / Carb Correction treatments. Ordering by descending Insulin/Carbs picks the
    /// record a human would recognise as the main one; ThenBy(Id) is a deterministic tiebreaker so
    /// same-timestamp, same-dose records don't produce non-deterministic output across requests.
    /// A deleted and a live record never pair: the meal a client knows is deleted only when both
    /// constituents are.
    /// </summary>
    private static HashSet<object> PairMeals(
        IReadOnlyList<FetchedRecord> records,
        CarbFoodIndex foods,
        List<Treatment> treatments
    )
    {
        var paired = new HashSet<object>(ReferenceEqualityComparer.Instance);

        var boluses = Correlated<Bolus>(records);
        var carbs = Correlated<CarbIntake>(records);

        foreach (var key in boluses.Select(g => g.Key).Union(carbs.Select(g => g.Key)))
        {
            var bolus = boluses[key]
                .OrderByDescending(b => b.Record.Insulin)
                .ThenBy(b => b.Record.Id)
                .FirstOrDefault();
            var carb = carbs[key]
                .OrderByDescending(c => c.Record.Carbs)
                .ThenBy(c => c.Record.Id)
                .FirstOrDefault();

            if (bolus.Record is null || carb.Record is null)
                continue;

            paired.Add(bolus.Record);
            paired.Add(carb.Record);

            // The meal carries the newer of the two stamps: stamping the older one leaves the other
            // record above the cursor the consumer derives from this page, which re-serves the same
            // meal on the next request without ever advancing.
            treatments.Add(Stamp(
                ProjectMealBolus(bolus.Record, carb.Record, foods.For(carb.Record.Id)),
                Oldest(bolus.Record.CreatedAt, carb.Record.CreatedAt),
                Newest(bolus.Modified, carb.Modified),
                key.Deleted));
        }

        return paired;
    }

    private static ILookup<(Guid CorrelationId, bool Deleted), (T Record, DateTime Modified)> Correlated<T>(
        IReadOnlyList<FetchedRecord> records
    )
        where T : class, IV4Record =>
        records
            .Select(r => (Record: r.Record as T, r.Modified, r.Deleted))
            .Where(r => r.Record?.CorrelationId is not null)
            .ToLookup(r => (r.Record!.CorrelationId!.Value, r.Deleted), r => (r.Record!, r.Modified));

    private static DateTime Newest(DateTime left, DateTime right) => left > right ? left : right;

    private static DateTime Oldest(DateTime left, DateTime right) => left < right ? left : right;

    private static Treatment Stamp(Treatment treatment, DateTime createdAt, DateTime modifiedAt, bool deleted)
    {
        treatment.SrvCreated = new DateTimeOffset(createdAt, TimeSpan.Zero).ToUnixTimeMilliseconds();
        treatment.SrvModified = new DateTimeOffset(modifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();
        if (deleted)
            treatment.IsValid = false;
        return treatment;
    }

    private static Treatment ProjectMealBolus(Bolus bolus, CarbIntake carb, List<TreatmentFood> foods) =>
        new()
        {
            Id = bolus.Id.ToString(),
            AdditionalProperties = TreatmentClientId.ToTreatment(bolus.AdditionalProperties),
            RawTimestamp = TreatmentUploadedTimestamp.Of(bolus.AdditionalProperties),
            EventType = TreatmentTypes.MealBolus,
            Mills = bolus.Mills,
            Insulin = bolus.Insulin,
            Carbs = carb.Carbs,
            FoodType = DeriveFoodType(foods),
            Fat = DeriveTotalFat(foods),
            Protein = DeriveTotalProtein(foods),
            AbsorptionTime = carb.AbsorptionTime,
            CarbTime = carb.CarbTime.HasValue ? (int?)((int)carb.CarbTime.Value) : null,
            EnteredBy = bolus.Device,
            DataSource = bolus.DataSource,
            SyncIdentifier = bolus.SyncIdentifier,
            InsulinType = bolus.InsulinType,
            UtcOffset = bolus.UtcOffset,
            Automatic = bolus.Automatic,
        };

    private static Treatment ProjectCorrectionBolus(Bolus bolus) =>
        new()
        {
            Id = bolus.Id.ToString(),
            AdditionalProperties = TreatmentClientId.ToTreatment(bolus.AdditionalProperties),
            RawTimestamp = TreatmentUploadedTimestamp.Of(bolus.AdditionalProperties),
            EventType = TreatmentTypes.CorrectionBolus,
            Mills = bolus.Mills,
            Insulin = bolus.Insulin,
            EnteredBy = bolus.Device,
            DataSource = bolus.DataSource,
            SyncIdentifier = bolus.SyncIdentifier,
            InsulinType = bolus.InsulinType,
            UtcOffset = bolus.UtcOffset,
            // Preserve the algorithm/manual distinction (SMBs, auto-boluses): the v4 Bolus.Automatic
            // flag (set alongside Kind=Algorithm during decomposition) maps onto the legacy Nightscout
            // `automatic` field so v1/v3 clients (LoopFollow, Trio import) can tell an SMB from a
            // user-initiated bolus.
            Automatic = bolus.Automatic,
        };

    private static Treatment ProjectCarbCorrection(CarbIntake carb, List<TreatmentFood> foods) =>
        new()
        {
            Id = carb.Id.ToString(),
            AdditionalProperties = TreatmentClientId.ToTreatment(carb.AdditionalProperties),
            RawTimestamp = TreatmentUploadedTimestamp.Of(carb.AdditionalProperties),
            EventType = TreatmentTypes.CarbCorrection,
            Mills = carb.Mills,
            Carbs = carb.Carbs,
            FoodType = DeriveFoodType(foods),
            Fat = DeriveTotalFat(foods),
            Protein = DeriveTotalProtein(foods),
            AbsorptionTime = carb.AbsorptionTime,
            CarbTime = carb.CarbTime.HasValue ? (int?)((int)carb.CarbTime.Value) : null,
            EnteredBy = carb.Device,
            DataSource = carb.DataSource,
            SyncIdentifier = carb.SyncIdentifier,
            UtcOffset = carb.UtcOffset,
        };

    private static string? DeriveFoodType(List<TreatmentFood> foods)
    {
        if (foods.Count == 0) return null;

        var names = foods
            .Select(f => f.FoodName ?? f.Note)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static double? DeriveTotalFat(List<TreatmentFood> foods)
    {
        var sum = foods
            .Where(f => f.FatPerPortion.HasValue && f.Portions > 0)
            .Sum(f => (double)(f.FatPerPortion!.Value * f.Portions));
        return sum > 0 ? sum : null;
    }

    private static double? DeriveTotalProtein(List<TreatmentFood> foods)
    {
        var sum = foods
            .Where(f => f.ProteinPerPortion.HasValue && f.Portions > 0)
            .Sum(f => (double)(f.ProteinPerPortion!.Value * f.Portions));
        return sum > 0 ? sum : null;
    }

    private static Treatment ProjectBgCheck(BGCheck bgCheck) =>
        new()
        {
            Id = bgCheck.Id.ToString(),
            AdditionalProperties = TreatmentClientId.ToTreatment(bgCheck.AdditionalProperties),
            RawTimestamp = TreatmentUploadedTimestamp.Of(bgCheck.AdditionalProperties),
            EventType = TreatmentTypes.BgCheck,
            Mills = bgCheck.Mills,
            Glucose = bgCheck.Glucose,
            Mgdl = bgCheck.Mgdl,
            Mmol = bgCheck.Mmol,
            GlucoseType = bgCheck.GlucoseType?.ToString(),
            Units = bgCheck.Units == GlucoseUnit.Mmol ? "mmol" : "mg/dl",
            EnteredBy = bgCheck.Device,
            DataSource = bgCheck.DataSource,
            SyncIdentifier = bgCheck.SyncIdentifier,
            UtcOffset = bgCheck.UtcOffset,
        };

    private static Treatment ProjectNote(Note note) =>
        new()
        {
            Id = note.Id.ToString(),
            AdditionalProperties = TreatmentClientId.ToTreatment(note.AdditionalProperties),
            RawTimestamp = TreatmentUploadedTimestamp.Of(note.AdditionalProperties),
            EventType = note.EventType ?? "Note",
            Mills = note.Mills,
            Notes = note.Text,
            IsAnnouncement = note.IsAnnouncement,
            EnteredBy = note.Device,
            DataSource = note.DataSource,
            SyncIdentifier = note.SyncIdentifier,
            UtcOffset = note.UtcOffset,
        };

    private static Treatment ProjectDeviceEvent(DeviceEvent deviceEvent)
    {
        DeviceEventTypeToString.TryGetValue(deviceEvent.EventType, out var eventTypeString);
        return new Treatment
        {
            Id = deviceEvent.Id.ToString(),
            AdditionalProperties = TreatmentClientId.ToTreatment(deviceEvent.AdditionalProperties),
            RawTimestamp = TreatmentUploadedTimestamp.Of(deviceEvent.AdditionalProperties),
            EventType = eventTypeString ?? deviceEvent.EventType.ToString(),
            Mills = deviceEvent.Mills,
            Notes = deviceEvent.Notes,
            EnteredBy = deviceEvent.Device,
            DataSource = deviceEvent.DataSource,
            SyncIdentifier = deviceEvent.SyncIdentifier,
            UtcOffset = deviceEvent.UtcOffset,
        };
    }

    private static Treatment ProjectBolusCalculation(BolusCalculation bc) =>
        new()
        {
            Id = bc.Id.ToString(),
            AdditionalProperties = TreatmentClientId.ToTreatment(bc.AdditionalProperties),
            RawTimestamp = TreatmentUploadedTimestamp.Of(bc.AdditionalProperties),
            EventType = "Bolus Wizard",
            Mills = bc.Mills,
            BloodGlucoseInput = bc.BloodGlucoseInput,
            BloodGlucoseInputSource = bc.BloodGlucoseInputSource,
            Carbs = bc.CarbInput,
            InsulinOnBoard = bc.InsulinOnBoard,
            InsulinRecommendationForCorrection = bc.InsulinRecommendation,
            CR = bc.CarbRatio,
            CalculationType = bc.CalculationType.HasValue
                ? (Nocturne.Core.Models.CalculationType)(int)bc.CalculationType.Value
                : null,
            InsulinRecommendationForCarbs = bc.InsulinRecommendationForCarbs,
            InsulinProgrammed = bc.InsulinProgrammed,
            EnteredInsulin = bc.EnteredInsulin,
            SplitNow = bc.SplitNow,
            SplitExt = bc.SplitExt,
            PreBolus = bc.PreBolus,
            EnteredBy = bc.Device,
            DataSource = bc.DataSource,
            UtcOffset = bc.UtcOffset,
        };
}
