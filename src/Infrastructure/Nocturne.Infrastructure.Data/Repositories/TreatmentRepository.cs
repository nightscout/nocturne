using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Treatment operations
/// </summary>
public class TreatmentRepository
{
    private readonly NocturneDbContext _context;
    private readonly IQueryParser _queryParser;
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<TreatmentRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the TreatmentRepository class
    /// </summary>
    /// <param name="context">The database context</param>
    /// <param name="queryParser">MongoDB query parser for advanced filtering</param>
    /// <param name="deduplicationService">Service for deduplicating records</param>
    /// <param name="logger">Logger instance</param>
    public TreatmentRepository(
        NocturneDbContext context,
        IQueryParser queryParser,
        IDeduplicationService deduplicationService,
        ILogger<TreatmentRepository> logger)
    {
        _context = context;
        _queryParser = queryParser;
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <summary>
    /// Get treatments with optional filtering and pagination
    /// </summary>
    public async Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        string? eventType = null,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Treatments.AsQueryable();

        // Apply event type filter if specified
        if (!string.IsNullOrEmpty(eventType))
        {
            query = query.Where(t => t.EventType == eventType);
        }

        // Order by Mills descending (most recent first), then apply pagination
        var entities = await query
            .OrderByDescending(t => t.Mills)
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(TreatmentMapper.ToDomainModel);
    }

    /// <summary>
    /// Get a specific treatment by ID
    /// </summary>
    public async Task<Treatment?> GetTreatmentByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by OriginalId first (MongoDB ObjectId), then by GUID
        var entity = await _context.Treatments.FirstOrDefaultAsync(
            t => t.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.Treatments.FirstOrDefaultAsync(
                t => t.Id == guidId,
                cancellationToken
            );
        }

        return entity != null ? TreatmentMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Get treatments that are meal-related within a time range
    /// </summary>
    public async Task<IReadOnlyList<Treatment>> GetMealTreatmentsInTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var fromMills = from.ToUnixTimeMilliseconds();
        var toMills = to.ToUnixTimeMilliseconds();

        var entities = await _context.Treatments
            .Where(t => t.Mills >= fromMills && t.Mills <= toMills)
            .Where(t => t.Carbs > 0 || (t.EventType != null && t.EventType.Contains("Meal")))
            .OrderBy(t => t.Mills)
            .ToListAsync(cancellationToken);

        return entities.Select(TreatmentMapper.ToDomainModel).ToList();
    }

    /// <summary>
    /// Get all treatments within a time range
    /// </summary>
    public async Task<IEnumerable<Treatment>> GetTreatmentsByTimeRangeAsync(
        long startMills,
        long endMills,
        int count = 10000,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.Treatments
            .Where(t => t.Mills >= startMills && t.Mills <= endMills)
            .OrderByDescending(t => t.Mills)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(TreatmentMapper.ToDomainModel);
    }

    /// <summary>
    /// Create a single treatment and link to canonical groups for deduplication
    /// </summary>
    public async Task<Treatment?> CreateTreatmentAsync(
        Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        var results = await CreateTreatmentsAsync([treatment], cancellationToken);
        return results.FirstOrDefault();
    }

    /// <summary>
    /// Create new treatments and link to canonical groups for deduplication
    /// </summary>
    public async Task<IEnumerable<Treatment>> CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        CancellationToken cancellationToken = default
    )
    {
        var entities = treatments.Select(TreatmentMapper.ToEntity).ToList();
        var resultEntities = new List<TreatmentEntity>();
        var newEntities = new List<TreatmentEntity>();

        foreach (var entity in entities)
        {
            // Check if a treatment with this ID already exists
            var existingEntity = await _context.Treatments.FirstOrDefaultAsync(
                t => t.Id == entity.Id,
                cancellationToken
            );

            if (existingEntity != null)
            {
                // Update existing entity instead of inserting a duplicate
                _context.Entry(existingEntity).CurrentValues.SetValues(entity);
                resultEntities.Add(existingEntity);
            }
            else
            {
                // Add new entity
                _context.Treatments.Add(entity);
                resultEntities.Add(entity);
                newEntities.Add(entity);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Link new treatments to canonical groups for deduplication
        foreach (var entity in newEntities)
        {
            try
            {
                var criteria = new MatchCriteria
                {
                    EventType = entity.EventType,
                    Insulin = entity.Insulin,
                    InsulinTolerance = 0.1, // 0.1 unit tolerance
                    Carbs = entity.Carbs,
                    CarbsTolerance = 1.0 // 1g tolerance
                };

                var canonicalId = await _deduplicationService.GetOrCreateCanonicalIdAsync(
                    RecordType.Treatment,
                    entity.Mills,
                    criteria,
                    cancellationToken);

                await _deduplicationService.LinkRecordAsync(
                    canonicalId,
                    RecordType.Treatment,
                    entity.Id,
                    entity.Mills,
                    entity.DataSource ?? "unknown",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // Don't fail the insert if deduplication fails
                _logger.LogWarning(ex, "Failed to deduplicate treatment {TreatmentId}", entity.Id);
            }
        }

        return resultEntities.Select(TreatmentMapper.ToDomainModel);
    }

    /// <summary>
    /// Update an existing treatment
    /// </summary>
    public async Task<Treatment?> UpdateTreatmentAsync(
        string id,
        Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by OriginalId first (MongoDB ObjectId), then by GUID
        var entity = await _context.Treatments.FirstOrDefaultAsync(
            t => t.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.Treatments.FirstOrDefaultAsync(
                t => t.Id == guidId,
                cancellationToken
            );
        }

        if (entity == null)
            return null;

        TreatmentMapper.UpdateEntity(entity, treatment);
        await _context.SaveChangesAsync(cancellationToken);

        return TreatmentMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Delete a treatment
    /// </summary>
    public async Task<bool> DeleteTreatmentAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by OriginalId first (MongoDB ObjectId), then by GUID
        var entity = await _context.Treatments.FirstOrDefaultAsync(
            t => t.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.Treatments.FirstOrDefaultAsync(
                t => t.Id == guidId,
                cancellationToken
            );
        }

        if (entity == null)
            return false;

        _context.Treatments.Remove(entity);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Delete multiple treatments with optional filtering
    /// </summary>
    public async Task<long> DeleteTreatmentsAsync(
        string? eventType = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Treatments.AsQueryable();

        // Apply event type filter if specified
        if (!string.IsNullOrEmpty(eventType))
        {
            query = query.Where(t => t.EventType == eventType);
        }

        var deletedCount = await query.ExecuteDeleteAsync(cancellationToken);
        return deletedCount;
    }

    /// <summary>
    /// Delete all treatments with the specified data source
    /// </summary>
    /// <param name="dataSource">The data source to filter by (e.g., "demo-service")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of treatments deleted</returns>
    public async Task<long> DeleteByDataSourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var deletedCount = await _context
            .Treatments.Where(t => t.DataSource == dataSource)
            .ExecuteDeleteAsync(cancellationToken);
        return deletedCount;
    }

    /// <summary>
    /// Get treatments with advanced filtering (simplified version for now)
    /// </summary>
    /// <remarks>
    /// TODO: Complex MongoDB-style query parsing is not yet implemented.
    /// Currently supports basic type and date filtering.
    /// </remarks>
    public async Task<IEnumerable<Treatment>> GetTreatmentsWithAdvancedFilterAsync(
        string? eventType = null,
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        string? dateString = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Treatments.AsQueryable();

        // Apply event type filter if specified
        if (!string.IsNullOrEmpty(eventType))
        {
            query = query.Where(t => t.EventType == eventType);
        }

        // Apply date filter if specified
        if (!string.IsNullOrEmpty(dateString) && DateTime.TryParse(dateString, out var filterDate))
        {
            var filterMills = ((DateTimeOffset)filterDate).ToUnixTimeMilliseconds();
            query = query.Where(t => t.Mills >= filterMills);
        }

        // Apply advanced MongoDB-style query filtering
        if (!string.IsNullOrEmpty(findQuery))
        {
            var options = new QueryOptions
            {
                DateField = "Mills",
                UseEpochDates = true,
                DefaultDateRange = TimeSpan.FromDays(4),
            };

            query = await _queryParser.ApplyQueryAsync(
                query,
                findQuery,
                options,
                cancellationToken
            );
        }
        else
        {
            // Apply default date filter when no find query is specified
            var options = new QueryOptions
            {
                DateField = "Mills",
                UseEpochDates = true,
                DefaultDateRange = TimeSpan.FromDays(4),
            };

            query = _queryParser.ApplyDefaultDateFilter(query, findQuery, dateString, options);
        }

        // Apply ordering
        if (reverseResults)
        {
            query = query.OrderBy(t => t.Mills);
        }
        else
        {
            query = query.OrderByDescending(t => t.Mills);
        }

        // Apply pagination
        var entities = await query.Skip(skip).Take(count).ToListAsync(cancellationToken);

        return entities.Select(TreatmentMapper.ToDomainModel);
    }

    /// <summary>
    /// Count treatments with optional filtering
    /// </summary>
    public async Task<long> CountTreatmentsAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Treatments.AsQueryable();

        // Apply advanced MongoDB-style query filtering
        if (!string.IsNullOrEmpty(findQuery))
        {
            var options = new QueryOptions
            {
                DateField = "Mills",
                UseEpochDates = true,
                DefaultDateRange = TimeSpan.FromDays(4),
                DisableDefaultDateFilter = true, // Count queries don't need auto date filtering
            };

            query = await _queryParser.ApplyQueryAsync(
                query,
                findQuery,
                options,
                cancellationToken
            );
        }

        return await query.CountAsync(cancellationToken);
    }
}
