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
    };

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
        var query = _context.StateSpans.AsQueryable();

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

        // Exclude non-primary duplicates from cross-connector deduplication
        var nonPrimaryIds = _context.LinkedRecords
            .Where(lr => lr.RecordType == "statespan" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        query = query.Where(s => !nonPrimaryIds.Contains(s.Id));

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
        }

        if (entity != null)
        {
            StateSpanMapper.UpdateEntity(entity, stateSpan);
        }
        else
        {
            entity = StateSpanMapper.ToEntity(stateSpan);
            _context.StateSpans.Add(entity);
            isNew = true;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // For exclusive categories, close any existing open spans when a new one is inserted
        if (isNew && ExclusiveCategories.Contains(entity.Category))
        {
            var openSpans = await _context.StateSpans
                .Where(s =>
                    s.Category == entity.Category
                    && s.EndTimestamp == null
                    && s.Id != entity.Id)
                .ToListAsync(cancellationToken);

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
                var criteria = new MatchCriteria
                {
                    Category = Enum.TryParse<StateSpanCategory>(entity.Category, true, out var cat)
                        ? cat
                        : null,
                    State = entity.State,
                };

                var canonicalId = await _deduplicationService.GetOrCreateCanonicalIdAsync(
                    RecordType.StateSpan,
                    new DateTimeOffset(entity.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    criteria,
                    cancellationToken
                );

                await _deduplicationService.LinkRecordAsync(
                    canonicalId,
                    RecordType.StateSpan,
                    entity.Id,
                    new DateTimeOffset(entity.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    entity.Source ?? "unknown",
                    cancellationToken
                );
            }
            catch (Exception ex)
            {
                // Don't fail the insert if deduplication fails
                _logger.LogWarning(ex, "Failed to deduplicate state span {StateSpanId}", entity.Id);
            }
        }

        return StateSpanMapper.ToDomainModel(entity);
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

        _context.StateSpans.Remove(entity);
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
        var deletedCount = await _context.AuditedExecuteDeleteAsync(
            _context.StateSpans.Where(s => s.Source == source), _auditContext, cancellationToken);
        return deletedCount;
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

        var query = _context.StateSpans.Where(s => categoryStrings.Contains(s.Category));

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
        var activityCategories = ActivityStateSpanMapper
            .ActivityCategories.Select(c => c.ToString())
            .ToList();

        var query = _context.StateSpans.Where(s => activityCategories.Contains(s.Category));

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
        var activityCategories = ActivityStateSpanMapper
            .ActivityCategories.Select(c => c.ToString())
            .ToList();

        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id && activityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId && activityCategories.Contains(s.Category),
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
        var activityCategories = ActivityStateSpanMapper
            .ActivityCategories.Select(c => c.ToString())
            .ToList();

        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id && activityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId && activityCategories.Contains(s.Category),
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
        var activityCategories = ActivityStateSpanMapper
            .ActivityCategories.Select(c => c.ToString())
            .ToList();

        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id && activityCategories.Contains(s.Category),
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId && activityCategories.Contains(s.Category),
                cancellationToken
            );
        }

        if (entity == null)
            return false;

        _context.StateSpans.Remove(entity);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    #endregion
}
