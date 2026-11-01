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
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for StateSpan operations
/// </summary>
public class StateSpanRepository : IStateSpanRepository
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<StateSpanRepository> _logger;
    private readonly ILegacyReadCacheInvalidation? _legacyReads;

    /// <summary>
    /// Categories where only one span can be active at a time.
    /// When a new span is inserted in one of these categories, any existing open spans are closed.
    /// </summary>
    private static readonly HashSet<string> ExclusiveCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(StateSpanCategory.Override),
        nameof(StateSpanCategory.TemporaryTarget),
        nameof(StateSpanCategory.PumpMode),
        nameof(StateSpanCategory.Motion),
    };

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
    /// <param name="deduplicationService">Service for deduplicating records</param>
    /// <param name="auditContext">The audit context for tracking mutations</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="legacyReads">Optional; null clears no legacy read cache.</param>
    public StateSpanRepository(
        INocturneDbContextFactory contexts,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<StateSpanRepository> logger,
        ILegacyReadCacheInvalidation? legacyReads = null
    )
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _deduplicationService = deduplicationService;
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

        query = query.ExcludeNonPrimary(db, RecordType.StateSpan);

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
            s => s.OriginalId == id,
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
            .Where(s => (s.OriginalId != null && ids.Contains(s.OriginalId)) || guids.Contains(s.Id))
            .ToListAsync(cancellationToken);
        var byOriginalId = entities.Where(e => e.OriginalId != null)
            .GroupBy(e => e.OriginalId!).ToDictionary(g => g.Key, g => g.First());
        var byId = entities.ToDictionary(e => e.Id.ToString(), StringComparer.OrdinalIgnoreCase);

        var updated = new List<StateSpanEntity?>(stateSpans.Count);
        foreach (var stateSpan in stateSpans)
        {
            var entity = stateSpan.Id is { } id ? byOriginalId.GetValueOrDefault(id) ?? byId.GetValueOrDefault(id) : null;
            if (entity is not null)
                StateSpanMapper.UpdateEntity(entity, stateSpan);
            updated.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);
        IReadOnlyList<StateSpan?> committed = [.. updated.Select(e => e is null ? null : StateSpanMapper.ToDomainModel(e))];
        await ReportCommittedAsync(committed);
        return committed;
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
        var governingRows = await LoadGoverningSpansAsync(db, stateSpans, cancellationToken);
        var governingByOriginalId = governingRows
            .GroupBy(s => s.OriginalId!)
            .ToDictionary(g => g.Key, g => g.GoverningRow()!);
        var loaded = new HashSet<StateSpanEntity>(governingRows, ReferenceEqualityComparer.Instance);
        loaded.UnionWith(await LoadSupersedableSpansAsync(db, stateSpans, cancellationToken));
        var successors = await LoadSuccessorsAsync(db, stateSpans, loaded, cancellationToken);

        var written = new List<(StateSpanEntity Row, UpsertOutcome? Outcome)>(stateSpans.Count);
        var inserted = new List<StateSpanEntity>();
        foreach (var stateSpan in stateSpans)
        {
            var hasOriginalId = !string.IsNullOrEmpty(stateSpan.OriginalId);
            if (hasOriginalId && governingByOriginalId.TryGetValue(stateSpan.OriginalId!, out var governing))
            {
                if (governing.DeletedAt != null)
                {
                    written.Add((governing, null));
                    continue;
                }

                StateSpanMapper.UpdateEntity(governing, stateSpan);
                var entry = db.Entry(governing);
                entry.DetectChanges();
                written.Add((governing, entry.State == EntityState.Added ? UpsertOutcome.Created
                    : NocturneDbContext.WritesNothing(entry) ? UpsertOutcome.Unchanged
                    : UpsertOutcome.Updated));
                continue;
            }

            var entity = StateSpanMapper.ToEntity(stateSpan);
            db.StateSpans.Add(entity);
            SupersedeOpenSpans(entity, stateSpan.Metadata, loaded);
            await EndAtSuccessorAsync(db, entity, stateSpan.Metadata, loaded, successors, cancellationToken);
            loaded.Add(entity);
            inserted.Add(entity);
            written.Add((entity, UpsertOutcome.Created));
            if (hasOriginalId)
                governingByOriginalId[stateSpan.OriginalId!] = entity;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (inserted.Count > 0)
        {
            try
            {
                var dedupInputs = inserted
                    .Select(entity => new DeduplicationInput(
                        RecordId: entity.Id,
                        Mills: entity.StartTimestamp.ToMills(),
                        DataSource: entity.Source ?? DeduplicationInput.UnknownDataSource,
                        Criteria: MatchCriteriaMapper.From(entity)))
                    .ToList();

                await _deduplicationService.DeduplicateBatchAsync(RecordType.StateSpan, dedupInputs, cancellationToken);
            }
            catch (Exception ex)
            {
                // Don't fail the insert if deduplication fails
                _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "StateSpan", inserted.Count);
            }
        }

        IReadOnlyList<StateSpanUpsert> committed =
            [.. written.Select(w => new StateSpanUpsert(StateSpanMapper.ToDomainModel(w.Row), w.Outcome))];
        await ReportCommittedAsync(committed);
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
    /// Every row that can govern an <c>OriginalId</c> in the batch, latest delete first: the live row,
    /// which the batch updates, or else the soft-deleted row that forbids re-creating it. State spans are keyed by
    /// <c>OriginalId</c> where the V4 tables are keyed by <c>LegacyId</c>, so the lookup is local
    /// while the rule stays shared.
    /// </summary>
    /// <seealso cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>
    private static async Task<List<StateSpanEntity>> LoadGoverningSpansAsync(
        NocturneDbContext db,
        IReadOnlyList<StateSpan> stateSpans,
        CancellationToken cancellationToken)
    {
        var originalIds = stateSpans
            .Select(s => s.OriginalId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        if (originalIds.Count == 0)
            return [];

        return await db.StateSpans.IgnoreQueryFilters()
            .Where(s => s.TenantId == db.TenantId && originalIds.Contains(s.OriginalId))
            .WhereBlocksSpanRecreation()
            .OrderByDescending(s => s.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Every stored open span a new span in the batch could supersede, per
    /// <see cref="SupersedeOpenSpans"/>. Spans the batch reopens by update are not here; they are
    /// among the governing rows.
    /// </summary>
    private static async Task<List<StateSpanEntity>> LoadSupersedableSpansAsync(
        NocturneDbContext db,
        IReadOnlyList<StateSpan> stateSpans,
        CancellationToken cancellationToken)
    {
        var categories = stateSpans
            .Select(s => s.Category.ToString())
            .Where(ExclusiveCategories.Contains)
            .Distinct()
            .ToList();
        if (categories.Count == 0)
            return [];

        var latestStart = stateSpans.Max(s => s.StartTimestamp);
        return await db.StateSpans
            .Where(s => categories.Contains(s.Category)
                && s.EndTimestamp == null
                && s.StartTimestamp <= latestStart)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// For an exclusive category, closes every open span in <paramref name="candidates"/> that
    /// <paramref name="entity"/> supersedes.
    /// </summary>
    private void SupersedeOpenSpans(
        StateSpanEntity entity, IDictionary<string, object>? metadata, IEnumerable<StateSpanEntity> candidates)
    {
        if (!ExclusiveCategories.Contains(entity.Category))
            return;

        // PumpMode mixes independent dimensions (Automatic/Manual loop mode vs Suspended
        // delivery) which can legitimately overlap, so only the SAME state is mutually exclusive
        // there. Other exclusive categories (Override, TemporaryTarget, Motion) supersede any
        // open span regardless of state.
        var sameStateOnly = string.Equals(
            entity.Category, nameof(StateSpanCategory.PumpMode), StringComparison.OrdinalIgnoreCase);

        var superseded = 0;
        foreach (var open in candidates)
        {
            // Supersession closes a PRIOR open span when a newer one starts (a missed resume/switch).
            // "Prior" is by start time, not insert order: a span that starts AFTER this one is not
            // superseded by it. Without this bound, a span inserted out of order (historical backfill
            // of a pump that reports newest-first) closes a later-starting open span at its own
            // earlier start, inverting it (end < start) and clearing a genuinely active suspension.
            // One starting at the same instant would end at its own start, so it stays open too.
            if (open.DeletedAt != null
                || open.EndTimestamp != null
                || open.StartTimestamp >= entity.StartTimestamp
                || !string.Equals(open.Category, entity.Category, StringComparison.Ordinal)
                || (sameStateOnly && !string.Equals(open.State, entity.State, StringComparison.Ordinal))
                || !Supersedes(entity, metadata, open))
                continue;

            open.EndTimestamp = entity.StartTimestamp;
            open.SupersededById = entity.Id;
            superseded++;
        }

        if (superseded > 0)
            _logger.LogDebug(
                "Superseded {Count} open {Category} span(s) with new span {NewSpanId}",
                superseded, entity.Category, entity.Id);
    }

    /// <summary>Stored spans read per open span of a batch, the nearest first, as its successor candidates.</summary>
    internal const int SuccessorCandidates = 10;

    /// <summary>Open spans whose candidates one statement reads.</summary>
    private const int SuccessorLookupsPerStatement = 200;

    /// <summary>
    /// The stored spans <see cref="EndAtSuccessorAsync"/> can end an open span of the batch at, and the
    /// rows a stored successor already closed.
    /// </summary>
    /// <param name="Later">
    /// Per open span of the batch, keyed by <see cref="SuccessorKey"/>, the first
    /// <see cref="SuccessorCandidates"/> stored spans it can follow, oldest first, as the store holds them.
    /// </param>
    /// <param name="Closed">Tracked rows superseded by a candidate or by a row the batch loaded.</param>
    /// <param name="Covered">The ids whose closed rows <paramref name="Closed"/> holds.</param>
    internal sealed record Successors(
        Dictionary<(string Category, string? State, DateTime Start), List<StateSpanEntity>> Later,
        List<StateSpanEntity> Closed,
        HashSet<Guid> Covered);

    /// <summary>A span can follow only spans of its category, and in PumpMode only of its state.</summary>
    private static (string Category, string? State, DateTime Start) SuccessorKey(string category, string? state, DateTime start) =>
        (category, string.Equals(category, nameof(StateSpanCategory.PumpMode), StringComparison.OrdinalIgnoreCase) ? state : null, start);

    /// <summary>
    /// Per open span of the batch, the first <see cref="SuccessorCandidates"/> stored spans it can follow,
    /// and the rows those candidates and the batch's loaded rows closed: one statement per
    /// <see cref="SuccessorLookupsPerStatement"/> open spans, and one more.
    /// </summary>
    internal static async Task<Successors> LoadSuccessorsAsync(
        NocturneDbContext db, IReadOnlyList<StateSpan> stateSpans, IEnumerable<StateSpanEntity> loaded,
        CancellationToken cancellationToken)
    {
        var keys = stateSpans
            .Where(s => s.EndTimestamp == null && ExclusiveCategories.Contains(s.Category.ToString()))
            .Select(s => SuccessorKey(s.Category.ToString(), s.State ?? string.Empty, s.StartTimestamp))
            .Distinct()
            .ToList();
        var later = new Dictionary<(string Category, string? State, DateTime Start), List<StateSpanEntity>>();
        if (keys.Count == 0)
            return new Successors(later, [], []);

        foreach (var key in keys)
            later[key] = [];

        foreach (var chunk in keys.Chunk(SuccessorLookupsPerStatement))
        {
            IQueryable<SuccessorRow>? query = null;
            for (var i = 0; i < chunk.Length; i++)
            {
                var (category, state, start) = chunk[i];
                var lookup = i;
                var candidates = db.StateSpans.AsNoTracking()
                    .Where(s => s.Category == category && s.StartTimestamp > start);
                if (state is not null)
                    candidates = candidates.Where(s => s.State == state);
                var rows = candidates
                    .OrderBy(s => s.StartTimestamp)
                    .Take(SuccessorCandidates)
                    .Select(s => new SuccessorRow { Lookup = lookup, Span = s });
                query = query is null ? rows : query.Concat(rows);
            }

            foreach (var group in (await query!.ToListAsync(cancellationToken)).GroupBy(r => r.Lookup))
                later[chunk[group.Key]] = [.. group.Select(r => r.Span).OrderBy(s => s.StartTimestamp)];
        }

        var covered = later.Values.SelectMany(l => l).Select(s => s.Id)
            .Concat(loaded.Select(s => s.Id)).ToHashSet();
        var ids = covered.ToList();
        var closed = await db.StateSpans
            .Where(s => s.SupersededById != null && ids.Contains(s.SupersededById.Value))
            .ToListAsync(cancellationToken);
        return new Successors(later, closed, covered);
    }

    /// <summary>Set by member initialisation, which EF can translate on both sides of a union.</summary>
    private sealed class SuccessorRow
    {
        public int Lookup { get; init; }
        public StateSpanEntity Span { get; init; } = null!;
    }

    /// <summary>
    /// Ends an open span inserted behind a later, conflicting span at that span's start.
    /// </summary>
    /// <param name="batch">
    /// Rows this batch loaded or added, whose changes the store does not hold until the save. Stored
    /// rows a stored successor closed join it, so the save writes them and the batch detaches them.
    /// </param>
    /// <param name="successors">
    /// Read before the batch is applied and filtered on what the store holds, as a query per span at
    /// this point would: the batch saves nothing until it is applied. The store is read again only when
    /// every candidate read for the span is passed over.
    /// </param>
    private static async Task EndAtSuccessorAsync(
        NocturneDbContext db, StateSpanEntity entity, IDictionary<string, object>? metadata,
        HashSet<StateSpanEntity> batch, Successors successors, CancellationToken cancellationToken)
    {
        if (entity.EndTimestamp != null || !ExclusiveCategories.Contains(entity.Category))
            return;

        var sameStateOnly = string.Equals(
            entity.Category, nameof(StateSpanCategory.PumpMode), StringComparison.OrdinalIgnoreCase);
        bool Follows(StateSpanEntity s) =>
            s.DeletedAt == null
            && string.Equals(s.Category, entity.Category, StringComparison.Ordinal)
            && s.StartTimestamp > entity.StartTimestamp
            && (!sameStateOnly || string.Equals(s.State, entity.State, StringComparison.Ordinal))
            && Supersedes(entity, metadata, s);

        var successor = batch.Where(Follows).MinBy(s => s.StartTimestamp);

        var batchIds = batch.Select(s => s.Id).ToHashSet();
        var bound = successor?.StartTimestamp;
        bool Eligible(StateSpanEntity s) => !batchIds.Contains(s.Id) && (bound is null || s.StartTimestamp < bound);

        var read = successors.Later.TryGetValue(
            SuccessorKey(entity.Category, entity.State, entity.StartTimestamp), out var candidates);
        candidates ??= [];
        var stored = candidates.Where(Eligible).FirstOrDefault(Follows);
        if (stored is null
            && (!read || (candidates.Count == SuccessorCandidates && (bound is null || candidates[^1].StartTimestamp < bound))))
        {
            var after = read ? candidates[^1].StartTimestamp : entity.StartTimestamp;
            var further = db.StateSpans.AsNoTracking()
                .Where(s => s.Category == entity.Category && s.StartTimestamp > after);
            if (sameStateOnly)
                further = further.Where(s => s.State == entity.State);
            if (bound is { } before)
                further = further.Where(s => s.StartTimestamp < before);
            await foreach (var later in further.OrderBy(s => s.StartTimestamp).AsAsyncEnumerable()
                               .WithCancellation(cancellationToken))
            {
                if (!Eligible(later) || !Follows(later))
                    continue;
                stored = later;
                break;
            }
        }

        successor = stored ?? successor;
        if (successor == null)
            return;

        entity.EndTimestamp = successor.StartTimestamp;
        entity.SupersededById = successor.Id;

        if (db.Entry(successor).State != EntityState.Added)
        {
            var successorId = successor.Id;
            var successorStart = successor.StartTimestamp;
            var closed = successors.Covered.Contains(successorId)
                ? successors.Closed
                : await db.StateSpans.Where(s => s.SupersededById == successorId).ToListAsync(cancellationToken);
            batch.UnionWith(closed.Where(s =>
            {
                var original = db.Entry(s).OriginalValues;
                return original.GetValue<Guid?>(nameof(StateSpanEntity.SupersededById)) == successorId
                    && original.GetValue<DateTime?>(nameof(StateSpanEntity.EndTimestamp)) == successorStart
                    && original.GetValue<DateTime>(nameof(StateSpanEntity.StartTimestamp)) < entity.StartTimestamp;
            }));
        }

        foreach (var earlier in batch)
        {
            // Only an end the successor set moves; an uploaded end stays where the upload put it.
            if (earlier.DeletedAt != null
                || earlier.SupersededById != successor.Id
                || earlier.EndTimestamp != successor.StartTimestamp
                || earlier.StartTimestamp >= entity.StartTimestamp
                || (sameStateOnly && !string.Equals(earlier.State, entity.State, StringComparison.Ordinal))
                || !Supersedes(entity, metadata, earlier))
                continue;

            earlier.EndTimestamp = entity.StartTimestamp;
            earlier.SupersededById = entity.Id;
        }
    }

    /// <summary>
    /// Whether <paramref name="entity"/> ends <paramref name="other"/> in an exclusive category.
    /// Loop uploads one override as a treatment and as devicestatus snapshots, so a treatment span
    /// and a devicestatus span of the same override do not end each other. Two spans of one kind
    /// do, even of the same preset and whatever their sources.
    /// </summary>
    private static bool Supersedes(
        StateSpanEntity entity, IDictionary<string, object>? metadata, StateSpanEntity other) =>
        !string.Equals(entity.Category, nameof(StateSpanCategory.Override), StringComparison.OrdinalIgnoreCase)
        || !metadata.IsSameOverrideAs(MapperHelpers.DeserializeJson<Dictionary<string, object>>(other.MetadataJson));

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
            s => s.OriginalId == id,
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
        await db.SaveChangesAsync(cancellationToken);
        await ReportWrittenAsync();
        return StateSpanMapper.ToDomainModel(entity);
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
            s => s.OriginalId == id,
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
            var (saved, copies) = await DuplicateGroupPrimaries.SoftDeleteAsync(
                db, entity, RecordType.StateSpan, token);
            if (saved == 0)
                return [];

            await db.ClearSupersededByAsync([entity.Id, .. copies.Select(c => c.Id)], token);
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
        var deleted = await db.StateSpans.IgnoreQueryFilters().AsNoTracking()
            .WhereCategoryIsCurrent()
            .Where(s => s.TenantId == db.TenantId
                        && s.DeletedAt != null
                        && ((s.OriginalId != null && ids.Contains(s.OriginalId)) || rowIds.Contains(s.Id)))
            .Select(s => new { s.Id, s.OriginalId, s.Category, s.DeletedAt })
            .ToListAsync(cancellationToken);

        var resolved = new Dictionary<Guid, DeletedStateSpan>();
        foreach (var id in ids.Distinct())
        {
            var named = deleted.Where(s => s.OriginalId == id).MaxBy(s => s.DeletedAt)
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
            db.StateSpans.AddRange(stateSpans.Select(StateSpanMapper.ToEntity));
            await db.SaveChangesAsync(ct);
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
            .ExcludeNonPrimary(db, RecordType.StateSpan)
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
            .ExcludeNonPrimary(db, RecordType.StateSpan)
            .OrderByDescending(s => s.StartTimestamp)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : StateSpanMapper.ToDomainModel(entity);
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
            .ExcludeNonPrimary(db, RecordType.StateSpan);

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
                else if (ExclusiveCategories.Contains(category))
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
        var query = db.StateSpans.AsNoTracking().Where(s => ActivityCategories.Contains(s.Category));

        if (from is { } fromValue)
            query = query.Where(s => s.StartTimestamp >= fromValue);
        if (to is { } toValue)
            query = query.Where(s => s.StartTimestamp <= toValue);

        query = query.ExcludeNonPrimary(db, RecordType.StateSpan);

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
            s => s.OriginalId == id && ActivityCategories.Contains(s.Category),
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
            s => s.OriginalId == id && ActivityCategories.Contains(s.Category),
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

        StateSpanMapper.UpdateEntity(entity, stateSpan);
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
            s => s.OriginalId == id && ActivityCategories.Contains(s.Category),
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
