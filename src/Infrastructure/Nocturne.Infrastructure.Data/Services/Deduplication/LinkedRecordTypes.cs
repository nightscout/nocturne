using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using static Nocturne.Infrastructure.Data.Services.Deduplication.DuplicateMatching;

namespace Nocturne.Infrastructure.Data.Services.Deduplication;

/// <summary>
/// The record types deduplication links, each with how its rows read as match inputs, and the reads
/// of rows and links every part of deduplication shares.
/// </summary>
public static class LinkedRecordTypes
{
    /// <summary>
    /// How old a link must be before <see cref="DuplicateReconciler.PruneSingletonLinksAsync"/> may delete it as the only
    /// link of its group, and how old a record with no link must be before matching reads it as that
    /// pruned group (<see cref="UnlinkedAsync"/>). A link is written after its record, so every record
    /// whose link was pruned is older than this. A younger record with no link is one whose own
    /// insert-time pass has not run, which matching must not see: it would make the later records of
    /// an in-flight batch candidates for its earlier ones.
    /// </summary>
    internal static readonly TimeSpan SingletonLinkRetention = TimeSpan.FromDays(1);

    /// <summary>
    /// The records of <paramref name="recordType"/> with no link that are old enough to have been a
    /// pruned singleton, event time within the optional bounds, each as a primary link of a group of
    /// its own that is not yet stored. A caller that puts one into a group stores it.
    /// </summary>
    internal static Task<List<LinkedRecordEntity>> UnlinkedAsync(
        NocturneDbContext db, RecordType recordType, long? fromMills, long? toMills, CancellationToken ct) =>
        TypePhases.SingleOrDefault(p => p.RecordType == recordType) is { } phase
            ? phase.Unlinked(db, fromMills, toMills, DateTime.UtcNow - SingletonLinkRetention, ct)
            : Task.FromResult(new List<LinkedRecordEntity>());

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction holding the tenant's dedup write lock, which
    /// <see cref="DuplicateReconciler.PruneSingletonLinksAsync"/> also takes. Without it a pass could read a singleton
    /// link as a match, the prune delete it, and the pass then add a record to a group that no longer
    /// has a primary, hiding it from every read.
    /// </summary>
    internal static Task<T> UnderWriteLockAsync<T>(
        NocturneDbContext db, Func<CancellationToken, Task<T>> work, CancellationToken ct) =>
        db.ExecuteInTransactionAsync(async token =>
        {
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({WriteLockKey(db.TenantId)}, 0))", token);
            }

            return await work(token);
        }, ct: ct);

    /// <summary>The text the tenant's dedup write lock is hashed from.</summary>
    internal static string WriteLockKey(Guid tenantId) => $"dedup:{tenantId}";

    /// <summary>
    /// Loads the full set of data sources behind each of the given canonical groups. The whole
    /// group is read rather than just the links inside a matching window, so a same-source record
    /// sitting outside the window still blocks a wide match. A group in <paramref name="unlinked"/> is
    /// not stored and its one record is its source.
    /// </summary>
    internal static async Task<Dictionary<Guid, HashSet<string>>> LoadGroupSourcesAsync(
        NocturneDbContext db,
        string recordTypeStr,
        List<Guid> canonicalIds,
        IReadOnlyDictionary<Guid, LinkedRecordEntity> unlinked,
        CancellationToken ct)
    {
        var sourcesByCanonical = new Dictionary<Guid, HashSet<string>>();
        void Add(Guid canonicalId, string dataSource)
        {
            if (!sourcesByCanonical.TryGetValue(canonicalId, out var sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sourcesByCanonical[canonicalId] = sources;
            }

            sources.Add(dataSource);
        }

        var stored = new List<Guid>();
        foreach (var canonicalId in canonicalIds)
        {
            if (unlinked.TryGetValue(canonicalId, out var link))
                Add(canonicalId, link.DataSource);
            else
                stored.Add(canonicalId);
        }

        if (stored.Count == 0)
            return sourcesByCanonical;

        var rows = await db.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr && stored.Contains(lr.CanonicalId))
            .Select(lr => new { lr.CanonicalId, lr.DataSource })
            .ToListAsync(ct);

        foreach (var row in rows)
            Add(row.CanonicalId, row.DataSource);

        return sourcesByCanonical;
    }

    /// <summary>
    /// The <see cref="RecordType"/> a <c>linked_records.record_type</c> string names, or null when
    /// the column holds a value outside the enum. The column's surface is broader than the enum —
    /// it carries whatever earlier versions wrote — and one such row must neither fail a read nor
    /// block a tenant's remaining reconcile batches, so every caller skips a null.
    /// </summary>
    internal static RecordType? ParseRecordType(string recordType, ILogger logger)
    {
        if (Enum.TryParse<RecordType>(recordType, ignoreCase: true, out var type))
        {
            return type;
        }

        logger.LogWarning("Skipping linked_records row with unknown RecordType '{RecordType}'", recordType);
        return null;
    }

    /// <summary>
    /// Loads, for each requested record id, its <see cref="MatchCriteria"/> and whether the
    /// underlying record is soft-deleted. Soft-deleted rows are included via
    /// <c>IgnoreQueryFilters</c>; record types without a <c>DeletedAt</c> column report
    /// <see cref="RecordInfo.IsDeleted"/> as <see langword="false"/>.
    /// <para>
    /// The global query filter combines tenant scoping (<c>TenantId == this.TenantId</c>) with
    /// the soft-delete predicate (<c>DeletedAt == null</c>) and <c>IgnoreQueryFilters</c> drops
    /// both. To bypass only the soft-delete filter, each query re-applies the tenant predicate
    /// (<c>e.TenantId == db.TenantId</c>) explicitly so cross-tenant rows can never leak in.
    /// </para>
    /// </summary>
    internal static async Task<Dictionary<Guid, RecordInfo>> LoadRecordInfoAsync(
        NocturneDbContext db,
        RecordType recordType, HashSet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, RecordInfo>();

        return recordType switch
        {
            RecordType.SensorGlucose => await LoadAsync<SensorGlucoseEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.Bolus => await LoadAsync<BolusEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.CarbIntake => await LoadAsync<CarbIntakeEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.BGCheck => await LoadAsync<BGCheckEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.DeviceEvent => await LoadAsync<DeviceEventEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.Note => await LoadAsync<NoteEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.BolusCalculation => await LoadAsync<BolusCalculationEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.TempBasal => await LoadAsync<TempBasalEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.StateSpan => await LoadStateSpanInfoAsync(db, ids, ct),
            _ => new Dictionary<Guid, RecordInfo>()
        };
    }

    /// <summary>
    /// Loads the criteria and soft-deleted status of every requested row of one entity type.
    /// </summary>
    private static async Task<Dictionary<Guid, RecordInfo>> LoadAsync<TEntity>(
        NocturneDbContext db,
        Func<TEntity, MatchCriteria> toCriteria, HashSet<Guid> ids, CancellationToken ct)
        where TEntity : class, IV4Entity
    {
        var records = await db.Set<TEntity>().AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.TenantId == db.TenantId && ids.Contains(e.Id)).ToListAsync(ct);
        return records.ToDictionary(e => e.Id, e => new RecordInfo(toCriteria(e), e.DeletedAt != null));
    }

    /// <summary>
    /// State spans are loaded apart from <see cref="LoadAsync{TEntity}"/> because
    /// <see cref="StateSpanEntity"/> is keyed by <c>OriginalId</c> rather than the
    /// <c>LegacyId</c> that <see cref="IV4Entity"/> requires.
    /// </summary>
    private static async Task<Dictionary<Guid, RecordInfo>> LoadStateSpanInfoAsync(
        NocturneDbContext db,
        HashSet<Guid> ids, CancellationToken ct)
    {
        var records = await db.StateSpans.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == db.TenantId && ids.Contains(s.Id)).ToListAsync(ct);
        return records.ToDictionary(s => s.Id,
            s => new RecordInfo(
                MatchCriteriaMapper.From(s), s.DeletedAt != null, new SpanEnd(s.SupersededById, s.EndTimestamp)));
    }

    /// <summary>Rows <see cref="TypePhase.Pages"/> reads per page.</summary>
    internal const int PageSize = 500;

    /// <summary>
    /// One record type as deduplication reads it. <see cref="TypePhase.Name"/> is reported as
    /// <see cref="DeduplicationProgress.CurrentPhase"/>, so callers observe it.
    /// <see cref="TypePhase.OrphanedLinks"/> is this type's links whose record id is absent from the
    /// type's rows with <see cref="NocturneDbContext.SoftDeleteFilterKey"/> lifted, because a
    /// soft-deleted record is still linked. It is a correlated <c>NOT EXISTS</c>: a negated
    /// <c>Contains</c> translates to <c>NOT IN</c>, which PostgreSQL rescans per link once the record
    /// table outgrows work_mem. Tenant isolation stays on, which the <see cref="ITenantScoped"/>
    /// constraint on <see cref="Phase{TEntity}"/> is what guarantees.
    /// </summary>
    /// <param name="Unlinked">
    /// <see cref="UnlinkedAsync"/> for this type: its records created before the given instant with no
    /// link, event time within the optional bounds, each as the singleton group it would have been.
    /// </param>
    /// <param name="Input">A row of this type as a match input.</param>
    /// <param name="Pages">
    /// The type's rows with event time after the given instant (every row for null), as match inputs in
    /// event time order, a page at a time (<see cref="PagesAsync{TEntity}"/>).
    /// </param>
    internal sealed record TypePhase(
        RecordType RecordType,
        string Name,
        Func<NocturneDbContext, CancellationToken, Task<int>> CountAsync,
        Func<NocturneDbContext, IQueryable<LinkedRecordEntity>> OrphanedLinks,
        Func<NocturneDbContext, long?, long?, DateTime, CancellationToken, Task<List<LinkedRecordEntity>>> Unlinked,
        Func<object, DeduplicationInput> Input,
        Func<NocturneDbContext, DateTime?, CancellationToken, IAsyncEnumerable<InputPage>> Pages);

    /// <summary>A page of <see cref="TypePhase.Pages"/>: its inputs and the event time it ends on.</summary>
    internal sealed record InputPage(List<DeduplicationInput> Inputs, DateTime End);

    /// <summary>
    /// Builds one <see cref="TypePhase"/>. <paramref name="timestamp"/> both orders the query and
    /// supplies the event time, so a type whose event time is not <c>Timestamp</c> states it once;
    /// <paramref name="id"/> serves the same two purposes, and <paramref name="createdAt"/> is the
    /// insert stamp <see cref="SingletonLinkRetention"/> is measured on.
    /// </summary>
    private static TypePhase Phase<TEntity>(
        RecordType recordType,
        string name,
        Func<NocturneDbContext, IQueryable<TEntity>> set,
        Expression<Func<TEntity, DateTime>> timestamp,
        Expression<Func<TEntity, Guid>> id,
        Expression<Func<TEntity, DateTime>> createdAt,
        Func<TEntity, string?> dataSource,
        Func<TEntity, MatchCriteria> criteria) where TEntity : class, ITenantScoped
    {
        var eventTime = timestamp.Compile();
        var recordId = id.Compile();
        var key = RecordTypeKeys.Key(recordType);
        var timestampName = MemberName(timestamp);
        var idName = MemberName(id);
        var createdAtName = MemberName(createdAt);

        DeduplicationInput ToInput(TEntity e) => new(
            recordId(e),
            eventTime(e).ToMills(),
            dataSource(e) ?? DeduplicationInput.UnknownDataSource,
            criteria(e));

        return new TypePhase(
            recordType,
            name,
            (context, ct) => set(context).CountAsync(ct),
            context =>
            {
                var recordIds = set(context)
                    .IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
                    .Select(id);
                return context.LinkedRecords
                    .Where(lr => lr.RecordType == key && !recordIds.Any(recordId => recordId == lr.RecordId));
            },
            async (context, fromMills, toMills, createdBefore, ct) =>
            {
                // Soft-deleted rows included: a pruned link was a link whatever its record's state,
                // and the merge pass reads deleted primaries as evidence.
                var query = set(context).AsNoTracking()
                    .IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
                    .Where(e => EF.Property<DateTime>(e, createdAtName) < createdBefore
                        && !context.LinkedRecords.Any(lr =>
                            lr.RecordType == key && lr.RecordId == EF.Property<Guid>(e, idName)));
                if (fromMills is { } from)
                {
                    var fromTime = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime;
                    query = query.Where(e => EF.Property<DateTime>(e, timestampName) >= fromTime);
                }
                if (toMills is { } to)
                {
                    var toTime = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime;
                    query = query.Where(e => EF.Property<DateTime>(e, timestampName) <= toTime);
                }

                return (await query.ToListAsync(ct)).Select(e => new LinkedRecordEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = context.TenantId,
                    CanonicalId = Guid.CreateVersion7(),
                    RecordType = key,
                    RecordId = recordId(e),
                    SourceTimestamp = eventTime(e).ToMills(),
                    DataSource = dataSource(e) ?? DeduplicationInput.UnknownDataSource,
                    IsPrimary = true,
                }).ToList();
            },
            row => ToInput((TEntity)row),
            (context, after, ct) => PagesAsync(set(context), timestamp, ToInput, after, ct));
    }

    /// <summary>
    /// <paramref name="set"/> in <paramref name="timestamp"/> order, strictly after
    /// <paramref name="after"/>, <see cref="PageSize"/> rows to a page. A page ends on a whole
    /// timestamp (the run of rows sharing the last one is drained with it), so the cursor can advance
    /// strictly past it and no row is read twice or skipped, without needing an order over record ids
    /// that both the database and the CLR agree on.
    /// </summary>
    private static async IAsyncEnumerable<InputPage> PagesAsync<TEntity>(
        IQueryable<TEntity> set,
        Expression<Func<TEntity, DateTime>> timestamp,
        Func<TEntity, DeduplicationInput> toInput,
        DateTime? after,
        [EnumeratorCancellation] CancellationToken ct) where TEntity : class
    {
        var timestampProperty = MemberName(timestamp);
        var eventTime = timestamp.Compile();
        var ordered = set
            .AsNoTracking()
            .OrderBy(e => EF.Property<DateTime>(e, timestampProperty));

        var cursor = after;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await (cursor is { } from
                    ? ordered.Where(e => EF.Property<DateTime>(e, timestampProperty) > from)
                    : ordered)
                .Take(PageSize)
                .ToListAsync(ct);

            if (page.Count == 0)
                yield break;

            var pageEnd = eventTime(page[^1]);
            if (page.Count == PageSize)
            {
                var tail = await ordered
                    .Where(e => EF.Property<DateTime>(e, timestampProperty) == pageEnd)
                    .ToListAsync(ct);

                page = [.. page.Where(e => eventTime(e) < pageEnd), .. tail];
            }

            cursor = pageEnd;
            yield return new InputPage([.. page.Select(toInput)], pageEnd);
        }
    }

    /// <summary>
    /// Every record type deduplication links, in the order a full run passes over them. MeterGlucose
    /// has no <see cref="RecordType"/> value and is not deduplicated.
    /// </summary>
    internal static readonly TypePhase[] TypePhases =
    [
        Phase(RecordType.SensorGlucose, "SensorGlucose",
            static c => c.SensorGlucose, static e => e.Timestamp,
            static e => e.Id, static e => e.SysCreatedAt, static e => e.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.Bolus, "Boluses",
            static c => c.Boluses, static b => b.Timestamp,
            static b => b.Id, static b => b.SysCreatedAt, static b => b.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.CarbIntake, "CarbIntakes",
            static c => c.CarbIntakes, static c => c.Timestamp,
            static c => c.Id, static c => c.SysCreatedAt, static c => c.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.BGCheck, "BGChecks",
            static c => c.BGChecks, static bg => bg.Timestamp,
            static bg => bg.Id, static bg => bg.SysCreatedAt, static bg => bg.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.DeviceEvent, "DeviceEvents",
            static c => c.DeviceEvents, static d => d.Timestamp,
            static d => d.Id, static d => d.SysCreatedAt, static d => d.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.Note, "Notes",
            static c => c.Notes, static n => n.Timestamp,
            static n => n.Id, static n => n.SysCreatedAt, static n => n.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.BolusCalculation, "BolusCalculations",
            static c => c.BolusCalculations, static bc => bc.Timestamp,
            static bc => bc.Id, static bc => bc.SysCreatedAt, static bc => bc.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.TempBasal, "TempBasals",
            static c => c.TempBasals, static t => t.Timestamp,
            static t => t.Id, static t => t.SysCreatedAt, static t => t.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.StateSpan, "StateSpans",
            static c => c.StateSpans, static s => s.StartTimestamp,
            static s => s.Id, static s => s.CreatedAt, static s => s.Source, MatchCriteriaMapper.From)
    ];

    /// <summary>
    /// The record types deduplication links, in <see cref="TypePhases"/> order. Internal so a test can
    /// drive itself off the registry instead of restating it.
    /// </summary>
    internal static IReadOnlyList<RecordType> DeduplicatedRecordTypes { get; } =
        [.. TypePhases.Select(static p => p.RecordType)];

    /// <summary>The match inputs of <paramref name="rows"/>, rows of <paramref name="recordType"/>.</summary>
    internal static List<DeduplicationInput> InputsOf<TEntity>(RecordType recordType, IEnumerable<TEntity> rows)
        where TEntity : class
    {
        var phase = TypePhases.Single(p => p.RecordType == recordType);
        return [.. rows.Select(row => phase.Input(row))];
    }

    /// <summary>
    /// The <see cref="RecordTypeKeys"/> key the rows of <paramref name="entityType"/> are linked
    /// under, or null for a type deduplication does not cover. A hard delete of such rows deletes
    /// their links with them (<see cref="RetentionPurgeExtensions.PurgeOlderThanAsync"/>,
    /// <see cref="PurgeExtensions.PurgeAsync{TEntity}"/>).
    /// </summary>
    public static string? LinkedRecordTypeKeyOf(Type entityType) =>
        DuplicateGroupPrimaries.RecordTypeOf(entityType) is { } recordType ? RecordTypeKeys.Key(recordType) : null;

    /// <summary>
    /// Deletes the links of <paramref name="context"/>'s tenant that have nothing left to point at:
    /// a record id no longer in its type's table, and a record type no <see cref="RecordType"/>
    /// member names, which no caller can read back (<see cref="ParseRecordType"/> skips it). A
    /// soft-deleted record is not an orphan. Every hard-delete path deletes a row's links with it
    /// (<see cref="LinkedRecordTypeKeyOf"/>), so this reads every link the tenant owns to find what
    /// those paths cannot have left; a full run (<see cref="DeduplicationAdmin.RunAsync"/>) runs it
    /// first, and nothing runs it on a schedule.
    /// </summary>
    /// <returns>The number of links deleted.</returns>
    internal static async Task<int> DeleteOrphanedLinksAsync(
        NocturneDbContext context, CancellationToken ct = default)
    {
        var namedTypes = Enum.GetValues<RecordType>().Select(RecordTypeKeys.Key).ToList();

        var deleted = await context.LinkedRecords
            .Where(lr => !namedTypes.Contains(lr.RecordType))
            .ExecuteDeleteAsync(ct);

        foreach (var phase in TypePhases)
        {
            deleted += await phase.OrphanedLinks(context).ExecuteDeleteAsync(ct);
        }

        return deleted;
    }

    internal static IQueryable<LinkedRecordEntity> OrphanedLinksOf(
        NocturneDbContext context, RecordType recordType) =>
        TypePhases.Single(p => p.RecordType == recordType).OrphanedLinks(context);

    /// <summary>
    /// The property <paramref name="member"/> reads. One query shape serves every entity type, so a
    /// column arrives as a property name rather than an expression to compose into it.
    /// </summary>
    private static string MemberName<TEntity, TValue>(Expression<Func<TEntity, TValue>> member) =>
        member.Body is MemberExpression access
            ? access.Member.Name
            : throw new ArgumentException(
                "The expression must be a simple member access (e => e.Timestamp): the query reads "
                + "that property by name.",
                nameof(member));
}
