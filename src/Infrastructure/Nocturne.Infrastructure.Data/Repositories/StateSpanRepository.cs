using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for StateSpan operations
/// </summary>
public class StateSpanRepository : IStateSpanRepository
{
    private readonly NocturneDbContext _context;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<StateSpanRepository> _logger;

    /// <summary>
    /// Categories where only one span can be active at a time.
    /// When a new span is inserted in one of these categories, any existing open spans are closed.
    /// </summary>
    private static readonly HashSet<string> ExclusiveCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(StateSpanCategory.Override),
        nameof(StateSpanCategory.TemporaryTarget),
        nameof(StateSpanCategory.Profile),
        nameof(StateSpanCategory.PumpMode),
    };

    /// <summary>
    /// The stored <c>Category</c> values that represent v1 Activity records, as strings for
    /// translation into SQL.
    /// </summary>
    private static readonly List<string> ActivityCategories =
        ActivityStateSpanMapper.ActivityCategories.Select(c => c.ToString()).ToList();

    /// <summary>
    /// Initializes a new instance of the StateSpanRepository class
    /// </summary>
    /// <param name="context">The database context</param>
    /// <param name="deduplicationService">Service for deduplicating records</param>
    /// <param name="auditContext">The audit context for tracking mutations</param>
    /// <param name="logger">Logger instance</param>
    public StateSpanRepository(
        NocturneDbContext context,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<StateSpanRepository> logger
    )
    {
        _context = context;
        _deduplicationService = deduplicationService;
        _auditContext = auditContext;
        _logger = logger;
    }

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
        var query = BuildFilteredQuery(category, state, from, to, source, active);

        var ordered = descending
            ? query.OrderByDescending(s => s.StartTimestamp)
            : query.OrderBy(s => s.StartTimestamp);

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
        var query = BuildFilteredQuery(category, state, from, to, source, active);
        return await query.CountAsync(cancellationToken);
    }

    private IQueryable<StateSpanEntity> BuildFilteredQuery(
        StateSpanCategory? category,
        string? state,
        DateTime? from,
        DateTime? to,
        string? source,
        bool? active)
    {
        var query = _context.StateSpans.AsNoTracking().AsQueryable();

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

        query = query.ExcludeNonPrimary(_context, RecordType.StateSpan);

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
        var entity = await _context.StateSpans.AsNoTracking().FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.AsNoTracking().FirstOrDefaultAsync(
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
    ) => (await UpsertBatchAsync([stateSpan], cancellationToken))[0];

    /// <summary>
    /// Bulk upsert state spans (for connector imports)
    /// </summary>
    /// <param name="stateSpans">The collection of state spans to upsert.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of spans processed.</returns>
    public async Task<int> BulkUpsertAsync(
        IEnumerable<StateSpan> stateSpans,
        CancellationToken cancellationToken = default
    ) => (await UpsertBatchAsync(stateSpans.ToList(), cancellationToken)).Count;

    /// <summary>
    /// Upserts <paramref name="stateSpans"/> by <c>OriginalId</c> with one save, leaving the rows a
    /// save per span in input order would: each span sees every earlier one, so a repeated
    /// <c>OriginalId</c> updates the row its first occurrence inserted, and an open span arriving behind
    /// a later one it conflicts with ends at that one's start. Every row it loaded or added is detached
    /// afterwards so a long connector sync does not pay change detection over every earlier batch; only
    /// those rows, since the scoped context may also track entities the caller still holds.
    /// </summary>
    /// <returns>Per input span, the row it wrote or the soft-deleted row that blocked it.</returns>
    private async Task<List<StateSpan>> UpsertBatchAsync(
        IReadOnlyList<StateSpan> stateSpans,
        CancellationToken cancellationToken)
    {
        if (stateSpans.Count == 0)
            return [];

        var governingRows = await LoadGoverningSpansAsync(stateSpans, cancellationToken);
        var governingByOriginalId = governingRows
            .GroupBy(s => s.OriginalId!)
            .ToDictionary(g => g.Key, g => g.GoverningRow()!);
        var loaded = new HashSet<StateSpanEntity>(governingRows, ReferenceEqualityComparer.Instance);
        loaded.UnionWith(await LoadSupersedableSpansAsync(stateSpans, cancellationToken));

        var written = new List<StateSpanEntity>(stateSpans.Count);
        var inserted = new List<StateSpanEntity>();
        foreach (var stateSpan in stateSpans)
        {
            var hasOriginalId = !string.IsNullOrEmpty(stateSpan.OriginalId);
            if (hasOriginalId && governingByOriginalId.TryGetValue(stateSpan.OriginalId!, out var governing))
            {
                if (governing.DeletedAt == null)
                    StateSpanMapper.UpdateEntity(governing, stateSpan);
                written.Add(governing);
                continue;
            }

            var entity = StateSpanMapper.ToEntity(stateSpan);
            _context.StateSpans.Add(entity);
            SupersedeOpenSpans(entity, stateSpan.Metadata, loaded);
            await EndAtSuccessorAsync(entity, stateSpan.Metadata, loaded, cancellationToken);
            loaded.Add(entity);
            inserted.Add(entity);
            written.Add(entity);
            if (hasOriginalId)
                governingByOriginalId[stateSpan.OriginalId!] = entity;
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (inserted.Count > 0)
        {
            try
            {
                var dedupInputs = inserted
                    .Select(entity => new DeduplicationInput(
                        RecordId: entity.Id,
                        Mills: new DateTimeOffset(entity.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
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

        var results = written.Select(StateSpanMapper.ToDomainModel).ToList();
        foreach (var entity in loaded)
            _context.Entry(entity).State = EntityState.Detached;

        return results;
    }

    /// <summary>
    /// Every row that can govern an <c>OriginalId</c> in the batch, latest delete first: the live row,
    /// which the batch updates, or else the soft-deleted row that forbids re-creating it. State spans are keyed by
    /// <c>OriginalId</c> where the V4 tables are keyed by <c>LegacyId</c>, so the lookup is local
    /// while the rule stays shared.
    /// </summary>
    /// <seealso cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>
    private async Task<List<StateSpanEntity>> LoadGoverningSpansAsync(
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

        return await _context.StateSpans.IgnoreQueryFilters()
            .Where(s => s.TenantId == _context.TenantId && originalIds.Contains(s.OriginalId))
            .WhereBlocksRecreation()
            .OrderByDescending(s => s.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Every stored open span a new span in the batch could supersede, per
    /// <see cref="SupersedeOpenSpans"/>. Spans the batch reopens by update are not here; they are
    /// among the governing rows.
    /// </summary>
    private async Task<List<StateSpanEntity>> LoadSupersedableSpansAsync(
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
        return await _context.StateSpans
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
        // there. Other exclusive categories (Override, TemporaryTarget, Profile) supersede any
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
            open.UpdatedAt = DateTime.UtcNow;
            superseded++;
        }

        if (superseded > 0)
            _logger.LogDebug(
                "Superseded {Count} open {Category} span(s) with new span {NewSpanId}",
                superseded, entity.Category, entity.Id);
    }

    /// <summary>
    /// Ends an open span inserted behind a later, conflicting span at that span's start.
    /// </summary>
    /// <param name="batch">
    /// Rows this batch loaded or added, whose changes the store does not hold until the save. Stored
    /// rows a stored successor closed join it, so the save writes them and the batch detaches them.
    /// </param>
    private async Task EndAtSuccessorAsync(
        StateSpanEntity entity, IDictionary<string, object>? metadata,
        HashSet<StateSpanEntity> batch, CancellationToken cancellationToken)
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

        var batchIds = batch.Select(s => s.Id).ToList();
        var stored = _context.StateSpans.AsNoTracking()
            .Where(s => s.Category == entity.Category
                && s.StartTimestamp > entity.StartTimestamp
                && !batchIds.Contains(s.Id));
        if (sameStateOnly)
            stored = stored.Where(s => s.State == entity.State);
        if (successor != null)
            stored = stored.Where(s => s.StartTimestamp < successor.StartTimestamp);

        await foreach (var later in stored.OrderBy(s => s.StartTimestamp).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            if (!Follows(later)) continue;
            successor = later;
            break;
        }

        if (successor == null)
            return;

        entity.EndTimestamp = successor.StartTimestamp;
        entity.SupersededById = successor.Id;

        if (_context.Entry(successor).State != EntityState.Added)
            batch.UnionWith(await _context.StateSpans
                .Where(s => s.SupersededById == successor.Id
                    && s.EndTimestamp == successor.StartTimestamp
                    && s.StartTimestamp < entity.StartTimestamp)
                .ToListAsync(cancellationToken));

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
            earlier.UpdatedAt = DateTime.UtcNow;
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
        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId,
                cancellationToken
            );
        }

        if (entity == null)
            return null;

        StateSpanMapper.UpdateEntity(entity, stateSpan);
        await _context.SaveChangesAsync(cancellationToken);
        return StateSpanMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Delete a state span
    /// </summary>
    /// <param name="id">The unique identifier of the span to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the span was deleted, otherwise false.</returns>
    public async Task<bool> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId,
                cancellationToken
            );
        }

        if (entity == null)
            return false;

        entity.DeletedAt = DateTime.UtcNow;
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
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
        var deletedCount = await _context.AuditedSoftDeleteAsync(
            _context.StateSpans.Where(s => s.Source == source), _auditContext,
            $"data_source={source}", cancellationToken);
        return deletedCount;
    }

    /// <inheritdoc />
    public async Task<PumpModeState?> GetCurrentPumpModeAsync(CancellationToken cancellationToken = default)
    {
        var pumpModeCategory = nameof(StateSpanCategory.PumpMode);

        var latest = await _context.StateSpans.AsNoTracking()
            .Where(s => s.Category == pumpModeCategory && s.EndTimestamp == null)
            .ExcludeNonPrimary(_context, RecordType.StateSpan)
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

    /// <summary>
    /// Get state spans by category
    /// </summary>
    /// <param name="category">The category to filter by.</param>
    /// <param name="from">Optional start date filter.</param>
    /// <param name="to">Optional end date filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of state spans matching the category.</returns>
    public async Task<IEnumerable<StateSpan>> GetByCategory(
        StateSpanCategory category,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default
    )
    {
        return await GetStateSpansAsync(
            category: category,
            from: from,
            to: to,
            cancellationToken: cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<StateSpan?> GetActiveAtAsync(
        StateSpanCategory category,
        string? state,
        DateTime at,
        CancellationToken cancellationToken = default)
    {
        var categoryString = category.ToString();
        var entity = await _context.StateSpans
            .AsNoTracking()
            .Where(s => s.Category == categoryString
                        && (state == null || s.State == state)
                        && s.StartTimestamp <= at
                        && (s.EndTimestamp == null || s.EndTimestamp > at))
            .OrderByDescending(s => s.StartTimestamp)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : StateSpanMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Get state spans for multiple categories in a single query (batch fetch)
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
        var categoryStrings = categories.Select(c => c.ToString()).ToList();

        var query = _context.StateSpans.AsNoTracking().Where(s => categoryStrings.Contains(s.Category));

        if (from.HasValue)
            query = query.Where(s => s.EndTimestamp == null || s.EndTimestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(s => s.StartTimestamp <= to.Value);

        var entities = await query
            .OrderByDescending(s => s.StartTimestamp)
            .ToListAsync(cancellationToken);

        // Group results by category
        var result = categories.ToDictionary(c => c, c => new List<StateSpan>());

        foreach (var entity in entities)
        {
            if (
                Enum.TryParse<StateSpanCategory>(entity.Category, true, out var category)
                && result.ContainsKey(category)
            )
            {
                result[category].Add(StateSpanMapper.ToDomainModel(entity));
            }
        }

        return result;
    }

    #region Activity Compatibility Methods

    /// <summary>
    /// Get state spans that represent Activity records (Exercise, Sleep, Illness, Travel categories)
    /// </summary>
    /// <param name="type">Optional specific activity type (state) filter.</param>
    /// <param name="count">The maximum number of spans to return.</param>
    /// <param name="skip">The number of spans to skip.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of state spans representing activities.</returns>
    public async Task<IEnumerable<StateSpan>> GetActivityStateSpansAsync(
        string? type = null,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.StateSpans.AsNoTracking().Where(s => ActivityCategories.Contains(s.Category));

        // Filter by type/state if provided
        if (!string.IsNullOrEmpty(type))
            query = query.Where(s => s.State == type);

        var entities = await query
            .OrderByDescending(s => s.StartTimestamp)
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(StateSpanMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    ) =>
        await _context.StateSpans
            .AsNoTracking()
            .Where(s => ActivityCategories.Contains(s.Category) && s.Source == source)
            .MaxAsync(s => (DateTime?)s.StartTimestamp, cancellationToken);

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
        var entity = await _context.StateSpans.AsNoTracking().FirstOrDefaultAsync(
            s => s.OriginalId == id && ActivityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.AsNoTracking().FirstOrDefaultAsync(
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
    ) => await UpsertBatchAsync(stateSpans.ToList(), cancellationToken);

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
        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id && ActivityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId && ActivityCategories.Contains(s.Category),
                cancellationToken
            );
        }

        if (entity == null)
            return null;

        StateSpanMapper.UpdateEntity(entity, stateSpan);
        await _context.SaveChangesAsync(cancellationToken);
        return StateSpanMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Delete an Activity state span by ID
    /// </summary>
    /// <param name="id">The unique identifier of the activity to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the activity was deleted, otherwise false.</returns>
    public async Task<bool> DeleteActivityStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id && ActivityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId && ActivityCategories.Contains(s.Category),
                cancellationToken
            );
        }

        if (entity == null)
            return false;

        entity.DeletedAt = DateTime.UtcNow;
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    #endregion
}
