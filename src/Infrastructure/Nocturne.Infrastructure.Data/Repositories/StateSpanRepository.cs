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
    ///     Categories whose spans are device-reported states: a pump that reports its mode as a
    ///     stream of short readings — a retry every couple of minutes, a mode confirmed again at
    ///     each sync — describes one continuous state in many rows. Those rows fold into one span
    ///     at write time (<see cref="TryFoldIntoNeighbourAsync"/>). Patient-entered categories
    ///     (exercise, illness, travel) are left as entered: two workouts back to back are two.
    ///     Overrides are not folded either: every override carries the same <c>Custom</c> state
    ///     and differs only by name, so two adjacent ones are as likely two overrides as one.
    /// </summary>
    private static readonly HashSet<string> FoldableCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(StateSpanCategory.PumpMode),
        nameof(StateSpanCategory.PumpConnectivity),
    };

    /// <summary>
    ///     How far apart two same-state spans may sit and still be one state. A minute absorbs the
    ///     minute rounding of an export and the second-level jitter of a device clock; a longer
    ///     gap is a real interruption the timeline should show.
    /// </summary>
    public static readonly TimeSpan FoldTolerance = TimeSpan.FromMinutes(1);

    private const string FoldedSpansKey = "foldedSpans";

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
    )
    {
        StateSpanEntity? entity = null;
        var isNew = false;

        // Check for existing by originalId
        if (!string.IsNullOrEmpty(stateSpan.OriginalId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.OriginalId == stateSpan.OriginalId,
                cancellationToken
            );

            if (entity == null)
            {
                var blocked = await FindBlockingSpanAsync(stateSpan.OriginalId, cancellationToken);
                if (blocked != null)
                    return StateSpanMapper.ToDomainModel(blocked);
            }
        }

        if (entity != null)
        {
            StateSpanMapper.UpdateEntity(entity, stateSpan);
        }
        else
        {
            var folded = await TryFoldIntoNeighbourAsync(stateSpan, cancellationToken);
            if (folded != null)
                return StateSpanMapper.ToDomainModel(folded);

            entity = StateSpanMapper.ToEntity(stateSpan);
            _context.StateSpans.Add(entity);
            isNew = true;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // For exclusive categories, close any existing open spans when a new one is inserted
        if (isNew && ExclusiveCategories.Contains(entity.Category))
        {
            // Supersession closes a PRIOR open span when a newer one starts (a missed resume/switch).
            // "Prior" is by start time, not insert order: a span that starts AFTER this one is not
            // superseded by it. Without this bound, a span inserted out of order (historical backfill
            // of a pump that reports newest-first) closes a later-starting open span at its own
            // earlier start — inverting it (end < start), and clearing a genuinely active suspension.
            var openSpansQuery = _context.StateSpans
                .Where(s =>
                    s.Category == entity.Category
                    && s.EndTimestamp == null
                    && s.Id != entity.Id
                    && s.StartTimestamp <= entity.StartTimestamp);

            // PumpMode mixes independent dimensions — Automatic/Manual loop mode vs Suspended
            // delivery — which can legitimately overlap, so only the SAME state is mutually exclusive
            // there. Other exclusive categories (Override, TemporaryTarget, Profile) supersede any
            // open span regardless of state.
            if (string.Equals(entity.Category, nameof(StateSpanCategory.PumpMode), StringComparison.OrdinalIgnoreCase))
                openSpansQuery = openSpansQuery.Where(s => s.State == entity.State);

            var openSpans = await openSpansQuery.ToListAsync(cancellationToken);

            if (openSpans.Count > 0)
            {
                foreach (var openSpan in openSpans)
                {
                    openSpan.EndTimestamp = entity.StartTimestamp;
                    openSpan.SupersededById = entity.Id;
                    openSpan.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogDebug(
                    "Superseded {Count} open {Category} span(s) with new span {NewSpanId}",
                    openSpans.Count, entity.Category, entity.Id);
            }
        }

        // Link new state spans to canonical groups for deduplication
        if (isNew)
        {
            try
            {
                var dedupInputs = new List<DeduplicationInput>
                {
                    new(
                        RecordId: entity.Id,
                        Mills: new DateTimeOffset(entity.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                        DataSource: entity.Source ?? DeduplicationInput.UnknownDataSource,
                        Criteria: MatchCriteriaMapper.From(entity)
                    )
                };

                await _deduplicationService.DeduplicateBatchAsync(RecordType.StateSpan, dedupInputs, cancellationToken);
            }
            catch (Exception ex)
            {
                // Don't fail the insert if deduplication fails
                _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "StateSpan", 1);
            }
        }

        return StateSpanMapper.ToDomainModel(entity);
    }

    /// <summary>
    ///     Absorbs <paramref name="incoming"/> into a stored span of the same category, state and
    ///     source that touches it — overlapping, or within <see cref="FoldTolerance"/> on either
    ///     side — by widening that span to cover both. Returns the widened span, or null when there
    ///     is nothing to fold into. Only connector-keyed, closed spans fold: a span without an
    ///     <c>OriginalId</c> is a person's own entry, and an open span says nothing about when the
    ///     state ended, so a later same-state reading supersedes it (see the caller) rather than
    ///     claiming the state ran unbroken in between.
    /// </summary>
    /// <remarks>
    ///     Folding is what makes a stream of short same-state readings one span, and what keeps a
    ///     re-read idempotent without remembering which ids were absorbed: a folded reading, seen
    ///     again, lies inside the span it widened and folds again to no effect. The number of
    ///     readings that widened the span is kept in its metadata.
    /// </remarks>
    private async Task<StateSpanEntity?> TryFoldIntoNeighbourAsync(StateSpan incoming, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(incoming.OriginalId) || string.IsNullOrEmpty(incoming.State)) return null;
        if (!FoldableCategories.Contains(incoming.Category.ToString())) return null;

        var category = incoming.Category.ToString();
        var start = incoming.StartTimestamp;
        var end = incoming.EndTimestamp;
        var reachStart = start - FoldTolerance;
        var reachEnd = (end ?? start) + FoldTolerance;

        var neighbour = await _context.StateSpans
            .Where(s => s.Category == category
                        && s.State == incoming.State
                        && s.Source == incoming.Source
                        && s.DeletedAt == null
                        && s.SupersededById == null
                        && s.EndTimestamp != null
                        && s.StartTimestamp <= reachEnd
                        && s.EndTimestamp >= reachStart)
            .OrderBy(s => s.StartTimestamp)
            .FirstOrDefaultAsync(cancellationToken);
        if (neighbour == null) return null;

        var widened = false;
        if (start < neighbour.StartTimestamp)
        {
            neighbour.StartTimestamp = start;
            widened = true;
        }

        if (end == null || end > neighbour.EndTimestamp)
        {
            neighbour.EndTimestamp = end;
            widened = true;
        }

        // A reading that lies inside the span already — a re-read, or a retry nested in a longer
        // report — changes nothing and is not counted.
        if (!widened) return neighbour;

        var metadata = MapperHelpers.DeserializeJson<Dictionary<string, object>>(neighbour.MetadataJson) ?? new Dictionary<string, object>();
        var foldedBefore = metadata.TryGetValue(FoldedSpansKey, out var raw) && raw is System.Text.Json.JsonElement je && je.TryGetInt32(out var n) ? n : 0;
        metadata[FoldedSpansKey] = foldedBefore + 1;
        if (metadata.ContainsKey("durationSeconds"))
            metadata["durationSeconds"] = neighbour.EndTimestamp is { } e ? (long)(e - neighbour.StartTimestamp).TotalSeconds : 0L;

        neighbour.MetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
        neighbour.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Folded {Category}/{State} span {OriginalId} into {NeighbourId} ({Start:O}..{End:O})",
            category, incoming.State, incoming.OriginalId, neighbour.Id, neighbour.StartTimestamp, neighbour.EndTimestamp);

        return neighbour;
    }

    /// <summary>
    /// The soft-deleted row, if any, that forbids re-creating <paramref name="originalId"/>.
    /// State spans are keyed by <c>OriginalId</c> where the V4 tables are keyed by
    /// <c>LegacyId</c>, so the lookup is local while the rule stays shared.
    /// </summary>
    /// <seealso cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>
    private Task<StateSpanEntity?> FindBlockingSpanAsync(
        string originalId,
        CancellationToken cancellationToken) =>
        _context.StateSpans.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == _context.TenantId && s.OriginalId == originalId)
            .WhereBlocksRecreation()
            .OrderByDescending(s => s.DeletedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<StateSpanFoldResult> FoldStoredSpansAsync(CancellationToken cancellationToken = default)
    {
        var result = new StateSpanFoldResult();
        var categories = FoldableCategories.ToList();

        var spans = await _context.StateSpans
            .Where(s => categories.Contains(s.Category)
                        && s.OriginalId != null
                        && s.EndTimestamp != null
                        && s.DeletedAt == null
                        && s.SupersededById == null)
            .OrderBy(s => s.Category).ThenBy(s => s.Source).ThenBy(s => s.State).ThenBy(s => s.StartTimestamp)
            .ToListAsync(cancellationToken);
        result.Examined = spans.Count;

        var now = DateTime.UtcNow;
        foreach (var run in spans.GroupBy(s => (s.Category, s.Source, s.State)))
        {
            StateSpanEntity? keeper = null;
            var absorbedIntoKeeper = 0;

            foreach (var span in run)
            {
                if (keeper != null && span.StartTimestamp <= keeper.EndTimestamp!.Value + FoldTolerance)
                {
                    if (span.EndTimestamp > keeper.EndTimestamp)
                        keeper.EndTimestamp = span.EndTimestamp;
                    span.DeletedAt = now;
                    absorbedIntoKeeper++;
                    result.Removed++;
                    continue;
                }

                if (keeper != null && absorbedIntoKeeper > 0)
                    MarkFolded(keeper, absorbedIntoKeeper, now, result);

                keeper = span;
                absorbedIntoKeeper = 0;
            }

            if (keeper != null && absorbedIntoKeeper > 0)
                MarkFolded(keeper, absorbedIntoKeeper, now, result);
        }

        if (result.Removed > 0)
            await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Folded stored state spans: {Examined} examined, {Widened} widened, {Removed} removed",
            result.Examined, result.Widened, result.Removed);
        return result;
    }

    private static void MarkFolded(StateSpanEntity keeper, int absorbed, DateTime now, StateSpanFoldResult result)
    {
        var metadata = MapperHelpers.DeserializeJson<Dictionary<string, object>>(keeper.MetadataJson) ?? new Dictionary<string, object>();
        var before = metadata.TryGetValue(FoldedSpansKey, out var raw) && raw is System.Text.Json.JsonElement je && je.TryGetInt32(out var n) ? n : 0;
        metadata[FoldedSpansKey] = before + absorbed;
        if (metadata.ContainsKey("durationSeconds"))
            metadata["durationSeconds"] = (long)(keeper.EndTimestamp!.Value - keeper.StartTimestamp).TotalSeconds;
        keeper.MetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
        keeper.UpdatedAt = now;
        result.Widened++;
    }

    /// <summary>
    /// Bulk upsert state spans (for connector imports)
    /// </summary>
    /// <param name="stateSpans">The collection of state spans to upsert.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of spans processed.</returns>
    public async Task<int> BulkUpsertAsync(
        IEnumerable<StateSpan> stateSpans,
        CancellationToken cancellationToken = default
    )
    {
        var count = 0;
        foreach (var span in stateSpans)
        {
            await UpsertStateSpanAsync(span, cancellationToken);
            count++;
        }
        return count;
    }

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
    /// Create or update a state span from an Activity (upsert by originalId)
    /// </summary>
    /// <param name="stateSpan">The state span data to upsert.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The upserted activity state span.</returns>
    public async Task<StateSpan> UpsertActivityAsStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default
    )
    {
        // Use the standard upsert method - Activity-specific logic is in the mapper
        return await UpsertStateSpanAsync(stateSpan, cancellationToken);
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
    )
    {
        var results = new List<StateSpan>();
        foreach (var span in stateSpans)
        {
            var created = await UpsertActivityAsStateSpanAsync(span, cancellationToken);
            results.Add(created);
        }
        return results;
    }

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
