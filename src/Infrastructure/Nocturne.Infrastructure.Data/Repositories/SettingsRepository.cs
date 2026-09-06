using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Settings operations
/// </summary>
public class SettingsRepository : ISettingsRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the SettingsRepository class
    /// </summary>
    /// <param name="context">The database context</param>
    public SettingsRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get all settings
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of all settings.</returns>
    public async Task<IEnumerable<Settings>> GetSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var entities = await _context.Settings.AsNoTracking().OrderBy(s => s.Key).ToListAsync(cancellationToken);

        return entities.Select(SettingsMapper.ToDomainModel);
    }

    /// <summary>
    /// Get settings with advanced filtering
    /// </summary>
    /// <param name="count">The maximum number of settings to return.</param>
    /// <param name="skip">The number of settings to skip.</param>
    /// <param name="findQuery">Optional search query string.</param>
    /// <param name="reverseResults">Whether to reverse the order of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of matching settings.</returns>
    public async Task<IEnumerable<Settings>> GetSettingsWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Settings.AsNoTracking().AsQueryable();

        // Apply find query filter if specified
        if (!string.IsNullOrEmpty(findQuery))
        {
            // Simple text search across key, notes, and value
            query = query.Where(s =>
                s.Key.Contains(findQuery)
                || (s.Notes != null && s.Notes.Contains(findQuery))
                || (s.Value != null && s.Value.Contains(findQuery))
            );
        }

        // Apply ordering
        query = reverseResults ? query.OrderByDescending(s => s.Key) : query.OrderBy(s => s.Key);

        // Apply pagination
        var entities = await query.Skip(skip).Take(count).ToListAsync(cancellationToken);

        return entities.Select(SettingsMapper.ToDomainModel);
    }

    /// <summary>
    /// Get a specific setting by ID
    /// </summary>
    /// <param name="id">The unique identifier (GUID or legacy string ID).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The setting, or null if not found.</returns>
    public async Task<Settings?> GetSettingsByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by original ID first (MongoDB ObjectId)
        var entity = await _context.Settings.AsNoTracking().FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken
        );

        // If not found by original ID, try by GUID
        if (entity == null && Guid.TryParse(id, out var guid))
        {
            entity = await _context.Settings.AsNoTracking().FirstOrDefaultAsync(
                s => s.Id == guid,
                cancellationToken
            );
        }

        return entity != null ? SettingsMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Get a specific setting by key
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The setting, or null if not found.</returns>
    public async Task<Settings?> GetSettingsByKeyAsync(
        string key,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await _context.Settings.AsNoTracking().FirstOrDefaultAsync(
            s => s.Key == key,
            cancellationToken
        );

        return entity != null ? SettingsMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Create multiple settings
    /// </summary>
    /// <param name="settings">The collection of settings to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of created or updated settings.</returns>
    public async Task<IEnumerable<Settings>> CreateSettingsAsync(
        IEnumerable<Settings> settings,
        CancellationToken cancellationToken = default
    )
    {
        var resultEntities = new List<SettingsEntity>();

        foreach (var setting in settings)
        {
            var entity = SettingsMapper.ToEntity(setting);
            var entityId = entity.Id;
            var originalId = entity.OriginalId;
            var key = entity.Key;

            // A setting's identity is its key — that is what ix_settings_tenant_id_key enforces —
            // so the key claims its row before either id does. Failing that, a legacy Mongo _id
            // addresses its row through OriginalId, not the derived key.
            var existingEntity = (string.IsNullOrEmpty(key)
                ? null
                : await _context.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken))
                ?? await _context.Settings.FirstOrDefaultAsync(
                    s => s.Id == entityId || (originalId != null && s.OriginalId == originalId),
                    cancellationToken
                );

            if (existingEntity != null)
            {
                // Through the mapper rather than CurrentValues.SetValues: the stored row keeps its
                // own primary key, which the derived one need not match.
                SettingsMapper.UpdateEntity(existingEntity, setting);
                resultEntities.Add(existingEntity);
            }
            else
            {
                _context.Settings.Add(entity);
                resultEntities.Add(entity);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return resultEntities.Select(SettingsMapper.ToDomainModel);
    }

    /// <summary>
    /// Update an existing setting
    /// </summary>
    /// <param name="id">The unique identifier of the setting to update.</param>
    /// <param name="settings">The updated setting data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated setting, or null if not found.</returns>
    public async Task<Settings?> UpdateSettingsAsync(
        string id,
        Settings settings,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by original ID first (MongoDB ObjectId)
        var entity = await _context.Settings.FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken
        );

        // If not found by original ID, try by GUID
        if (entity == null && Guid.TryParse(id, out var guid))
        {
            entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Id == guid,
                cancellationToken
            );
        }

        if (entity == null)
        {
            return null;
        }

        SettingsMapper.UpdateEntity(entity, settings);
        await _context.SaveChangesAsync(cancellationToken);

        return SettingsMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Delete a setting by ID
    /// </summary>
    /// <param name="id">The unique identifier of the setting to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the setting was deleted, otherwise false.</returns>
    public async Task<bool> DeleteSettingsAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by original ID first (MongoDB ObjectId)
        var entity = await _context.Settings.FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken
        );

        // If not found by original ID, try by GUID
        if (entity == null && Guid.TryParse(id, out var guid))
        {
            entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Id == guid,
                cancellationToken
            );
        }

        if (entity == null)
        {
            return false;
        }

        _context.Settings.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Bulk delete settings with query
    /// </summary>
    /// <param name="findQuery">The search query for deletion.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<long> BulkDeleteSettingsAsync(
        string findQuery,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Settings.AsQueryable();

        // Apply find query filter
        if (!string.IsNullOrEmpty(findQuery))
        {
            // Simple text search across key, notes, and value
            query = query.Where(s =>
                s.Key.Contains(findQuery)
                || (s.Notes != null && s.Notes.Contains(findQuery))
                || (s.Value != null && s.Value.Contains(findQuery))
            );
        }

        var entities = await query.ToListAsync(cancellationToken);
        var count = entities.Count;

        if (count > 0)
        {
            _context.Settings.RemoveRange(entities);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return count;
    }

    /// <summary>
    /// Count settings
    /// </summary>
    /// <param name="findQuery">Optional search query string.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The total number of matching settings.</returns>
    public async Task<long> CountSettingsAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Settings.AsNoTracking().AsQueryable();

        // Apply find query filter if specified
        if (!string.IsNullOrEmpty(findQuery))
        {
            // Simple text search across key, notes, and value
            query = query.Where(s =>
                s.Key.Contains(findQuery)
                || (s.Notes != null && s.Notes.Contains(findQuery))
                || (s.Value != null && s.Value.Contains(findQuery))
            );
        }

        return await query.CountAsync(cancellationToken);
    }
}
