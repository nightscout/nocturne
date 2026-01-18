using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Entry operations
/// </summary>
public class EntryRepository
{
    private readonly NocturneDbContext _context;
    private readonly IQueryParser _queryParser;
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<EntryRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the EntryRepository class
    /// </summary>
    /// <param name="context">The database context</param>
    /// <param name="queryParser">MongoDB query parser for advanced filtering</param>
    /// <param name="deduplicationService">Service for deduplicating records</param>
    /// <param name="logger">Logger instance</param>
    public EntryRepository(
        NocturneDbContext context,
        IQueryParser queryParser,
        IDeduplicationService deduplicationService,
        ILogger<EntryRepository> logger)
    {
        _context = context;
        _queryParser = queryParser;
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <summary>
    /// Get entries with optional filtering and pagination
    /// </summary>
    public async Task<IEnumerable<Entry>> GetEntriesAsync(
        string? type = null,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Entries.AsQueryable();

        // Apply type filter if specified
        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(e => e.Type == type);
        }

        // Order by Mills descending (most recent first), then apply pagination
        var entities = await query
            .OrderByDescending(e => e.Mills)
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(EntryMapper.ToDomainModel);
    }

    /// <summary>
    /// Get the most recent entry
    /// </summary>
    public async Task<Entry?> GetCurrentEntryAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _context
            .Entries.OrderByDescending(e => e.Mills)
            .FirstOrDefaultAsync(cancellationToken);

        return entity != null ? EntryMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Get a specific entry by ID
    /// </summary>
    public async Task<Entry?> GetEntryByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by OriginalId first (MongoDB ObjectId), then by GUID
        var entity = await _context.Entries.FirstOrDefaultAsync(
            e => e.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.Entries.FirstOrDefaultAsync(
                e => e.Id == guidId,
                cancellationToken
            );
        }

        return entity != null ? EntryMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Create new entries, skipping duplicates and linking to canonical groups
    /// </summary>
    public async Task<IEnumerable<Entry>> CreateEntriesAsync(
        IEnumerable<Entry> entries,
        CancellationToken cancellationToken = default
    )
    {
        var entities = entries.Select(EntryMapper.ToEntity).ToList();

        if (entities.Count == 0)
        {
            return Enumerable.Empty<Entry>();
        }

        // Get the IDs of the entities we're trying to insert
        var entityIds = entities.Select(e => e.Id).ToHashSet();

        // Check which IDs already exist in the database
        var existingIds = await _context
            .Entries.Where(e => entityIds.Contains(e.Id))
            .Select(e => e.Id)
            .ToHashSetAsync(cancellationToken);

        // Filter out entities that already exist
        var newEntities = entities.Where(e => !existingIds.Contains(e.Id)).ToList();

        if (newEntities.Count == 0)
        {
            // All entries already exist, return empty
            return Enumerable.Empty<Entry>();
        }

        _context.Entries.AddRange(newEntities);
        await _context.SaveChangesAsync(cancellationToken);

        // Link new entries to canonical groups for deduplication
        foreach (var entity in newEntities)
        {
            try
            {
                var criteria = new MatchCriteria
                {
                    GlucoseValue = entity.Sgv ?? entity.Mgdl,
                    GlucoseTolerance = 1.0 // 1 mg/dL tolerance for glucose matching
                };

                var canonicalId = await _deduplicationService.GetOrCreateCanonicalIdAsync(
                    RecordType.Entry,
                    entity.Mills,
                    criteria,
                    cancellationToken);

                await _deduplicationService.LinkRecordAsync(
                    canonicalId,
                    RecordType.Entry,
                    entity.Id,
                    entity.Mills,
                    entity.DataSource ?? "unknown",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // Don't fail the insert if deduplication fails
                _logger.LogWarning(ex, "Failed to deduplicate entry {EntryId}", entity.Id);
            }
        }

        return newEntities.Select(EntryMapper.ToDomainModel);
    }

    /// <summary>
    /// Update an existing entry
    /// </summary>
    public async Task<Entry?> UpdateEntryAsync(
        string id,
        Entry entry,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by OriginalId first (MongoDB ObjectId), then by GUID
        var entity = await _context.Entries.FirstOrDefaultAsync(
            e => e.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.Entries.FirstOrDefaultAsync(
                e => e.Id == guidId,
                cancellationToken
            );
        }

        if (entity == null)
            return null;

        EntryMapper.UpdateEntity(entity, entry);
        await _context.SaveChangesAsync(cancellationToken);

        return EntryMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Delete an entry
    /// </summary>
    public async Task<bool> DeleteEntryAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by OriginalId first (MongoDB ObjectId), then by GUID
        var entity = await _context.Entries.FirstOrDefaultAsync(
            e => e.OriginalId == id,
            cancellationToken
        );

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.Entries.FirstOrDefaultAsync(
                e => e.Id == guidId,
                cancellationToken
            );
        }

        if (entity == null)
            return false;

        _context.Entries.Remove(entity);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Delete multiple entries with optional filtering
    /// </summary>
    public async Task<long> DeleteEntriesAsync(
        string? type = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Entries.AsQueryable();

        // Apply type filter if specified
        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(e => e.Type == type);
        }

        var deletedCount = await query.ExecuteDeleteAsync(cancellationToken);
        return deletedCount;
    }

    /// <summary>
    /// Delete all entries with the specified data source
    /// </summary>
    /// <param name="dataSource">The data source to filter by (e.g., "demo-service")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of entries deleted</returns>
    public async Task<long> DeleteByDataSourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var deletedCount = await _context
            .Entries.Where(e => e.DataSource == dataSource)
            .ExecuteDeleteAsync(cancellationToken);
        return deletedCount;
    }

    /// <summary>
    /// Get entries with advanced filtering (simplified version for now)
    /// </summary>
    /// <remarks>
    /// TODO: Complex MongoDB-style query parsing is not yet implemented.
    /// Currently supports basic type and date filtering.
    /// </remarks>
    public async Task<IEnumerable<Entry>> GetEntriesWithAdvancedFilterAsync(
        string? type = null,
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        string? dateString = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Entries.AsQueryable();

        // Apply type filter if specified
        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(e => e.Type == type);
        }

        // Apply date filter if specified
        if (!string.IsNullOrEmpty(dateString) && DateTime.TryParse(dateString, out var filterDate))
        {
            var filterMills = ((DateTimeOffset)filterDate).ToUnixTimeMilliseconds();
            query = query.Where(e => e.Mills >= filterMills);
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
            query = query.OrderBy(e => e.Mills);
        }
        else
        {
            query = query.OrderByDescending(e => e.Mills);
        }

        // Apply pagination
        var entities = await query.Skip(skip).Take(count).ToListAsync(cancellationToken);

        return entities.Select(EntryMapper.ToDomainModel);
    }

    /// <summary>
    /// Count entries with optional filtering
    /// </summary>
    public async Task<long> CountEntriesAsync(
        string? findQuery = null,
        string? type = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Entries.AsQueryable();

        // Apply type filter if specified
        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(e => e.Type == type);
        }

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
