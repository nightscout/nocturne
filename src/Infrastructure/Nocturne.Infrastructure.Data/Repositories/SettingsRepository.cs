using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for the Nightscout v3 settings collection. The rows Nocturne owns share the
/// table but are not in the collection (<see cref="SettingsKeys"/>): no method reads, replaces or
/// deletes one, and writing a document under a reserved key or onto a reserved row is refused.
/// </summary>
public class SettingsRepository : ISettingsRepository
{
    private readonly INocturneDbContextFactory _contexts;

    /// <summary>
    /// Initializes a new instance of the SettingsRepository class
    /// </summary>
    public SettingsRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await db.Settings.V3Documents().AsNoTracking().OrderBy(s => s.Key).ToListAsync(cancellationToken);

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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = db.Settings.V3Documents().AsNoTracking();

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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await FindByIdAsync(db.Settings.V3Documents().AsNoTracking(), id, cancellationToken);

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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.Settings.V3Documents().AsNoTracking().FirstOrDefaultAsync(
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var resultEntities = new List<SettingsEntity>();

        foreach (var setting in settings)
        {
            var entity = SettingsMapper.ToEntity(setting);
            if (SettingsKeys.IsReserved(entity.Key))
                throw new ArgumentException($"Settings key '{entity.Key}' is reserved.", nameof(settings));
            var entityId = entity.Id;
            var originalId = entity.OriginalId;
            var key = entity.Key;
            if (await db.Settings.AnyAsync(
                    s => (s.Id == entityId || (originalId != null && s.OriginalId == originalId))
                         && (s.Key.StartsWith(SettingsKeys.UiSettingsPrefix) || s.Key.StartsWith(SettingsKeys.ConnectorsPrefix)),
                    cancellationToken))
                throw new ArgumentException("The settings id names a reserved row.", nameof(settings));

            // A setting's identity is its key — that is what ix_settings_tenant_id_key enforces —
            // so the key claims its row before either id does. Failing that, a legacy Mongo _id
            // addresses its row through OriginalId, not the derived key.
            var existingEntity = (string.IsNullOrEmpty(key)
                ? null
                : await db.Settings.V3Documents().FirstOrDefaultAsync(s => s.Key == key, cancellationToken))
                ?? await db.Settings.V3Documents().FirstOrDefaultAsync(
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
                db.Settings.Add(entity);
                resultEntities.Add(entity);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return resultEntities.Select(SettingsMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<bool> PutSettingsAsync(
        string id,
        Settings settings,
        CancellationToken cancellationToken = default
    )
    {
        if (SettingsKeys.IsReserved(id))
            throw new ArgumentException($"Settings key '{id}' is reserved.", nameof(id));

        if (await ReplaceAsync(id, settings, cancellationToken))
        {
            return false;
        }

        settings.Id = null;
        settings.Key = id;
        settings.SrvCreated = settings.SrvModified;

        try
        {
            await using var db = await _contexts.CreateAsync(cancellationToken);
            db.Settings.Add(SettingsMapper.ToEntity(settings));
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // A concurrent PUT inserted this key first; ours replaces what it stored.
            if (await ReplaceAsync(id, settings, cancellationToken))
            {
                return false;
            }

            throw;
        }
    }

    private async Task<bool> ReplaceAsync(string id, Settings settings, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await FindByIdAsync(db.Settings.V3Documents(), id, cancellationToken);
        if (entity == null)
        {
            return false;
        }

        settings.Key = entity.Key;
        settings.SrvCreated = entity.SrvCreated?.ToUnixTimeMilliseconds() ?? settings.SrvModified;
        SettingsMapper.UpdateEntity(entity, settings);
        await db.SaveChangesAsync(cancellationToken);
        return true;
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        if (SettingsKeys.IsReserved(settings.Key))
            throw new ArgumentException($"Settings key '{settings.Key}' is reserved.", nameof(settings));

        var entity = await FindByIdAsync(db.Settings.V3Documents(), id, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        SettingsMapper.UpdateEntity(entity, settings);
        await db.SaveChangesAsync(cancellationToken);

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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await FindByIdAsync(db.Settings.V3Documents(), id, cancellationToken);

        if (entity == null)
        {
            return false;
        }

        db.Settings.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Find a setting by any id a reader may hold: a legacy Mongo <c>_id</c>, the row's GUID, or
    /// the key, which is what a v3 <c>identifier</c> names.
    /// </summary>
    private static async Task<SettingsEntity?> FindByIdAsync(
        IQueryable<SettingsEntity> settings,
        string id,
        CancellationToken cancellationToken
    )
    {
        var entity = await settings.FirstOrDefaultAsync(s => s.OriginalId == id, cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guid))
        {
            entity = await settings.FirstOrDefaultAsync(s => s.Id == guid, cancellationToken);
        }

        return entity ?? await settings.FirstOrDefaultAsync(s => s.Key == id, cancellationToken);
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = db.Settings.V3Documents().AsNoTracking();

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
