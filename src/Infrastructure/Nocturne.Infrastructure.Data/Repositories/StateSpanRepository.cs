using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Services.Deduplication;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for StateSpan operations
/// </summary>
public class StateSpanRepository : IStateSpanRepository
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly IDuplicateLinker _linker;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<StateSpanRepository> _logger;
    private readonly ILegacyReadCacheInvalidation? _legacyReads;

    /// <summary>
    /// The stored <c>Category</c> values that represent v1 Activity records, as strings for
    /// translation into SQL.
    /// </summary>
    private static readonly List<string> ActivityCategories =
        ActivityStateSpanMapper.ActivityCategories.Select(c => c.ToString()).ToList();

    /// <summary>
    /// Every <see cref="StateSpanCategory"/> name outside <see cref="ActivityCategories"/>.
    /// </summary>
    private static readonly List<string> NonActivityCategories =
        StateSpanMapper.CategoryNames.Except(ActivityCategories).ToList();

    /// <summary>
    /// Open spans per non-exclusive category that <see cref="GetByCategories"/> returns from
    /// before its window.
    /// </summary>
    private const int OpenCarryInLimit = 10;

    /// <summary>
    /// Exclusive categories whose open spans exclude each other only within a partition.
    /// </summary>
    private static readonly Dictionary<string, Expression<Func<StateSpanEntity, string?>>>
        CarryInPartitions = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(StateSpanCategory.PumpMode)] = s => s.State,
        };

    /// <summary>
    /// Initializes a new instance of the StateSpanRepository class
    /// </summary>
    /// <param name="contexts">Creates the contexts the repository reads and writes through</param>
    /// <param name="linker">Links committed inserts into their duplicate groups</param>
    /// <param name="auditContext">The audit context for tracking mutations</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="legacyReads">Optional; null clears no legacy read cache.</param>
    public StateSpanRepository(
        INocturneDbContextFactory contexts,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<StateSpanRepository> logger,
        ILegacyReadCacheInvalidation? legacyReads = null
    )
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _linker = linker;
        _auditContext = auditContext;
        _logger = logger;
        _legacyReads = legacyReads;
    }

    /// <summary>Tells <see cref="ILegacyReadCacheInvalidation"/> state spans were written, after every committed write.</summary>
    private Task ReportWrittenAsync() =>
        _legacyReads?.WrittenAsync([typeof(StateSpan)]) ?? Task.CompletedTask;

    /// <summary>
    /// Get state spans with optional filtering
    /// </summary>
    /// <param name="category">Optional category filter.</param>
    /// <param name="state">Optional state name filter.</param>
    /// <param name="from">Optional start date filter (includes spans ending after this date).</param>
    /// <param name="to">Optional end date filter (includes spans starting before this date).</param>
    /// <param name="source">Optional source filter.</param>
    /// <param name="active">Optional filter for active (open-ended) vs completed spans.</param>
    /// <param name="count">The maximum number of spans to return.</param>
    /// <param name="skip">The number of spans to skip.</param>
    /// <param name="descending">Whether to sort by start timestamp descending.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of state spans.</returns>
    public async Task<IEnumerable<StateSpan>> GetStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        DateTime? from = null,
        DateTime? to = null,
        string? source = null,
        bool? active = null,
        int count = 100,
        int skip = 0,
        bool descending = true,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await PageAsync(
            BuildFilteredQuery(db, category, state, from, to, source, active),
            count, skip, descending, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StateSpan>> GetStateSpansInCategoriesAsync(
        IReadOnlyCollection<StateSpanCategory> categories,
        string? state,
        DateTime? from,
        DateTime? to,
        string? source,
        bool? active,
        int count,
        int skip,
        bool descending,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await PageAsync(
            InCategories(BuildFilteredQuery(db, null, state, from, to, source, active), categories),
            count, skip, descending, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountStateSpansInCategoriesAsync(
        IReadOnlyCollection<StateSpanCategory> categories,
        string? state,
        DateTime? from,
        DateTime? to,
        string? source,
        bool? active,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await InCategories(BuildFilteredQuery(db, null, state, from, to, source, active), categories)
            .CountAsync(cancellationToken);
    }

    private static IQueryable<StateSpanEntity> InCategories(
        IQueryable<StateSpanEntity> query, IReadOnlyCollection<StateSpanCategory> categories)
    {
        var names = categories.Select(c => c.ToString()).ToList();
        return query.Where(s => names.Contains(s.Category));
    }

    private static async Task<IEnumerable<StateSpan>> PageAsync(
        IQueryable<StateSpanEntity> query, int count, int skip, bool descending, CancellationToken cancellationToken)
    {
        var ordered = descending
            ? query.OrderByDescending(s => s.StartTimestamp).ThenByDescending(s => s.Id)
            : query.OrderBy(s => s.StartTimestamp).ThenBy(s => s.Id);

        var entities = await ordered
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(StateSpanMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        DateTime? from = null,
        DateTime? to = null,
        string? source = null,
        bool? active = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = BuildFilteredQuery(db, category, state, from, to, source, active);
        return await query.CountAsync(cancellationToken);
    }

    private static IQueryable<StateSpanEntity> BuildFilteredQuery(
        NocturneDbContext db,
        StateSpanCategory? category,
        string? state,
        DateTime? from,
        DateTime? to,
        string? source,
        bool? active)
    {
        var query = db.StateSpans.AsNoTracking();

        if (category.HasValue)
            query = query.Where(s => s.Category == category.Value.ToString());

        if (!string.IsNullOrEmpty(state))
            query = query.Where(s => s.State == state);

        if (!string.IsNullOrEmpty(source))
            query = query.Where(s => s.Source == source);

        if (from.HasValue)
            query = query.Where(s => s.EndTimestamp == null || s.EndTimestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(s => s.StartTimestamp <= to.Value);

        if (active.HasValue)
        {
            if (active.Value)
                query = query.Where(s => s.EndTimestamp == null);
            else
                query = query.Where(s => s.EndTimestamp != null);
        }

        query = query.ExcludeDuplicates();

        return query;
    }

    /// <summary>
    /// Get a specific state span by ID
    /// </summary>
    /// <param name="id">The unique identifier (GUID or legacy string ID).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state span, or null if not found.</returns>
    public async Task<StateSpan?> GetStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.StateSpans.AsNoTracking().FirstOrDefaultAsync(
            s => s.LegacyId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await db.StateSpans.AsNoTracking().FirstOrDefaultAsync(
                s => s.Id == guidId,
                cancellationToken
            );
        }

        return entity != null ? StateSpanMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Create or update a state span (upsert by originalId) and link to canonical groups
    /// </summary>
    /// <param name="stateSpan">The state span data to upsert.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The upserted state span.</returns>
    public async Task<StateSpan> UpsertStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default
    ) => (await UpsertBatchAsync([stateSpan], cancellationToken))[0].Span;

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateSpanUpsert>> BulkUpsertAsync(
        IEnumerable<StateSpan> stateSpans,
        CancellationToken cancellationToken = default
    ) => await UpsertBatchAsync(stateSpans.ToList(), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateSpan?>> UpdateStateSpansAsync(
        IReadOnlyList<StateSpan> stateSpans,
        CancellationToken cancellationToken = default)
    {
        var ids = stateSpans.Select(s => s.Id).OfType<string>().Distinct().ToList();
        if (ids.Count == 0)
            return [.. stateSpans.Select(_ => (StateSpan?)null)];

        var guids = ids.Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null).OfType<Guid>().ToList();
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await db.StateSpans
            .Where(s => (s.LegacyId != null && ids.Contains(s.LegacyId)) || guids.Contains(s.Id))
            .ToListAsync(cancellationToken);
        var byOriginalId = entities.Where(e => e.LegacyId != null)
            .ToDictionary(e => e.LegacyId!, StringComparer.Ordinal);
        var byId = entities.ToDictionary(e => e.Id.ToString(), StringComparer.OrdinalIgnoreCase);
        var superseders = await db.SupersedersAsync(entities, cancellationToken);
        var tracked = entities.ToDictionary(e => e.Id);

        var updated = new List<StateSpanEntity?>(stateSpans.Count);
        var mapped = new List<(StateSpanEntity Entity, StateSpan Sent, DateTime? SupersededEnd)>();
        foreach (var stateSpan in stateSpans)
        {
            var entity = stateSpan.Id is { } id ? byOriginalId.GetValueOrDefault(id) ?? byId.GetValueOrDefault(id) : null;
            updated.Add(entity);
            if (entity is null)
                continue;

            // A fold hands back the spans it read, so an end it left alone is the stored one, not an upload's.
            if (stateSpan.EndTimestamp != entity.EndTimestamp)
                mapped.Add((entity, stateSpan, StateSpanSupersessionExtensions.SupersededEnd(entity, superseders)));
            StateSpanMapper.UpdateEntity(entity, stateSpan);
        }

        // Ends are decided once every span is mapped, so a superseder the fold moves is judged where it now starts.
        var leftOpen = new List<StateSpanEntity>();
        foreach (var (entity, sent, supersededEnd) in mapped)
        {
            var by = entity.SupersededById;
            (entity.EndTimestamp, entity.SupersededById) = StateSpanSupersessionExtensions.UploadedEnd(
                entity, sent, supersededEnd,
                by is { } named ? tracked.GetValueOrDefault(named) ?? superseders.GetValueOrDefault(named) : null);
            if (supersededEnd is not null && entity.EndTimestamp is null)
                leftOpen.Add(entity);
        }

        await db.SaveUploadAsync([], leftOpen, db.SaveChangesAsync, cancellationToken);
        IReadOnlyList<StateSpan?> committed = [.. updated.Select(e => e is null ? null : StateSpanMapper.ToDomainModel(e))];
        await ReportCommittedAsync(committed);
        return committed;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every row is mapped before any end is decided, so a superseder the rewrite moves is judged where it
    /// now starts. A row that keeps a superseded end whose superseder moved is written open, and every row
    /// then settles with its neighbours (<see cref="StateSpanSupersessionExtensions.SaveUploadAsync"/>).
    /// </remarks>
    public async Task<IReadOnlyList<string>> RewriteFromUpstreamAsync(
        string source,
        IReadOnlyList<StateSpan> stateSpans,
        CancellationToken cancellationToken = default)
    {
        var sent = stateSpans
            .Where(s => !string.IsNullOrEmpty(s.OriginalId))
            .GroupBy(s => s.OriginalId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        if (sent.Count == 0)
            return [];

        string[] ids = [.. sent.Keys];
        string[] categories = [.. DecomposedTreatmentKeys.TreatmentSpanCategories];
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var written = await db.ExecuteInTransactionAsync(async ct =>
        {
            var rows = await db.StateSpans
                .Where(s => s.Source == source
                    && s.LegacyId != null && ids.Contains(s.LegacyId) && categories.Contains(s.Category))
                .ToListAsync(ct);
            var superseders = await db.SupersedersAsync(rows, ct);
            var supersededEnds = rows
                .Where(r => StateSpanSupersessionExtensions.SupersededEnd(r, superseders) is not null)
                .ToDictionary(r => r.Id, r => (End: r.EndTimestamp, By: r.SupersededById!.Value));
            List<Guid> moved = [.. rows
                .Where(r => r.StartTimestamp != sent[r.LegacyId!].StartTimestamp
                    || r.Category != sent[r.LegacyId!].Category.ToString())
                .Select(r => r.Id)];
            foreach (var row in rows)
                StateSpanMapper.UpdateEntity(row, sent[row.LegacyId!]);

            var byId = rows.ToDictionary(r => r.Id);
            foreach (var row in rows)
            {
                var superseded = supersededEnds.TryGetValue(row.Id, out var ended);
                (row.EndTimestamp, row.SupersededById) = StateSpanSupersessionExtensions.UploadedEnd(
                    row, sent[row.LegacyId!],
                    superseded ? ended.End : null,
                    superseded ? byId.GetValueOrDefault(ended.By) ?? superseders[ended.By] : null);
            }

            await db.SaveUploadAsync(moved, rows, db.SaveChangesAsync, ct);
            return rows.Select(r => r.LegacyId!).ToList();
        }, ct: cancellationToken);

        if (written.Count > 0)
            await ReportWrittenAsync();
        return written;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateSpan>> GetStateSpansAroundAsync(
        StateSpanCategory category,
        DateTime from,
        DateTime through,
        IReadOnlyCollection<string?> followingSources,
        int following,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var spans = BuildFilteredQuery(db, category, null, null, null, null, null);
        var query = spans.Where(s => s.EndTimestamp == null || (s.EndTimestamp >= from && s.StartTimestamp <= through));
        foreach (var source in followingSources.Select(s => string.IsNullOrEmpty(s) ? null : s).Distinct())
        {
            var after = spans.Where(s => s.StartTimestamp > through);
            if (source is not null)
                after = after.Where(s => s.Source == source);
            query = query.Concat(after.OrderBy(s => s.StartTimestamp).Take(following));
        }

        return [.. (await query.ToListAsync(cancellationToken)).DistinctBy(s => s.Id).Select(StateSpanMapper.ToDomainModel)];
    }

    /// <summary>
    /// Upserts <paramref name="stateSpans"/> by <c>OriginalId</c> with one save, leaving the rows a
    /// save per span in input order would: each span sees every earlier one, so a repeated
    /// <c>OriginalId</c> updates the row its first occurrence inserted, and an open span arriving behind
    /// a later one it conflicts with ends at that one's start.
    /// </summary>
    /// <remarks>
    /// Each span's end is decided against the rows as the spans before it left them
    /// (<see cref="StateSpanSupersessionExtensions.UploadedEnd"/>). Every row the batch moves, inserts or
    /// changes then settles with its neighbours (<see cref="StateSpanSupersessionExtensions.SaveUploadAsync"/>),
    /// the step the republish takes.
    /// </remarks>
    /// <param name="stateSpans">The spans to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per input span, what the upsert did with it.</returns>
    private async Task<List<StateSpanUpsert>> UpsertBatchAsync(
        IReadOnlyList<StateSpan> stateSpans,
        CancellationToken cancellationToken)
    {
        if (stateSpans.Count == 0)
            return [];

        await using var db = await _contexts.CreateAsync(cancellationToken);
        var originalIds = stateSpans
            .Select(s => s.OriginalId)
            .OfType<string>()
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var governingByOriginalId = await LoadGoverningSpansAsync(db, originalIds, cancellationToken);
        var hidden = await LoadHiddenGoverningSpansAsync(db, originalIds, governingByOriginalId, cancellationToken);
        var live = governingByOriginalId.Values.Where(s => s.DeletedAt == null).ToList();
        var tracked = live.ToDictionary(s => s.Id);
        // A hidden row's superseder is hidden too, since it starts where the row ends.
        var superseders = hidden.Count == 0
            ? await db.SupersedersAsync(live, cancellationToken)
            : await db.WriteWithHistoryClampLiftedAsync(token => db.SupersedersAsync(live, token), cancellationToken);
        var moved = new HashSet<Guid>();

        var written = new List<(StateSpanEntity Row, StateSpan Sent, UpsertOutcome? Outcome)>(stateSpans.Count);
        var inserted = new List<StateSpanEntity>();
        foreach (var stateSpan in stateSpans)
        {
            var hasOriginalId = !string.IsNullOrEmpty(stateSpan.OriginalId);
            if (hasOriginalId && governingByOriginalId.TryGetValue(stateSpan.OriginalId!, out var governing))
            {
                if (governing.DeletedAt != null)
                {
                    written.Add((governing, stateSpan, null));
                    continue;
                }

                if (tracked.ContainsKey(governing.Id)
                    && StateSpanSuccession.ExclusiveCategories.Contains(governing.Category)
                    && (governing.StartTimestamp != stateSpan.StartTimestamp
                        || governing.Category != stateSpan.Category.ToString()))
                    moved.Add(governing.Id);

                var by = governing.SupersededById;
                var storedSuperseder = by is { } named ? superseders.GetValueOrDefault(named) : null;
                var supersededEnd = StateSpanSupersessionExtensions.SupersededEnd(governing, storedSuperseder);
                StateSpanMapper.UpdateEntity(governing, stateSpan);
                (governing.EndTimestamp, governing.SupersededById) = StateSpanSupersessionExtensions.UploadedEnd(
                    governing, stateSpan, supersededEnd,
                    by is { } pointer ? tracked.GetValueOrDefault(pointer) ?? storedSuperseder : null);

                var entry = db.Entry(governing);
                entry.DetectChanges();
                written.Add((governing, stateSpan, entry.State == EntityState.Added ? UpsertOutcome.Created
                    : NocturneDbContext.WritesNothing(entry) && !hidden.Contains(governing) ? UpsertOutcome.Unchanged
                    : UpsertOutcome.Updated));
                continue;
            }

            var entity = StateSpanMapper.ToEntity(stateSpan);
            db.StateSpans.Add(entity);
            inserted.Add(entity);
            written.Add((entity, stateSpan, UpsertOutcome.Created));
            if (hasOriginalId)
                governingByOriginalId[stateSpan.OriginalId!] = entity;
        }

        async Task<int> SaveAsync(CancellationToken token) => hidden.Count == 0
            ? await db.SaveChangesAsync(token)
            : await db.WriteWithHistoryClampLiftedAsync(db.SaveChangesAsync, token);

        await db.SaveUploadAsync(
            moved,
            written.Where(w => w.Outcome is UpsertOutcome.Created or UpsertOutcome.Updated).Select(w => w.Row),
            SaveAsync,
            cancellationToken);

        IReadOnlyList<StateSpanUpsert> committed =
            [.. written.Select(w => new StateSpanUpsert(AnswerFor(w.Row, w.Sent, hidden), w.Outcome))];
        await ReportCommittedAsync(committed);

        if (inserted.Count > 0)
        {
            try
            {
                await _linker.DeduplicateBatchAsync(
                    RecordType.StateSpan, LinkedRecordTypes.InputsOf(RecordType.StateSpan, inserted), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "StateSpan", inserted.Count);
            }
            catch (OperationCanceledException ex)
            {
                CommittedWrite.Attach(ex, committed);
                throw;
            }

            await ReportCommittedAsync(committed);
        }

        return [.. committed];
    }

    /// <summary>
    /// <see cref="ReportWrittenAsync"/> after a commit, carrying <paramref name="committed"/> on what it
    /// throws (<see cref="CommittedWrite"/>).
    /// </summary>
    private async Task ReportCommittedAsync<TResult>(TResult committed) where TResult : class
    {
        try
        {
            await ReportWrittenAsync();
        }
        catch (Exception ex)
        {
            CommittedWrite.Attach(ex, committed);
            throw;
        }
    }

    /// <summary>
    /// The row that governs each <c>OriginalId</c> in the batch, per
    /// <see cref="SoftDeleteDedupExtensions.GoverningRow{TEntity}"/>: the live row, tracked so the batch
    /// can update it, or else the latest user tombstone that forbids re-creating it, per
    /// <see cref="SoftDeleteDedupExtensions.WhereBlocksSpanRecreation"/>. The unique legacy-id index
    /// allows one live row per id, whatever its category.
    /// </summary>
    private static async Task<Dictionary<string, StateSpanEntity>> LoadGoverningSpansAsync(
        NocturneDbContext db,
        IReadOnlyCollection<string> originalIds,
        CancellationToken cancellationToken)
    {
        if (originalIds.Count == 0)
            return new Dictionary<string, StateSpanEntity>(StringComparer.Ordinal);

        var governing = (await db.StateSpans
                .Where(s => s.LegacyId != null && originalIds.Contains(s.LegacyId))
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.LegacyId!, StringComparer.Ordinal);

        var unheld = originalIds.Except(governing.Keys).ToList();
        if (unheld.Count == 0)
            return governing;

        foreach (var tombstone in (await db.UserTombstones<StateSpanEntity>()
                     .WhereCategoryIsCurrent()
                     .Where(s => s.LegacyId != null && unheld.Contains(s.LegacyId))
                     .ToListAsync(cancellationToken))
                 .GroupBy(s => s.LegacyId!)
                 .Select(g => g.MaxBy(s => s.DeletedAt)!))
        {
            governing[tombstone.LegacyId!] = tombstone;
        }

        return governing;
    }

    /// <summary>
    /// The governing rows of the ids <paramref name="governing"/> lacks that a history-clamped writer cannot
    /// see, added to <paramref name="governing"/>. The unique legacy-id index spans the whole tenant, so an
    /// insert beside one fails with 23505 however the clamp reads. The writer's span overwrites the row, and
    /// the answer is built from the span as sent (<see cref="AnswerFor"/>) and is never
    /// <see cref="UpsertOutcome.Unchanged"/>, which would tell the writer its guess equals a row it cannot read.
    /// </summary>
    private static async Task<HashSet<StateSpanEntity>> LoadHiddenGoverningSpansAsync(
        NocturneDbContext db,
        HashSet<string> originalIds,
        Dictionary<string, StateSpanEntity> governing,
        CancellationToken cancellationToken)
    {
        var hidden = new HashSet<StateSpanEntity>(ReferenceEqualityComparer.Instance);
        if (!db.HistoryClamped)
            return hidden;

        var unseen = originalIds.Except(governing.Keys).ToList();
        if (unseen.Count == 0)
            return hidden;

        var held = await db.WriteWithHistoryClampLiftedAsync(
            token => LoadGoverningSpansAsync(db, unseen, token), cancellationToken);
        foreach (var (id, row) in held)
        {
            governing[id] = row;
            hidden.Add(row);
        }

        return hidden;
    }

    /// <summary>
    /// What an upsert answers for <paramref name="row"/>: the stored row, or for one the clamp hides the span
    /// <paramref name="sent"/> as it was sent, since the row's own supersession, end and timestamps are not
    /// the writer's to read.
    /// </summary>
    private static StateSpan AnswerFor(StateSpanEntity row, StateSpan sent, HashSet<StateSpanEntity> hidden) =>
        hidden.Contains(row)
            ? StateSpanMapper.ToDomainModel(StateSpanMapper.ToEntity(sent))
            : StateSpanMapper.ToDomainModel(row);

    /// <summary>
    /// Update an existing state span
    /// </summary>
    /// <param name="id">The unique identifier of the span to update.</param>
    /// <param name="stateSpan">The updated state span data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated state span, or null if not found.</returns>
    public async Task<StateSpan?> UpdateStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.StateSpans.FirstOrDefaultAsync(
            s => s.LegacyId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await db.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId,
                cancellationToken
            );
        }

        if (entity == null)
            return null;

        StateSpanMapper.UpdateEntity(entity, stateSpan);
        TakeEditedEnd(entity, stateSpan);
        await db.SaveChangesAsync(cancellationToken);
        await ReportWrittenAsync();
        return StateSpanMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// The end an edit of one span by its id leaves it. A new end is the editor's, which wins over
    /// supersession and so drops <see cref="StateSpanEntity.SupersededById"/>. An edit that sends the
    /// stored end, or none, leaves a superseded end where supersession put it.
    /// </summary>
    private static void TakeEditedEnd(StateSpanEntity entity, StateSpan edited)
    {
        if (edited.EndTimestamp is { } end && end != entity.EndTimestamp)
            (entity.EndTimestamp, entity.SupersededById) = (end, null);
        else if (entity.SupersededById is null)
            entity.EndTimestamp = edited.EndTimestamp;
    }

    /// <summary>
    /// Delete a state span
    /// </summary>
    /// <param name="id">The unique identifier of the span to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The spans deleted, the requested one first, with the other copies in its duplicate group.</returns>
    public async Task<IReadOnlyList<StateSpan>> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.StateSpans.FirstOrDefaultAsync(
            s => s.LegacyId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await db.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId,
                cancellationToken
            );
        }

        return entity == null ? [] : await SoftDeleteWithCopiesAsync(db, entity, cancellationToken);
    }

    private async Task<IReadOnlyList<StateSpan>> SoftDeleteWithCopiesAsync(
        NocturneDbContext db, StateSpanEntity entity, CancellationToken cancellationToken)
    {
        var deleted = await db.ExecuteInTransactionAsync<IReadOnlyList<StateSpan>>(async token =>
        {
            var (saved, copies, copyIds) = await DuplicateGroupPrimaries.SoftDeleteAsync(
                db, entity, RecordType.StateSpan, token);
            if (saved == 0)
                return [];

            await db.ReopenSupersededAsync([entity.Id, .. copyIds], purged: false, token);
            return [StateSpanMapper.ToDomainModel(entity), .. copies.Select(StateSpanMapper.ToDomainModel)];
        }, ct: cancellationToken);
        if (deleted.Count > 0)
            await ReportWrittenAsync();
        return deleted;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, DeletedStateSpan>> ResolveDeletedAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var rowIds = ids.Select(id => Guid.TryParse(id, out var rowId) ? rowId : (Guid?)null).ToList();
        var deleted = await db.StateSpans.IncludingDeleted().AsNoTracking()
            .WhereCategoryIsCurrent()
            .Where(s => s.DeletedAt != null
                        && ((s.LegacyId != null && ids.Contains(s.LegacyId)) || rowIds.Contains(s.Id)))
            .Select(s => new { s.Id, s.LegacyId, s.Category, s.DeletedAt })
            .ToListAsync(cancellationToken);

        var resolved = new Dictionary<Guid, DeletedStateSpan>();
        foreach (var id in ids.Distinct())
        {
            var named = deleted.Where(s => s.LegacyId == id).MaxBy(s => s.DeletedAt)
                        ?? deleted.FirstOrDefault(s => Guid.TryParse(id, out var rowId) && s.Id == rowId);
            if (named is not null)
                resolved[named.Id] = new DeletedStateSpan(id, StateSpanMapper.ParseCategory(named.Category));
        }

        return resolved;
    }

    /// <inheritdoc />
    public async Task<StateSpan> RestoreAsync(
        Guid id, WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var restored = await db.RestoreDeletedAsync<StateSpanEntity>(id, nameof(StateSpan), cancellationToken);
        await ReportWrittenAsync();
        return StateSpanMapper.ToDomainModel(restored);
    }

    /// <inheritdoc />
    public async Task<BulkRestoreResult<StateSpan>> BulkRestoreAsync(
        IEnumerable<Guid> ids, WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var restored = await db.RestoreDeletedAsync<StateSpanEntity>(ids, nameof(StateSpan), cancellationToken);
        await ReportWrittenAsync();
        return restored.Map(StateSpanMapper.ToDomainModel);
    }

    /// <summary>
    /// Delete all state spans with the specified data source
    /// </summary>
    /// <param name="source">The source identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<long> DeleteBySourceAsync(
        string source,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var deletedCount = await db.AuditedSoftDeleteAsync(
            db.StateSpans.Where(s => s.Source == source), _auditContext,
            $"data_source={source}", cancellationToken);
        if (deletedCount > 0)
            await ReportWrittenAsync();
        return deletedCount;
    }

    /// <inheritdoc />
    public async Task ReplaceBySourceAsync(
        string source,
        IReadOnlyCollection<StateSpan> stateSpans,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var purged = await db.ExecuteInTransactionAsync(async ct =>
        {
            var count = await db.StateSpans.PurgeAsync(s => s.Source == source, ct);
            var written = stateSpans.Select(StateSpanMapper.ToEntity).ToList();
            db.StateSpans.AddRange(written);
            await db.SaveChangesAsync(ct);
            await db.SupersedeByArrivedAsync([.. written.Select(s => s.Id)], asGiven: true, ct);
            return count;
        }, ct: cancellationToken);
        if (purged > 0 || stateSpans.Count > 0)
            await ReportWrittenAsync();
    }

    /// <inheritdoc />
    public async Task<PumpModeState?> GetCurrentPumpModeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var pumpModeCategory = nameof(StateSpanCategory.PumpMode);

        var latest = await db.StateSpans.AsNoTracking()
            .Where(s => s.Category == pumpModeCategory && s.EndTimestamp == null)
            .ExcludeDuplicates()
            .OrderByDescending(s => s.StartTimestamp)
            .ThenByDescending(s => s.Id)
            .Select(s => s.State)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
            return null;

        return Enum.TryParse<PumpModeState>(latest, ignoreCase: true, out var mode)
            ? mode
            : null;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StateSpan>> GetByCategory(
        StateSpanCategory category,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await BuildFilteredQuery(db, category, null, from, to, null, null)
            .OrderByDescending(s => s.StartTimestamp)
            .ThenByDescending(s => s.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(StateSpanMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<StateSpan?> GetActiveAtAsync(
        StateSpanCategory category,
        string? state,
        DateTime at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var categoryString = category.ToString();
        var entity = await db.StateSpans
            .AsNoTracking()
            .Where(s => s.Category == categoryString
                        && (state == null || s.State == state)
                        && s.StartTimestamp <= at
                        && (s.EndTimestamp == null || s.EndTimestamp > at))
            .ExcludeDuplicates()
            .OrderByDescending(s => s.StartTimestamp)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : StateSpanMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateSpan>> GetAllCopiesAsync(
        StateSpanCategory category,
        DateTime from,
        DateTime? to,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var categoryString = category.ToString();
        var entities = await db.StateSpans
            .AsNoTracking()
            .Where(s => s.Category == categoryString
                        && (to == null || s.StartTimestamp <= to)
                        && (s.EndTimestamp == null || s.EndTimestamp >= from))
            .OrderBy(s => s.StartTimestamp)
            .ToListAsync(cancellationToken);
        return [.. entities.Select(StateSpanMapper.ToDomainModel)];
    }

    /// <summary>
    /// Get state spans for multiple categories. With <paramref name="from"/> set, runs one query
    /// for the window and one per category for spans that started before it.
    /// </summary>
    /// <param name="categories">The collection of categories to filter by.</param>
    /// <param name="from">Optional start date filter.</param>
    /// <param name="to">Optional end date filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary of results grouped by category.</returns>
    public virtual async Task<Dictionary<StateSpanCategory, List<StateSpan>>> GetByCategories(
        IEnumerable<StateSpanCategory> categories,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var categoryStrings = categories.Select(c => c.ToString()).ToList();

        var query = db.StateSpans.AsNoTracking()
            .Where(s => categoryStrings.Contains(s.Category))
            .ExcludeDuplicates();

        if (to.HasValue)
            query = query.Where(s => s.StartTimestamp <= to.Value);

        List<StateSpanEntity> entities;
        if (from.HasValue)
        {
            entities = await query
                .Where(s => s.EndTimestamp >= from.Value
                            || (s.EndTimestamp == null && s.StartTimestamp >= from.Value))
                .ToListAsync(cancellationToken);

            // A store with never-closed spans can hold any number of open spans from before
            // the window.
            foreach (var category in categoryStrings)
            {
                var before = query.Where(s => s.Category == category && s.StartTimestamp < from.Value);

                // An exclusive span older than a newer closed one is not in effect, even if open.
                if (CarryInPartitions.TryGetValue(category, out var partition))
                {
                    var newest = await before
                        .GroupBy(partition)
                        .Select(g => g
                            .OrderByDescending(s => s.StartTimestamp)
                            .ThenByDescending(s => s.Id)
                            .First())
                        .ToListAsync(cancellationToken);
                    entities.AddRange(newest.Where(s => s.EndTimestamp == null));
                }
                else if (StateSpanSuccession.ExclusiveCategories.Contains(category))
                {
                    var newest = await NewestFirst(before).FirstOrDefaultAsync(cancellationToken);
                    if (newest is { EndTimestamp: null })
                        entities.Add(newest);
                }
                else
                {
                    var open = await NewestFirst(before.Where(s => s.EndTimestamp == null))
                        .Take(OpenCarryInLimit + 1)
                        .ToListAsync(cancellationToken);

                    if (open.Count > OpenCarryInLimit)
                    {
                        open.RemoveAt(OpenCarryInLimit);
                        _logger.LogDebug(
                            "More than {Limit} open {Category} spans started before {From}; returning only the newest",
                            OpenCarryInLimit, category, from.Value);
                    }

                    entities.AddRange(open);
                }
            }

            entities = entities.OrderByDescending(s => s.StartTimestamp).ToList();
        }
        else
        {
            entities = await query
                .OrderByDescending(s => s.StartTimestamp)
                .ToListAsync(cancellationToken);
        }

        // Group results by category
        var result = categories.ToDictionary(c => c, c => new List<StateSpan>());

        foreach (var span in entities.Select(StateSpanMapper.ToDomainModel))
            result[span.Category].Add(span);

        return result;
    }

    private static IQueryable<StateSpanEntity> NewestFirst(IQueryable<StateSpanEntity> spans) =>
        spans.OrderByDescending(s => s.StartTimestamp).ThenByDescending(s => s.Id);

    #region Activity Compatibility Methods

    /// <summary>
    /// Get state spans that represent Activity records (Exercise, Sleep, Illness, Travel categories)
    /// </summary>
    /// <param name="count">The maximum number of spans to return.</param>
    /// <param name="skip">The number of spans to skip.</param>
    /// <param name="from">The earliest start time to include, inclusive.</param>
    /// <param name="to">The latest start time to include, inclusive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of state spans representing activities.</returns>
    public async Task<IEnumerable<StateSpan>> GetActivityStateSpansAsync(
        int count = 10,
        int skip = 0,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = db.StateSpans.AsNoTracking()
            .Where(s => ActivityCategories.Contains(s.Category))
            .InWindow(s => s.StartTimestamp, from, to)
            .ExcludeDuplicates();

        var entities = await query
            .OrderByDescending(s => s.StartTimestamp)
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(StateSpanMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public Task<DateTime?> GetLatestActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    ) => GetLatestStartAsync(source, ActivityCategories, cancellationToken);

    /// <inheritdoc />
    public Task<DateTime?> GetLatestNonActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    ) => GetLatestStartAsync(source, NonActivityCategories, cancellationToken);

    /// <summary>
    /// One <c>max</c> probe per category, because an <c>IN</c> list cannot use the min/max index
    /// transform on <c>(tenant_id, source, category, start_timestamp DESC)</c> and would walk every
    /// span of the source. The categories are a closed set, so the greatest probe is the answer.
    /// </summary>
    private async Task<DateTime?> GetLatestStartAsync(
        string source,
        IReadOnlyList<string> categories,
        CancellationToken cancellationToken
    )
    {
        await using var ctx = await _contexts.CreateAsync(cancellationToken);
        var arms = categories.Select((_, i) => $$"""
            SELECT max(s.start_timestamp) AS v FROM state_spans s
            WHERE s.tenant_id = {0} AND s.deleted_at IS NULL
              AND s.source = {1} AND s.category = {{{i + 2}}}
            """);
        var sql = $"SELECT max(p.v) AS \"Value\" FROM ({string.Join(" UNION ALL ", arms)}) p";
        object[] parameters = [ctx.TenantId, source, .. categories];
        return await ctx.Database
            .SqlQueryRaw<DateTime?>(sql, parameters)
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Get a state span by ID that represents an Activity record
    /// </summary>
    /// <param name="id">The unique identifier (GUID or legacy string ID).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The activity state span, or null if not found.</returns>
    public async Task<StateSpan?> GetActivityStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.StateSpans.AsNoTracking().FirstOrDefaultAsync(
            s => s.LegacyId == id && ActivityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await db.StateSpans.AsNoTracking().FirstOrDefaultAsync(
                s => s.Id == guidId && ActivityCategories.Contains(s.Category),
                cancellationToken
            );
        }

        return entity != null ? StateSpanMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Create multiple state spans from Activities
    /// </summary>
    /// <param name="stateSpans">The collection of activity state spans to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of created activity state spans.</returns>
    public async Task<IEnumerable<StateSpan>> CreateActivitiesAsStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        CancellationToken cancellationToken = default
    ) => [.. (await UpsertBatchAsync(stateSpans.ToList(), cancellationToken))
        .Where(w => w.Outcome is not null)
        .Select(w => w.Span)];

    /// <summary>
    /// Update an existing Activity state span
    /// </summary>
    /// <param name="id">The unique identifier of the activity to update.</param>
    /// <param name="stateSpan">The updated activity state span data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated activity state span, or null if not found.</returns>
    public async Task<StateSpan?> UpdateActivityStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.StateSpans.FirstOrDefaultAsync(
            s => s.LegacyId == id && ActivityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await db.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId && ActivityCategories.Contains(s.Category),
                cancellationToken
            );
        }

        if (entity == null)
            return null;

        // An edit payload carries no record id (V4) or the id it was addressed by (V1). The stored
        // LegacyId is the key a resync matches, and the stored Source is the connector that owns the row.
        var (source, legacyId) = (entity.Source, entity.LegacyId);
        StateSpanMapper.UpdateEntity(entity, stateSpan);
        TakeEditedEnd(entity, stateSpan);
        (entity.Source, entity.LegacyId) = (source, legacyId);
        await db.SaveChangesAsync(cancellationToken);
        await ReportWrittenAsync();
        return StateSpanMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Delete an Activity state span by ID
    /// </summary>
    /// <param name="id">The unique identifier of the activity to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The spans deleted, the requested one first, with the other copies in its duplicate group.</returns>
    public async Task<IReadOnlyList<StateSpan>> DeleteActivityStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.StateSpans.FirstOrDefaultAsync(
            s => s.LegacyId == id && ActivityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await db.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId && ActivityCategories.Contains(s.Category),
                cancellationToken
            );
        }

        return entity == null ? [] : await SoftDeleteWithCopiesAsync(db, entity, cancellationToken);
    }

    #endregion
}
