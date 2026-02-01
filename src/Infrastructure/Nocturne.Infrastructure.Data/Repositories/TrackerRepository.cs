using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Tracker operations (definitions, instances, presets)
/// </summary>
public class TrackerRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackerRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    public TrackerRepository(NocturneDbContext context)
    {
        _context = context;
    }

    #region Definitions

    /// <summary>
    /// Get all tracker definitions for a user
    /// </summary>
    public virtual async Task<List<TrackerDefinitionEntity>> GetDefinitionsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerDefinitions.Include(d => d.NotificationThresholds)
            .Where(d => d.UserId == userId || d.Visibility == TrackerVisibility.Public)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get all tracker definitions (for anonymous/public access filtering)
    /// </summary>
    public virtual async Task<List<TrackerDefinitionEntity>> GetAllDefinitionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerDefinitions.Include(d => d.NotificationThresholds)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get tracker definitions by category
    /// </summary>
    public virtual async Task<List<TrackerDefinitionEntity>> GetDefinitionsByCategoryAsync(
        string userId,
        TrackerCategory category,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerDefinitions.Include(d => d.NotificationThresholds)
            .Where(d => (d.UserId == userId || d.Visibility == TrackerVisibility.Public) && d.Category == category)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get favorite tracker definitions for quick-add
    /// </summary>
    public virtual async Task<TrackerDefinitionEntity[]> GetFavoriteDefinitionsAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerDefinitions.Where(d => d.UserId == userId && d.IsFavorite)
            .OrderBy(d => d.Name)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Get a specific tracker definition by ID
    /// </summary>
    public virtual async Task<TrackerDefinitionEntity?> GetDefinitionByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerDefinitions.Include(d => d.NotificationThresholds)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    /// <summary>
    /// Create a new tracker definition
    /// </summary>
    public virtual async Task<TrackerDefinitionEntity> CreateDefinitionAsync(
        TrackerDefinitionEntity definition,
        CancellationToken cancellationToken = default
    )
    {
        definition.Id = Guid.CreateVersion7();
        definition.CreatedAt = DateTime.UtcNow;

        _context.TrackerDefinitions.Add(definition);
        await _context.SaveChangesAsync(cancellationToken);

        return definition;
    }

    /// <summary>
    /// Update an existing tracker definition
    /// </summary>
    public virtual async Task<TrackerDefinitionEntity?> UpdateDefinitionAsync(
        Guid id,
        TrackerDefinitionEntity updated,
        CancellationToken cancellationToken = default
    )
    {
        var existing = await _context.TrackerDefinitions.FirstOrDefaultAsync(
            d => d.Id == id,
            cancellationToken
        );

        if (existing == null)
            return null;

        existing.Name = updated.Name;
        existing.Description = updated.Description;
        existing.Category = updated.Category;
        existing.Icon = updated.Icon;
        existing.TriggerEventTypes = updated.TriggerEventTypes;
        existing.TriggerNotesContains = updated.TriggerNotesContains;
        existing.LifespanHours = updated.LifespanHours;
        existing.IsFavorite = updated.IsFavorite;
        existing.DashboardVisibility = updated.DashboardVisibility;
        existing.Visibility = updated.Visibility;
        existing.StartEventType = updated.StartEventType;
        existing.CompletionEventType = updated.CompletionEventType;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        // Reload thresholds to ensure we return the complete object
        await _context.Entry(existing).Collection(d => d.NotificationThresholds).LoadAsync(cancellationToken);

        return existing;
    }

    /// <summary>
    /// Delete a tracker definition
    /// </summary>
    public virtual async Task<bool> DeleteDefinitionAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var definition = await _context.TrackerDefinitions.FirstOrDefaultAsync(
            d => d.Id == id,
            cancellationToken
        );

        if (definition == null)
            return false;

        _context.TrackerDefinitions.Remove(definition);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Update notification thresholds for a definition (replaces all existing)
    /// </summary>
    public virtual async Task UpdateNotificationThresholdsAsync(
        Guid definitionId,
        List<TrackerNotificationThresholdEntity> thresholds,
        CancellationToken cancellationToken = default
    )
    {
        // Remove existing thresholds
        var existing = await _context
            .TrackerNotificationThresholds.Where(t => t.TrackerDefinitionId == definitionId)
            .ToListAsync(cancellationToken);

        _context.TrackerNotificationThresholds.RemoveRange(existing);

        // Add new thresholds
        foreach (var threshold in thresholds)
        {
            threshold.Id = Guid.CreateVersion7();
            threshold.TrackerDefinitionId = definitionId;
            _context.TrackerNotificationThresholds.Add(threshold);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    #endregion

    #region Instances

    /// <summary>
    /// Get active (not completed) tracker instances for a user
    /// </summary>
    public virtual async Task<TrackerInstanceEntity[]> GetActiveInstancesAsync(
        string? userId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerInstances.Include(i => i.Definition)
            .Where(i => ((userId != null && i.UserId == userId) || i.Definition.Visibility == TrackerVisibility.Public) && i.CompletedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Get active (not completed) tracker instances for a specific definition
    /// </summary>
    public virtual async Task<List<TrackerInstanceEntity>> GetActiveInstancesForDefinitionAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerInstances.Include(i => i.Definition)
            .Where(i => i.DefinitionId == definitionId && i.CompletedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get completed tracker instances for history
    /// </summary>
    public virtual async Task<TrackerInstanceEntity[]> GetCompletedInstancesAsync(
        string userId,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerInstances.Include(i => i.Definition)
            .Where(i => i.UserId == userId && i.CompletedAt != null)
            .OrderByDescending(i => i.CompletedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Get upcoming instance expirations for calendar display
    /// </summary>
    public virtual async Task<TrackerInstanceEntity[]> GetUpcomingInstancesAsync(
        string? userId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    )
    {
        // Get active instances with lifespan defined
        var instances = await _context
            .TrackerInstances.Include(i => i.Definition)
            .Where(i =>
                ((userId != null && i.UserId == userId) || i.Definition.Visibility == TrackerVisibility.Public) && i.CompletedAt == null && i.Definition.LifespanHours != null
            )
            .ToArrayAsync(cancellationToken);

        // Filter by expected end date (calculated in memory)
        return instances
            .Where(i =>
            {
                var expectedEnd = i.StartedAt.AddHours(i.Definition.LifespanHours!.Value);
                return expectedEnd >= from && expectedEnd <= to;
            })
            .ToArray();
    }

    /// <summary>
    /// Get a specific tracker instance by ID
    /// </summary>
    public virtual async Task<TrackerInstanceEntity?> GetInstanceByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerInstances.Include(i => i.Definition)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    /// <summary>
    /// Start a new tracker instance
    /// </summary>
    public virtual async Task<TrackerInstanceEntity> StartInstanceAsync(
        Guid definitionId,
        string userId,
        string? startNotes = null,
        string? startTreatmentId = null,
        DateTime? startedAt = null,
        CancellationToken cancellationToken = default
    )
    {
        var instance = new TrackerInstanceEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            DefinitionId = definitionId,
            StartedAt = startedAt ?? DateTime.UtcNow,
            StartNotes = startNotes,
            StartTreatmentId = startTreatmentId,
        };

        _context.TrackerInstances.Add(instance);
        await _context.SaveChangesAsync(cancellationToken);

        // Load the definition for the returned entity
        await _context.Entry(instance).Reference(i => i.Definition).LoadAsync(cancellationToken);

        return instance;
    }

    /// <summary>
    /// Complete a tracker instance with reason and notes
    /// </summary>
    public virtual async Task<TrackerInstanceEntity?> CompleteInstanceAsync(
        Guid instanceId,
        CompletionReason reason,
        string? completionNotes = null,
        string? completeTreatmentId = null,
        DateTime? completedAt = null,
        CancellationToken cancellationToken = default
    )
    {
        var instance = await _context
            .TrackerInstances.Include(i => i.Definition)
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);

        if (instance == null)
            return null;

        instance.CompletedAt = completedAt ?? DateTime.UtcNow;
        instance.CompletionReason = reason;
        instance.CompletionNotes = completionNotes;
        instance.CompleteTreatmentId = completeTreatmentId;

        await _context.SaveChangesAsync(cancellationToken);
        return instance;
    }

    /// <summary>
    /// Acknowledge/snooze a tracker instance
    /// </summary>
    public virtual async Task<bool> AckInstanceAsync(
        Guid instanceId,
        int snoozeMins,
        CancellationToken cancellationToken = default
    )
    {
        var instance = await _context.TrackerInstances.FirstOrDefaultAsync(
            i => i.Id == instanceId,
            cancellationToken
        );

        if (instance == null)
            return false;

        instance.LastAckedAt = DateTime.UtcNow;
        instance.AckSnoozeMins = snoozeMins;

        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Delete a tracker instance
    /// </summary>
    public virtual async Task<bool> DeleteInstanceAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var instance = await _context.TrackerInstances.FirstOrDefaultAsync(
            i => i.Id == id,
            cancellationToken
        );

        if (instance == null)
            return false;

        _context.TrackerInstances.Remove(instance);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    #endregion

    #region Presets

    /// <summary>
    /// Get all presets for a user
    /// </summary>
    public virtual async Task<TrackerPresetEntity[]> GetPresetsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerPresets.Include(p => p.Definition)
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Get a specific preset by ID
    /// </summary>
    public virtual async Task<TrackerPresetEntity?> GetPresetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .TrackerPresets.Include(p => p.Definition)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    /// <summary>
    /// Create a new preset
    /// </summary>
    public virtual async Task<TrackerPresetEntity> CreatePresetAsync(
        TrackerPresetEntity preset,
        CancellationToken cancellationToken = default
    )
    {
        preset.Id = Guid.CreateVersion7();
        preset.CreatedAt = DateTime.UtcNow;

        _context.TrackerPresets.Add(preset);
        await _context.SaveChangesAsync(cancellationToken);

        return preset;
    }

    /// <summary>
    /// Apply a preset (creates a new instance from the preset's definition)
    /// </summary>
    public virtual async Task<TrackerInstanceEntity?> ApplyPresetAsync(
        Guid presetId,
        string userId,
        string? overrideNotes = null,
        CancellationToken cancellationToken = default
    )
    {
        var preset = await _context
            .TrackerPresets.Include(p => p.Definition)
            .FirstOrDefaultAsync(p => p.Id == presetId, cancellationToken);

        if (preset == null)
            return null;

        var notes = overrideNotes ?? preset.DefaultStartNotes;
        return await StartInstanceAsync(
            preset.DefinitionId,
            userId,
            notes,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Delete a preset
    /// </summary>
    public virtual async Task<bool> DeletePresetAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var preset = await _context.TrackerPresets.FirstOrDefaultAsync(
            p => p.Id == id,
            cancellationToken
        );

        if (preset == null)
            return false;

        _context.TrackerPresets.Remove(preset);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    #endregion
}
