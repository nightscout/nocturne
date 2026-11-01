using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>The V4 repositories the tables of <see cref="LegacyTreatmentTables"/> read and write through.</summary>
internal sealed record LegacyTreatmentRepositories(
    IBolusRepository Boluses,
    ICarbIntakeRepository CarbIntakes,
    IBGCheckRepository BGChecks,
    INoteRepository Notes,
    IDeviceEventRepository DeviceEvents,
    ITempBasalRepository TempBasals,
    IBolusCalculationRepository BolusCalculations,
    IProfileActivationRepository ProfileActivations
);

/// <summary>
/// The time window, page size and direction of one time-range projection read.
/// </summary>
internal readonly record struct LegacyTreatmentRange(
    DateTime? From,
    DateTime? To,
    int Limit,
    bool Descending
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

    /// <summary>The model type of the records this table reads, whose writes change the treatments it serves.</summary>
    Type Model { get; }

    /// <summary>The treatments this table serves that start inside the window, bounds inclusive.</summary>
    Task<long> CountAsync(
        LegacyTreatmentRepositories repositories, NocturneDbContext context, DateTime? from, DateTime? to,
        ILogger logger, CancellationToken ct);

    /// <summary>
    /// A page of records within the requested window: the newest <see cref="LegacyTreatmentRange.Limit"/>
    /// newest first, or the oldest oldest first when <see cref="LegacyTreatmentRange.Descending"/> is false.
    /// </summary>
    Task<IReadOnlyList<FetchedRecord>> InRangeAsync(
        LegacyTreatmentRepositories repositories,
        NocturneDbContext context,
        LegacyTreatmentRange range,
        CancellationToken ct
    );

    /// <summary>
    /// The records <see cref="InRangeAsync"/> reads that start exactly at one of
    /// <paramref name="instants"/>: at each instant, the first <paramref name="limit"/> in the order a
    /// newest-first <see cref="InRangeAsync"/> reads them, as a read of that instant alone returns them.
    /// </summary>
    Task<IReadOnlyList<FetchedRecord>> AtAsync(
        NocturneDbContext context,
        IReadOnlyCollection<DateTime> instants,
        int limit,
        CancellationToken ct
    );

    /// <summary>The query <see cref="AtAsync"/> runs.</summary>
    IQueryable AtQuery(NocturneDbContext context, IReadOnlyCollection<DateTime> instants);

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

    /// <summary>
    /// The live records written under one of <paramref name="treatmentKeys"/>, the treatment ids
    /// <see cref="LegacyTreatmentTables.ServedKey"/> reads, non-primary copies left out as
    /// <see cref="ModifiedSinceAsync"/> leaves them out.
    /// </summary>
    Task<IReadOnlyList<FetchedRecord>> LiveByTreatmentKeyAsync(
        NocturneDbContext context,
        IReadOnlyCollection<string> treatmentKeys,
        CancellationToken ct
    );

    /// <summary>Projects a record this table owns that is not part of a meal pairing.</summary>
    Treatment Project(object record, CarbFoodIndex foods);

    /// <summary>The treatment ids this table's live records were written under.</summary>
    IQueryable<string> TreatmentKeys(NocturneDbContext context);
}

/// <summary>
/// A <see cref="ILegacyTreatmentTable"/> whose stored records a client-sent treatment id names by
/// <see cref="LegacyRecordKey"/>: every table but the state spans, which
/// <see cref="LegacyTreatmentStore"/> reaches in any form they are served in.
/// </summary>
/// <seealso cref="LegacyTreatmentTables.ByServedPrecedence"/>
internal interface ILegacyKeyedTreatmentTable : ILegacyTreatmentTable
{
    LegacyKeyedRecords Records { get; }
}

/// <summary>The stored records of one <see cref="ILegacyKeyedTreatmentTable"/>, through its repository.</summary>
internal abstract class LegacyKeyedRecords
{
    /// <summary>The record <paramref name="key"/> names in this table, or none.</summary>
    internal abstract Task<IV4Record?> FindAsync(LegacyTreatmentRepositories repositories, LegacyRecordKey key, CancellationToken ct);

    /// <summary>Whether the user deleted the record this table held under <paramref name="id"/>.</summary>
    internal abstract Task<bool> IsDeletedByUserAsync(LegacyTreatmentRepositories repositories, string id, CancellationToken ct);

    internal abstract Task DeleteAsync(LegacyTreatmentRepositories repositories, IV4Record record, CancellationToken ct);

    internal abstract Task UpdateAsync(LegacyTreatmentRepositories repositories, IV4Record record, CancellationToken ct);

    /// <summary>
    /// This table's half of the Meal Bolus correlated by each of <paramref name="correlationIds"/> that
    /// has one, in one read; a table outside <see cref="LegacyTreatmentTables.MealTables"/> holds none.
    /// </summary>
    internal abstract Task<IReadOnlyDictionary<Guid, IV4Record>> MealHalvesAsync(
        LegacyTreatmentRepositories repositories, IReadOnlyCollection<Guid> correlationIds, CancellationToken ct);

    /// <summary>
    /// The records any of <paramref name="objectIds"/> could name by a key other than
    /// <see cref="LegacyRecordKey.LegacyIdHash"/>, in one read, for <see cref="Pick"/> to resolve.
    /// </summary>
    internal abstract Task<IReadOnlyList<IV4Record>> FindServedAsync(
        LegacyTreatmentRepositories repositories, IReadOnlyCollection<string> objectIds, CancellationToken ct);

    /// <summary>The record of <paramref name="found"/> <see cref="FindAsync"/> resolves <paramref name="key"/> to, or none.</summary>
    internal abstract IV4Record? Pick(IReadOnlyList<IV4Record> found, LegacyRecordKey key);
}

/// <summary>
/// The stored records of one <see cref="ILegacyKeyedTreatmentTable"/> through its repository; a
/// <c>served</c> filter leaves out a record found that is not served as a treatment.
/// </summary>
internal sealed class LegacyKeyedRecords<TRecord>(
    Func<LegacyTreatmentRepositories, ILegacyKeyedRepository<TRecord>> repository,
    Func<LegacyTreatmentRepositories, IReadOnlyCollection<Guid>, CancellationToken, Task<IEnumerable<TRecord>>>? correlated = null,
    Func<TRecord, bool>? served = null
) : LegacyKeyedRecords
    where TRecord : class, IV4Record
{
    internal override async Task<IV4Record?> FindAsync(
        LegacyTreatmentRepositories repositories, LegacyRecordKey key, CancellationToken ct) =>
        await key.FindInAsync(repository(repositories), ct) is { } record && (served?.Invoke(record) ?? true)
            ? record
            : null;

    internal override Task<bool> IsDeletedByUserAsync(
        LegacyTreatmentRepositories repositories, string id, CancellationToken ct) =>
        repository(repositories).IsDeletedByUserAsync(id, ct);

    internal override Task DeleteAsync(LegacyTreatmentRepositories repositories, IV4Record record, CancellationToken ct) =>
        repository(repositories).DeleteAsync(record.Id, WriteOrigin.Live, ct);

    internal override Task UpdateAsync(LegacyTreatmentRepositories repositories, IV4Record record, CancellationToken ct) =>
        repository(repositories).UpdateAsync(record.Id, (TRecord)record, WriteOrigin.Live, ct);

    internal override async Task<IReadOnlyDictionary<Guid, IV4Record>> MealHalvesAsync(
        LegacyTreatmentRepositories repositories, IReadOnlyCollection<Guid> correlationIds, CancellationToken ct)
    {
        if (correlated is null || correlationIds.Count == 0)
            return new Dictionary<Guid, IV4Record>();

        return (await correlated(repositories, correlationIds, ct))
            .Where(r => r.CorrelationId is not null)
            .GroupBy(r => r.CorrelationId!.Value)
            .Select(g => (g.Key, Half: LegacyTreatmentTables.MealHalf(g)))
            .Where(h => h.Half is not null)
            .ToDictionary(h => h.Key, h => (IV4Record)h.Half!);
    }

    internal override async Task<IReadOnlyList<IV4Record>> FindServedAsync(
        LegacyTreatmentRepositories repositories, IReadOnlyCollection<string> objectIds, CancellationToken ct) =>
        [.. await repository(repositories).GetByServedIdsAsync(objectIds, ct)];

    internal override IV4Record? Pick(IReadOnlyList<IV4Record> found, LegacyRecordKey key) =>
        key.Resolve(found) is TRecord record && (served?.Invoke(record) ?? true) ? record : null;
}

/// <inheritdoc cref="ILegacyTreatmentTable"/>
internal sealed class LegacyTreatmentTable<TRecord, TEntity>(
    Func<LegacyTreatmentRepositories, ILegacyKeyedRepository<TRecord>> repository,
    Func<NocturneDbContext, IQueryable<TEntity>> table,
    Nocturne.Core.Models.RecordType dedupType,
    Func<TEntity, TRecord> toRecord,
    Func<TRecord, CarbFoodIndex, Treatment> project,
    Func<NocturneDbContext, DateTime?, DateTime?, CancellationToken, Task<long>>? count = null,
    Func<LegacyTreatmentRepositories, IReadOnlyCollection<Guid>, CancellationToken, Task<IEnumerable<TRecord>>>? correlated = null
) : ILegacyKeyedTreatmentTable
    where TRecord : class, IV4Record
    where TEntity : class, ISystemTimestamped, IIdentified, ISoftDeletable, IObservationTimestamped
{
    /// <inheritdoc />
    public string RecordType { get; } = typeof(TRecord).Name;

    /// <inheritdoc />
    public Type Model => typeof(TRecord);

    /// <inheritdoc />
    public async Task<long> CountAsync(
        LegacyTreatmentRepositories repositories, NocturneDbContext context, DateTime? from, DateTime? to,
        ILogger logger, CancellationToken ct) =>
        count is null
            ? await repository(repositories).CountAsync(from, to, ct)
            : await count(context, from, to, ct);

    /// <inheritdoc />
    public LegacyKeyedRecords Records { get; } = new LegacyKeyedRecords<TRecord>(repository, correlated);

    /// <inheritdoc />
    public IQueryable<string> TreatmentKeys(NocturneDbContext context) =>
        table(context)
            .Where(e => EF.Property<string?>(e, nameof(IV4Record.LegacyId)) != null)
            .Select(e => EF.Property<string>(e, nameof(IV4Record.LegacyId)));

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> InRangeAsync(
        LegacyTreatmentRepositories repositories,
        NocturneDbContext context,
        LegacyTreatmentRange range,
        CancellationToken ct
    )
    {
        var records = await repository(repositories).GetAsync(
            range.From, range.To, device: null, source: null, range.Limit, offset: 0, range.Descending, ct);
        return records.Select(Fetch).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the rows the repository's window read shows, as <see cref="LiveAsync"/> does, in the
    /// order it reads them.
    /// </remarks>
    public async Task<IReadOnlyList<FetchedRecord>> AtAsync(
        NocturneDbContext context,
        IReadOnlyCollection<DateTime> instants,
        int limit,
        CancellationToken ct
    )
    {
        var entities = await At(context, instants).ToListAsync(ct);

        return LegacyTreatmentTables.FirstAtEach(entities, e => e.Timestamp, limit)
            .Select(e => Fetch(toRecord(e))).ToList();
    }

    /// <inheritdoc />
    IQueryable ILegacyTreatmentTable.AtQuery(NocturneDbContext context, IReadOnlyCollection<DateTime> instants) =>
        At(context, instants);

    private IQueryable<TEntity> At(NocturneDbContext context, IReadOnlyCollection<DateTime> instants)
    {
        var at = instants.ToList();
        return table(context)
            .AsNoTracking()
            .ExcludeNonPrimary(context, dedupType)
            .Where(e => at.Contains(e.Timestamp))
            .InObservationOrder(descending: true);
    }

    internal FetchedRecord Fetch(TRecord record) =>
        new(this, record, record.Id, record.CreatedAt, record.ModifiedAt);

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
            context,
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
                this, x.Record, x.Record.Id, x.Record.CreatedAt, x.SysUpdatedAt, x.Deleted))
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
        return await LiveAsync(
            context, e => ids.Contains(EF.Property<Guid?>(e, nameof(IV4Record.CorrelationId))), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> LiveByTreatmentKeyAsync(
        NocturneDbContext context,
        IReadOnlyCollection<string> treatmentKeys,
        CancellationToken ct
    )
    {
        var keys = treatmentKeys.ToList();
        return await LiveAsync(
            context, e => keys.Contains(EF.Property<string?>(e, nameof(IV4Record.LegacyId))!), ct);
    }

    private async Task<IReadOnlyList<FetchedRecord>> LiveAsync(
        NocturneDbContext context, Expression<Func<TEntity, bool>> filter, CancellationToken ct)
    {
        var entities = await table(context)
            .AsNoTracking()
            .ExcludeNonPrimary(context, dedupType)
            .Where(filter)
            .ToListAsync(ct);

        return entities
            .Select(e => (Record: toRecord(e), e.SysUpdatedAt))
            .Select(x => new FetchedRecord(
                this, x.Record, x.Record.Id, x.Record.CreatedAt, x.SysUpdatedAt))
            .ToList();
    }

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

    /// <inheritdoc />
    public Type Model => typeof(StateSpan);

    internal StateSpanCategory Category => category;

    /// <inheritdoc />
    public Task<long> CountAsync(
        LegacyTreatmentRepositories repositories, NocturneDbContext context, DateTime? from, DateTime? to,
        ILogger logger, CancellationToken ct) => LegacyTreatmentTables.GuardedAsync(this, logger, () => InWindow(context, from, to).LongCountAsync(ct), 0L);

    internal IQueryable<StateSpanEntity> Rows(NocturneDbContext context) =>
        Served(context.StateSpans.AsNoTracking());

    /// <inheritdoc />
    public IQueryable<string> TreatmentKeys(NocturneDbContext context) => Rows(context).Select(s => s.OriginalId!);

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
    public async Task<IReadOnlyList<FetchedRecord>> InRangeAsync(
        LegacyTreatmentRepositories repositories,
        NocturneDbContext context,
        LegacyTreatmentRange range,
        CancellationToken ct
    )
    {
        var spans = await InStartOrder(InWindow(context, range.From, range.To), range.Descending)
            .Take(range.Limit)
            .ToListAsync(ct);

        return spans.Select(Fetched).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> AtAsync(
        NocturneDbContext context,
        IReadOnlyCollection<DateTime> instants,
        int limit,
        CancellationToken ct
    )
    {
        var spans = await At(context, instants).ToListAsync(ct);

        return LegacyTreatmentTables.FirstAtEach(spans, s => s.StartTimestamp, limit).Select(Fetched).ToList();
    }

    /// <inheritdoc />
    IQueryable ILegacyTreatmentTable.AtQuery(NocturneDbContext context, IReadOnlyCollection<DateTime> instants) =>
        At(context, instants);

    private IQueryable<StateSpanEntity> At(NocturneDbContext context, IReadOnlyCollection<DateTime> instants)
    {
        var at = instants.ToList();
        return InStartOrder(Rows(context).Where(s => at.Contains(s.StartTimestamp)), descending: true);
    }

    private static IOrderedQueryable<StateSpanEntity> InStartOrder(IQueryable<StateSpanEntity> spans, bool descending) =>
        (descending ? spans.OrderByDescending(s => s.StartTimestamp) : spans.OrderBy(s => s.StartTimestamp))
            .ThenBy(s => s.Id);

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
            context,
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
    public async Task<IReadOnlyList<FetchedRecord>> LiveByTreatmentKeyAsync(
        NocturneDbContext context,
        IReadOnlyCollection<string> treatmentKeys,
        CancellationToken ct
    )
    {
        var keys = treatmentKeys.ToList();
        var spans = await Rows(context).Where(s => keys.Contains(s.OriginalId!)).ToListAsync(ct);
        return spans.Select(Fetched).ToList();
    }

    /// <inheritdoc />
    public Treatment Project(object record, CarbFoodIndex foods)
    {
        var span = (StateSpanEntity)record;
        var metadata = StateSpanMapper.ToDomainModel(span).Metadata;

        var treatment = project(span, metadata);
        treatment.Id = span.Id.ToString();
        treatment.Mills = span.StartTimestamp.ToMills();
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
/// The <see cref="ProfileActivation"/>s <see cref="TreatmentDecomposer"/> wrote from a legacy
/// treatment, read back as that treatment under the activation's own id. A connector's activations
/// carry no legacy id and are not served, as the Profile spans they replace were not.
/// </summary>
internal sealed class LegacyProfileActivationTable : ILegacyKeyedTreatmentTable
{
    /// <inheritdoc />
    public string RecordType => nameof(ProfileActivation);

    /// <inheritdoc />
    public Type Model => typeof(ProfileActivation);

    /// <inheritdoc />
    public Task<long> CountAsync(
        LegacyTreatmentRepositories repositories, NocturneDbContext context, DateTime? from, DateTime? to,
        ILogger logger, CancellationToken ct) => LegacyTreatmentTables.GuardedAsync(this, logger, () => InWindow(context, from, to).LongCountAsync(ct), 0L);

    /// <inheritdoc />
    /// <remarks>Only an activation <see cref="Served"/> as a treatment is found.</remarks>
    public LegacyKeyedRecords Records { get; } = new LegacyKeyedRecords<ProfileActivation>(
        repositories => repositories.ProfileActivations, served: activation => activation.LegacyId is { Length: > 0 });

    /// <inheritdoc />
    public IQueryable<string> TreatmentKeys(NocturneDbContext context) => Rows(context).Select(a => a.LegacyId!);

    internal static IQueryable<ProfileActivationEntity> Served(IQueryable<ProfileActivationEntity> rows) =>
        rows.Where(a => a.LegacyId != null);

    internal static IQueryable<ProfileActivationEntity> Rows(NocturneDbContext context) =>
        Served(context.ProfileActivations.AsNoTracking());

    /// <summary>Activations starting inside the window, bounds inclusive.</summary>
    internal static IQueryable<ProfileActivationEntity> InWindow(NocturneDbContext context, DateTime? from, DateTime? to)
    {
        var rows = Rows(context);
        if (from is { } lower)
            rows = rows.Where(a => a.Timestamp >= lower);
        if (to is { } upper)
            rows = rows.Where(a => a.Timestamp <= upper);
        return rows;
    }

    private FetchedRecord Fetched(ProfileActivationEntity entity) =>
        new(this, ProfileActivationMapper.ToDomainModel(entity), entity.Id, entity.SysCreatedAt, entity.SysUpdatedAt,
            entity.DeletedAt is not null);

    private const int LatestSwitchPageSize = 50;

    // Bounds the step over activations served as something else; Nightscout's read has no cap.
    private const int LatestSwitchMaxPages = 20;

    /// <summary>
    /// The newest activation in the window that serves as a Profile Switch of zero duration, read
    /// in pages off <c>ix_profile_activations_tenant_start_timestamp</c> so an activation served as
    /// something else, or with a duration, is stepped over without reading the whole window.
    /// </summary>
    internal async Task<IReadOnlyList<FetchedRecord>> LatestSwitchAsync(
        NocturneDbContext context, DateTime from, DateTime to, CancellationToken ct)
    {
        for (var page = 0; page < LatestSwitchMaxPages; page++)
        {
            var rows = await InStartOrder(InWindow(context, from, to), descending: true)
                .Skip(page * LatestSwitchPageSize)
                .Take(LatestSwitchPageSize)
                .ToListAsync(ct);

            foreach (var row in rows.Select(Fetched))
            {
                var treatment = LegacyTreatmentTables.ProjectProfileActivation((ProfileActivation)row.Record);
                if (treatment.EventType == TreatmentTypes.ProfileSwitch && treatment.Duration == 0)
                    return [row];
            }

            if (rows.Count < LatestSwitchPageSize)
                break;
        }

        return [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> InRangeAsync(
        LegacyTreatmentRepositories repositories,
        NocturneDbContext context,
        LegacyTreatmentRange range,
        CancellationToken ct
    )
    {
        var rows = await InStartOrder(InWindow(context, range.From, range.To), range.Descending)
            .Take(range.Limit)
            .ToListAsync(ct);

        return rows.Select(Fetched).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> AtAsync(
        NocturneDbContext context,
        IReadOnlyCollection<DateTime> instants,
        int limit,
        CancellationToken ct
    )
    {
        var rows = await At(context, instants).ToListAsync(ct);

        return LegacyTreatmentTables.FirstAtEach(rows, a => a.Timestamp, limit).Select(Fetched).ToList();
    }

    /// <inheritdoc />
    IQueryable ILegacyTreatmentTable.AtQuery(NocturneDbContext context, IReadOnlyCollection<DateTime> instants) =>
        At(context, instants);

    private static IQueryable<ProfileActivationEntity> At(
        NocturneDbContext context, IReadOnlyCollection<DateTime> instants)
    {
        var at = instants.ToList();
        return InStartOrder(Rows(context).Where(a => at.Contains(a.Timestamp)), descending: true);
    }

    private static IOrderedQueryable<ProfileActivationEntity> InStartOrder(
        IQueryable<ProfileActivationEntity> rows, bool descending) =>
        (descending ? rows.OrderByDescending(a => a.Timestamp) : rows.OrderBy(a => a.Timestamp))
            .ThenBy(a => a.Id);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> ModifiedSinceAsync(
        NocturneDbContext context,
        long cursorMills,
        int limit,
        ILogger logger,
        CancellationToken ct
    )
    {
        var rows = await HistoryPage.GetAsync(
            context,
            Served(context.ProfileActivations.IncludingDeleted().AsNoTracking()),
            a => a.SysUpdatedAt, a => a.Id, cursorMills, limit, logger, RecordType, ct);

        return rows.Select(Fetched).ToList();
    }

    /// <inheritdoc />
    /// <remarks>An activation is never a meal's partner.</remarks>
    public Task<IReadOnlyList<FetchedRecord>> LiveByCorrelationAsync(
        NocturneDbContext context,
        IReadOnlyCollection<Guid> correlationIds,
        CancellationToken ct
    ) => Task.FromResult<IReadOnlyList<FetchedRecord>>([]);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedRecord>> LiveByTreatmentKeyAsync(
        NocturneDbContext context,
        IReadOnlyCollection<string> treatmentKeys,
        CancellationToken ct
    )
    {
        var keys = treatmentKeys.ToList();
        var rows = await Rows(context).Where(a => keys.Contains(a.LegacyId!)).ToListAsync(ct);
        return rows.Select(Fetched).ToList();
    }

    /// <inheritdoc />
    public Treatment Project(object record, CarbFoodIndex foods) =>
        LegacyTreatmentTables.ProjectProfileActivation((ProfileActivation)record);
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
            [DeviceEventType.PodActivated] = TreatmentTypes.PodActivated,
            [DeviceEventType.PodDeactivated] = TreatmentTypes.PodDeactivated,
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
    ];

    internal static readonly LegacyProfileActivationTable ProfileActivationTable = new();

    /// <summary>Whether a state span the decomposer wrote is read back as the treatment, under the span's id.</summary>
    internal static bool ServesStateSpan(StateSpan span) =>
        span.OriginalId is not null && StateSpanTables.Any(table => table.Category == span.Category);

    /// <summary>The treatment ids every live record other than a Note was written under.</summary>
    private static IQueryable<string> TreatmentKeysOfRecords(NocturneDbContext context) =>
        All.Where(table => table.Model != typeof(Note))
            .Select(table => table.TreatmentKeys(context))
            .Aggregate(Queryable.Concat);

    /// <summary>
    /// The Notes a treatment's <c>notes</c> wrote beside the record it is served as, which serves
    /// them as its own <c>notes</c> (<see cref="Assemble"/>): one treatment, served once. A state
    /// span's or activation's treatment wrote one only before <see cref="NotesKey"/> was kept on it.
    /// </summary>
    internal static IQueryable<NoteEntity> NotesServedWithAnotherRecord(NocturneDbContext context)
    {
        var keys = TreatmentKeysOfRecords(context);
        return context.Notes.Where(n => n.LegacyId != null && keys.Contains(n.LegacyId));
    }

    /// <summary>
    /// The first <paramref name="limit"/> of <paramref name="newestFirst"/> at each instant, in the order
    /// it was read.
    /// </summary>
    /// <remarks>
    /// The limit is applied here, not in SQL: a per-instant limit in SQL is a correlated subquery that
    /// SQLite cannot run, or a <c>ROW_NUMBER()</c> window that PostgreSQL runs over the tenant's whole
    /// table. The read is bounded by the rows that share exactly the instants asked for, which is few.
    /// </remarks>
    internal static IEnumerable<TRow> FirstAtEach<TRow>(
        IEnumerable<TRow> newestFirst, Func<TRow, DateTime> timestamp, int limit) =>
        newestFirst.GroupBy(timestamp).SelectMany(atInstant => atInstant.Take(limit));

    /// <summary>
    /// Every state span <see cref="StateSpanTables"/> serves that starts inside the window, bounds
    /// inclusive, as one tracked-entity query a bulk update can run against.
    /// </summary>
    internal static IQueryable<StateSpanEntity> ServedStateSpansInWindow(
        NocturneDbContext context, DateTime? from, DateTime? to) =>
        TrackedSpans(context, table => table.InWindow(context, from, to));

    /// <summary>
    /// Every state span <see cref="StateSpanTables"/> serves that <paramref name="source"/> wrote,
    /// as one tracked-entity query a bulk update can run against.
    /// </summary>
    internal static IQueryable<StateSpanEntity> ServedStateSpansFromSource(NocturneDbContext context, string source) =>
        TrackedSpans(context, table => table.Rows(context).Where(s => s.Source == source));

    private static IQueryable<StateSpanEntity> TrackedSpans(
        NocturneDbContext context, Func<LegacyStateSpanTable, IQueryable<StateSpanEntity>> rows)
    {
        var served = StateSpanTables
            .Select(table => rows(table).Select(s => s.Id))
            .Aggregate(Queryable.Concat);
        return context.StateSpans.Where(s => served.Contains(s.Id));
    }

    /// <summary>
    /// The newest record serving as <paramref name="eventType"/> in the window, bounds inclusive, or
    /// none: a device event off <c>ix_device_events_tenant_event_type_timestamp</c>, or a zero-duration
    /// Profile Switch off <see cref="LegacyProfileActivationTable.LatestSwitchAsync"/>.
    /// </summary>
    internal static async Task<IReadOnlyList<FetchedRecord>> LatestOfTypeAsync(
        LegacyTreatmentRepositories repositories,
        NocturneDbContext context,
        string eventType,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        if (eventType == TreatmentTypes.ProfileSwitch)
            return await ProfileActivationTable.LatestSwitchAsync(context, from, to, ct);

        if (!TreatmentTypes.DeviceEventTypeMap.TryGetValue(eventType, out var type))
            return [];

        var latest = await repositories.DeviceEvents.GetLatestByEventTypeAsync(type, to, ct);
        if (latest is null || latest.Timestamp < from)
            return [];

        return [DeviceEventTable.Fetch(latest)];
    }

    private static readonly LegacyTreatmentTable<DeviceEvent, DeviceEventEntity> DeviceEventTable = new(
        repositories => repositories.DeviceEvents,
        c => c.DeviceEvents, RecordType.DeviceEvent, DeviceEventMapper.ToDomainModel,
        (r, _) => ProjectDeviceEvent(r));

    internal static readonly IReadOnlyList<ILegacyTreatmentTable> All =
    [
        new LegacyTreatmentTable<Bolus, BolusEntity>(
            repositories => repositories.Boluses,
            c => c.Boluses, RecordType.Bolus, BolusMapper.ToDomainModel,
            (r, _) => ProjectCorrectionBolus(r),
            correlated: (repositories, ids, ct) => repositories.Boluses.GetByCorrelationIdsAsync(ids, ct)),
        new LegacyTreatmentTable<CarbIntake, CarbIntakeEntity>(
            repositories => repositories.CarbIntakes,
            c => c.CarbIntakes, RecordType.CarbIntake, CarbIntakeMapper.ToDomainModel,
            (r, foods) => ProjectCarbCorrection(r, foods.For(r.Id)),
            CountCarbIntakesServedApartAsync,
            (repositories, ids, ct) => repositories.CarbIntakes.GetByCorrelationIdsAsync(ids, ct)),
        new LegacyTreatmentTable<BGCheck, BGCheckEntity>(
            repositories => repositories.BGChecks,
            c => c.BGChecks, RecordType.BGCheck, BGCheckMapper.ToDomainModel,
            (r, _) => ProjectBgCheck(r)),
        new LegacyTreatmentTable<Note, NoteEntity>(
            repositories => repositories.Notes,
            c => c.Notes, RecordType.Note, NoteMapper.ToDomainModel,
            (r, _) => ProjectNote(r),
            CountNotesServedApartAsync),
        DeviceEventTable,
        new LegacyTreatmentTable<TempBasal, TempBasalEntity>(
            repositories => repositories.TempBasals,
            c => c.TempBasals, RecordType.TempBasal, TempBasalMapper.ToDomainModel,
            (r, _) => TempBasalToTreatmentMapper.ToTreatment(r)),
        new LegacyTreatmentTable<BolusCalculation, BolusCalculationEntity>(
            repositories => repositories.BolusCalculations,
            c => c.BolusCalculations, RecordType.BolusCalculation, BolusCalculationMapper.ToDomainModel,
            (r, _) => ProjectBolusCalculation(r)),
        .. StateSpanTables,
        ProfileActivationTable,
    ];

    /// <summary>
    /// The keyed tables in the order a treatment id is resolved and a decomposed treatment is named
    /// by: a temp basal first, a bolus before the carb intake it heads a Meal Bolus with, and a Note
    /// last, since it is only its own treatment when nothing else was written under its id.
    /// </summary>
    internal static readonly IReadOnlyList<ILegacyKeyedTreatmentTable> ByServedPrecedence =
    [
        .. new[]
        {
            typeof(TempBasal), typeof(Bolus), typeof(CarbIntake), typeof(BGCheck), typeof(DeviceEvent),
            typeof(BolusCalculation), typeof(ProfileActivation), typeof(Note),
        }.Select(model => All.OfType<ILegacyKeyedTreatmentTable>().Single(table => table.Model == model)),
    ];

    /// <summary>The keyed table of <typeparamref name="TRecord"/>.</summary>
    internal static ILegacyKeyedTreatmentTable KeyedTableOf<TRecord>() where TRecord : IV4Record =>
        ByServedPrecedence.Single(table => table.Model == typeof(TRecord));

    /// <summary>
    /// The Notes served as treatments of their own in the window, bounds inclusive: every Note but
    /// those <see cref="NotesServedWithAnotherRecord"/>.
    /// </summary>
    private static async Task<long> CountNotesServedApartAsync(
        NocturneDbContext context, DateTime? from, DateTime? to, CancellationToken ct) =>
        await InWindow(context, context.Notes, RecordType.Note, from, to).LongCountAsync(ct)
        - await InWindow(context, NotesServedWithAnotherRecord(context), RecordType.Note, from, to).LongCountAsync(ct);

    /// <summary>
    /// The carb intakes served as treatments of their own in the window, bounds inclusive: every
    /// carb intake but the one <see cref="PairMeals"/> serves as the half of each Meal Bolus. A
    /// correlation pairs once, when the window holds a bolus and a carb intake of it, so a pair split
    /// by the window edge counts as whichever half is inside and a further carb intake of the
    /// correlation counts on its own.
    /// </summary>
    private static async Task<long> CountCarbIntakesServedApartAsync(
        NocturneDbContext context, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var carbs = InWindow(context, context.CarbIntakes, RecordType.CarbIntake, from, to);
        var boluses = InWindow(context, context.Boluses, RecordType.Bolus, from, to);
        var pairedMeals = carbs
            .Where(c => c.CorrelationId != null && boluses.Any(b => b.CorrelationId == c.CorrelationId))
            .Select(c => c.CorrelationId)
            .Distinct();

        return await carbs.LongCountAsync(ct) - await pairedMeals.LongCountAsync(ct);
    }

    private static IQueryable<TEntity> InWindow<TEntity>(
        NocturneDbContext context, IQueryable<TEntity> rows, RecordType recordType, DateTime? from, DateTime? to)
        where TEntity : V4TimeSeriesEntityBase
    {
        rows = rows.AsNoTracking().ExcludeNonPrimary(context, recordType);
        if (from is { } lower)
            rows = rows.Where(r => r.Timestamp >= lower);
        if (to is { } upper)
            rows = rows.Where(r => r.Timestamp <= upper);
        return rows;
    }

    /// <summary>
    /// A <see cref="ProfileActivation"/> as the Profile Switch, or AAPS effective switch Note, it was
    /// uploaded as, from the span metadata <see cref="ProfileActivationLegacyFields.ToSpanMetadata"/>
    /// rebuilds.
    /// </summary>
    /// <remarks>
    /// A permanent switch a later one ended is served with a duration of zero, never the length it ran:
    /// a non-zero duration would read as a temporary switch. AAPS reads a v3 switch's length from
    /// <c>durationInMilliseconds</c> before <c>duration</c> (<c>TreatmentMapper.kt</c>), so both are served.
    /// </remarks>
    internal static Treatment ProjectProfileActivation(ProfileActivation activation)
    {
        var metadata = ProfileActivationLegacyFields.ToSpanMetadata(activation);
        var span = ProfileSpanMetadata.Parse(metadata);
        Treatment treatment;
        if (span.IsEffective)
        {
            treatment = EffectiveProfileSwitch.Project(span, activation.Mills);
        }
        else
        {
            var duration = UploadedDuration(metadata)
                ?? (activation.RequestedDurationMs is > 0 and var requestedMs ? requestedMs / 60_000.0
                    : activation.EndTimestamp is not null ? 0 : null);
            treatment = new Treatment
            {
                EventType = TreatmentTypes.ProfileSwitch,
                Duration = duration,
                DurationInMilliseconds = span.DurationInMilliseconds
                    ?? (duration is { } minutes ? (long)Math.Round(minutes * 60_000) : null),
                Profile = span.ProfileName,
                ProfileJson = span.ProfileJson,
                Percentage = span.Percentage,
                Timeshift = span.UploadedTimeshift,
                Reason = metadata.TryReadString("reason"),
                OriginalProfileName = span.OriginalProfileName,
                OriginalDuration = (int?)span.OriginalDuration,
            };
        }

        treatment.PumpId = span.PumpId;
        treatment.PumpType = span.PumpType;
        treatment.PumpSerial = span.PumpSerial;
        if (span.App is { } app)
            treatment.AdditionalProperties = new Dictionary<string, object> { [ProfileSpanMetadata.AppKey] = app };

        treatment.Id = activation.Id.ToString();
        treatment.Mills = activation.Mills;
        treatment.DataSource = activation.DataSource;
        treatment.RawTimestamp ??= TreatmentUploadedTimestamp.OfSpan(metadata);
        treatment.EnteredBy = metadata.TryReadString(ProfileActivationLegacyFields.EnteredByKey);
        treatment.Notes = metadata.TryReadString(NotesKey);
        treatment.SyncIdentifier = metadata.TryReadString(SyncIdentifierKey);
        treatment.InsulinType = metadata.TryReadString(InsulinTypeKey);
        treatment.UtcOffset = (int?)metadata.TryReadDecimal(StateSpanMetadataExtensions.UtcOffsetKey);
        return treatment;
    }

    /// <summary>The tables whose records pair into a Meal Bolus.</summary>
    internal static readonly IReadOnlyList<ILegacyKeyedTreatmentTable> MealTables =
        [KeyedTableOf<Bolus>(), KeyedTableOf<CarbIntake>()];

    /// <summary>
    /// A read of one type the database provider cannot translate logs and yields
    /// <paramref name="fallback"/>, so the other types still serve. Any other failure, a cancellation
    /// included, propagates.
    /// </summary>
    internal static async Task<T> GuardedAsync<T>(
        ILegacyTreatmentTable table, ILogger logger, Func<Task<T>> read, T fallback)
    {
        try
        {
            return await read();
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            logger.LogError(ex, "Failed to fetch V4 records of type {Type} for legacy projection", table.RecordType);
            return fallback;
        }
    }

    private const string TreatmentsCollectionJson =
        $$"""{"{{StateSpanMetadataExtensions.CollectionKey}}":"{{StateSpanMetadataExtensions.TreatmentsCollection}}"}""";

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
        (double?)metadata.TryReadDecimal(StateSpanMetadataExtensions.UploadedDurationKey);

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
    /// <c>notes</c> holds the text of the Note a served record's treatment wrote beside it, by
    /// treatment id, served on the record over any copy a span keeps; see
    /// <see cref="NotesServedWithAnotherRecord"/>. A device event keeps the one copy of its notes,
    /// which its own V4 edits reach, so a Note written beside one before that is not served on it.
    /// </remarks>
    internal static List<Treatment> Assemble(
        IReadOnlyList<FetchedRecord> rows,
        CarbFoodIndex foods,
        IReadOnlyDictionary<string, string>? notes = null)
    {
        var treatments = new List<Treatment>();
        var paired = PairMeals(rows, foods, notes, treatments);

        foreach (var row in rows)
        {
            if (paired.Contains(row.Record))
                continue;

            var treatment = row.Table.Project(row.Record, foods);
            if (row.Record is not DeviceEvent)
                treatment = WithNotes(treatment, ServedKey(row.Record), notes);
            treatments.Add(Stamp(treatment, row.Created, row.Modified, row.Deleted));
        }

        return treatments;
    }

    private static Treatment WithNotes(
        Treatment treatment, string? key, IReadOnlyDictionary<string, string>? notes)
    {
        if (key is not null && notes?.GetValueOrDefault(key) is { } text)
            treatment.Notes = text;
        return treatment;
    }

    /// <summary>
    /// The treatment id a served record other than a Note was written under, which the Note its
    /// treatment wrote beside it carries; see <see cref="NotesServedWithAnotherRecord"/>.
    /// </summary>
    internal static string? ServedKey(object record) => record switch
    {
        StateSpanEntity span => span.OriginalId,
        Note => null,
        IV4Record v4 => v4.LegacyId,
        _ => null,
    };

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
        IReadOnlyDictionary<string, string>? notes,
        List<Treatment> treatments
    )
    {
        var paired = new HashSet<object>(ReferenceEqualityComparer.Instance);

        var boluses = Correlated<Bolus>(records);
        var carbs = Correlated<CarbIntake>(records);

        foreach (var key in boluses.Select(g => g.Key).Union(carbs.Select(g => g.Key)))
        {
            var mealBolus = MealHalf(boluses[key].Select(b => b.Record));
            var mealCarb = MealHalf(carbs[key].Select(c => c.Record));
            if (mealBolus is null || mealCarb is null)
                continue;

            var bolus = boluses[key].First(b => ReferenceEquals(b.Record, mealBolus));
            var carb = carbs[key].First(c => ReferenceEquals(c.Record, mealCarb));

            paired.Add(bolus.Record);
            paired.Add(carb.Record);

            // The meal carries the newer of the two stamps: stamping the older one leaves the other
            // record above the cursor the consumer derives from this page, which re-serves the same
            // meal on the next request without ever advancing.
            treatments.Add(Stamp(
                WithNotes(
                    ProjectMealBolus(bolus.Record, carb.Record, foods.For(carb.Record.Id)),
                    bolus.Record.LegacyId ?? carb.Record.LegacyId,
                    notes),
                Oldest(bolus.Record.CreatedAt, carb.Record.CreatedAt),
                Newest(bolus.Modified, carb.Modified),
                key.Deleted));
        }

        return paired;
    }

    /// <summary>
    /// The bolus or carb intake of one correlation that <see cref="PairMeals"/> serves as half of its
    /// Meal Bolus: the largest dose, the lowest id on a tie.
    /// </summary>
    internal static T? MealHalf<T>(IEnumerable<T> correlated) where T : class, IV4Record =>
        correlated
            .OrderByDescending(record => record switch
            {
                Bolus bolus => bolus.Insulin,
                CarbIntake carb => carb.Carbs,
                _ => 0,
            })
            .ThenBy(record => record.Id)
            .FirstOrDefault();

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
        treatment.SrvCreated = createdAt.ToMills();
        treatment.SrvModified = modifiedAt.ToMills();
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
            EventType = TreatmentUploadedEventType.Of(bolus.AdditionalProperties)
                ?? TreatmentUploadedEventType.Of(carb.AdditionalProperties)
                ?? TreatmentTypes.MealBolus,
            Mills = bolus.Mills,
            Insulin = bolus.Insulin,
            PreBolus = TreatmentUploadedPreBolus.Of(bolus.AdditionalProperties),
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
            EventType = TreatmentUploadedEventType.Of(bolus.AdditionalProperties) ?? TreatmentTypes.CorrectionBolus,
            Mills = bolus.Mills,
            Insulin = bolus.Insulin,
            PreBolus = TreatmentUploadedPreBolus.Of(bolus.AdditionalProperties),
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
            EventType = TreatmentUploadedEventType.Of(carb.AdditionalProperties) ?? TreatmentTypes.CarbCorrection,
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
