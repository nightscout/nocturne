using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing therapy settings in the database.
/// </summary>
public class TherapySettingsRepository : ITherapySettingsRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<TherapySettingsRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TherapySettingsRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public TherapySettingsRepository(NocturneDbContext context, ILogger<TherapySettingsRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets therapy settings based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of therapy settings.</returns>
    public async Task<IEnumerable<TherapySettings>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    )
    {
        var query = _context.TherapySettings.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(TherapySettingsMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets therapy settings by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The therapy settings, or null if not found.</returns>
    public async Task<TherapySettings?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.TherapySettings.FindAsync([id], ct);
        return entity is null ? null : TherapySettingsMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets therapy settings by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The therapy settings, or null if not found.</returns>
    public async Task<TherapySettings?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.TherapySettings.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : TherapySettingsMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets therapy settings by profile name.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of therapy settings.</returns>
    public async Task<IEnumerable<TherapySettings>> GetByProfileNameAsync(
        string profileName,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .TherapySettings.AsNoTracking()
            .Where(e => e.ProfileName == profileName)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(TherapySettingsMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets the most recent therapy settings for a profile that was active at-or-before the given timestamp.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching therapy settings, or null if none found.</returns>
    public async Task<TherapySettings?> GetActiveAtAsync(
        string profileName, DateTime timestamp, CancellationToken ct = default)
    {
        var entity = await _context.TherapySettings
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : TherapySettingsMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new therapy settings record.
    /// </summary>
    /// <param name="model">The therapy settings to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created therapy settings.</returns>
    public async Task<TherapySettings> CreateAsync(TherapySettings model, CancellationToken ct = default)
    {
        var entity = TherapySettingsMapper.ToEntity(model);
        _context.TherapySettings.Add(entity);
        await _context.SaveChangesAsync(ct);
        return TherapySettingsMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing therapy settings record.
    /// </summary>
    /// <param name="id">The unique identifier of the settings to update.</param>
    /// <param name="model">The updated settings data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated therapy settings.</returns>
    public async Task<TherapySettings> UpdateAsync(Guid id, TherapySettings model, CancellationToken ct = default)
    {
        var entity =
            await _context.TherapySettings.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"TherapySettings {id} not found");
        TherapySettingsMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return TherapySettingsMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes therapy settings by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.TherapySettings.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"TherapySettings {id} not found");
        _context.TherapySettings.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes therapy settings by legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.TherapySettings.Where(e => e.LegacyId == legacyId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes therapy settings by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, CancellationToken ct = default)
    {
        return await _context
            .TherapySettings.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Counts therapy settings within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.TherapySettings.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets therapy settings by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of therapy settings.</returns>
    public async Task<IEnumerable<TherapySettings>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .TherapySettings.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(TherapySettingsMapper.ToDomainModel);
    }

    /// <summary>
    /// Performs a bulk creation of therapy settings records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created settings.</returns>
    public async Task<IEnumerable<TherapySettings>> BulkCreateAsync(
        IEnumerable<TherapySettings> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(TherapySettingsMapper.ToEntity).ToList();
        if (entities.Count == 0)
            return [];

        // Batch-level dedup: keep first occurrence per LegacyId
        entities = entities
            .GroupBy(e => e.LegacyId ?? e.Id.ToString())
            .Select(g => g.First())
            .ToList();

        // DB-level dedup: filter out records whose LegacyId already exists
        var legacyIds = entities
            .Where(e => !string.IsNullOrEmpty(e.LegacyId))
            .Select(e => e.LegacyId!)
            .ToHashSet();

        if (legacyIds.Count > 0)
        {
            var existingIds = await _context
                .TherapySettings.AsNoTracking()
                .Where(e => legacyIds.Contains(e.LegacyId!))
                .Select(e => e.LegacyId)
                .ToListAsync(ct);

            var existingSet = existingIds.ToHashSet();
            entities = entities
                .Where(e => string.IsNullOrEmpty(e.LegacyId) || !existingSet.Contains(e.LegacyId))
                .ToList();
        }

        if (entities.Count == 0)
            return [];

        const int batchSize = 500;
        foreach (var batch in entities.Chunk(batchSize))
        {
            _context.TherapySettings.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return entities.Select(TherapySettingsMapper.ToDomainModel);
    }
}
