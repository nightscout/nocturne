using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>Stored tracker definitions, runs and presets as the Core tracker models, and back.</summary>
public static class TrackerMapper
{
    /// <summary>The definition, with its thresholds in <c>DisplayOrder</c> when they were read with it.</summary>
    public static TrackerDefinition ToDomain(TrackerDefinitionEntity entity) => TrackerDefinition.Restore(
        id: entity.Id,
        name: entity.Name,
        category: entity.Category,
        mode: entity.Mode,
        createdBy: entity.CreatedBy,
        description: entity.Description,
        icon: entity.Icon,
        triggerEventTypes: Names(entity.TriggerEventTypes),
        endEventTypes: Names(entity.EndEventTypes),
        covers: TrackerCoverage.Parse(entity.Covers),
        deviceComponent: entity.DeviceComponent,
        triggerNotesContains: entity.TriggerNotesContains,
        lifespanHours: entity.LifespanHours,
        lowReservoirUnits: entity.LowReservoirUnits,
        lowReservoirUrgency: entity.LowReservoirUrgency,
        isFavorite: entity.IsFavorite,
        dashboardVisibility: entity.DashboardVisibility,
        startEventType: entity.StartEventType,
        completionEventType: entity.CompletionEventType,
        createdAt: entity.CreatedAt,
        updatedAt: entity.UpdatedAt,
        notificationThresholds: [.. entity.NotificationThresholds.OrderBy(t => t.DisplayOrder).Select(ToDomain)]);

    public static TrackerNotificationThreshold ToDomain(TrackerNotificationThresholdEntity entity) => new(
        entity.Id, entity.Urgency, entity.Hours, entity.Description, entity.DisplayOrder, entity.AlertRuleId);

    /// <summary>The run, with the definition it was read with.</summary>
    public static TrackerInstance ToDomain(TrackerInstanceEntity entity) => new()
    {
        Id = entity.Id,
        CreatedBy = entity.CreatedBy,
        Definition = ToDomain(entity.Definition),
        StartedAt = entity.StartedAt,
        RecordedAt = entity.RecordedAt,
        CompletedAt = entity.CompletedAt,
        ScheduledAt = entity.ScheduledAt,
        StartTreatmentId = entity.StartTreatmentId,
        CompleteTreatmentId = entity.CompleteTreatmentId,
        StartNotes = entity.StartNotes,
        CompletionNotes = entity.CompletionNotes,
        CompletionReason = entity.CompletionReason,
        LastAckedAt = entity.LastAckedAt,
        AckSnoozeMins = entity.AckSnoozeMins,
    };

    public static TrackerPreset ToDomain(TrackerPresetEntity entity) => new()
    {
        Id = entity.Id,
        CreatedBy = entity.CreatedBy,
        Name = entity.Name,
        DefinitionId = entity.DefinitionId,
        DefaultStartNotes = entity.DefaultStartNotes,
        CreatedAt = entity.CreatedAt,
        Definition = entity.Definition is { } definition ? ToDomain(definition) : null,
    };

    /// <summary>A new row of <paramref name="definition"/> and its thresholds.</summary>
    public static TrackerDefinitionEntity ToEntity(TrackerDefinition definition)
    {
        var entity = new TrackerDefinitionEntity { CreatedBy = definition.CreatedBy };
        Apply(definition, entity);
        foreach (var threshold in definition.NotificationThresholds)
            entity.NotificationThresholds.Add(ToEntity(threshold));
        return entity;
    }

    /// <summary>A new row of <paramref name="threshold"/>, its id left for the store to assign.</summary>
    public static TrackerNotificationThresholdEntity ToEntity(TrackerNotificationThreshold threshold) => new()
    {
        Urgency = threshold.Urgency,
        Hours = threshold.Hours,
        Description = threshold.Description,
        DisplayOrder = threshold.DisplayOrder,
        AlertRuleId = threshold.AlertRuleId,
    };

    /// <summary>Sets every field of <paramref name="entity"/> but its keys, creator, stamps and thresholds.</summary>
    public static void Apply(TrackerDefinition definition, TrackerDefinitionEntity entity)
    {
        entity.Name = definition.Name;
        entity.Description = definition.Description;
        entity.Category = definition.Category;
        entity.Icon = definition.Icon;
        entity.TriggerEventTypes = JsonSerializer.Serialize(definition.TriggerEventTypes);
        entity.EndEventTypes = JsonSerializer.Serialize(definition.EndEventTypes);
        entity.Covers = TrackerCoverage.Serialize(definition.Covers);
        entity.DeviceComponent = definition.DeviceComponent;
        entity.TriggerNotesContains = definition.TriggerNotesContains;
        entity.LifespanHours = definition.LifespanHours;
        entity.LowReservoirUnits = definition.LowReservoirUnits;
        entity.LowReservoirUrgency = definition.LowReservoirUrgency;
        entity.IsFavorite = definition.IsFavorite;
        entity.DashboardVisibility = definition.DashboardVisibility;
        entity.StartEventType = definition.StartEventType;
        entity.CompletionEventType = definition.CompletionEventType;
        entity.Mode = definition.Mode;
    }

    /// <summary>The names a stored JSON list holds; none when it is not a list of strings.</summary>
    private static IReadOnlyList<string> Names(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return [.. (JsonSerializer.Deserialize<List<string?>>(json) ?? []).OfType<string>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
