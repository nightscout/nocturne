using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Monitoring;

/// <summary>What a fake tracker store hands back for a definition it stores.</summary>
internal static class TrackerDefinitionStore
{
    /// <summary>
    /// <paramref name="definition"/> as stored under <paramref name="id"/>, a new one when null,
    /// with each threshold given an id of its own.
    /// </summary>
    public static TrackerDefinition Stored(this TrackerDefinition definition, Guid? id = null) =>
        TrackerDefinition.Restore(
            id ?? Guid.NewGuid(),
            definition.Name,
            definition.Category,
            definition.Mode,
            definition.CreatedBy,
            definition.Description,
            definition.Icon,
            definition.TriggerEventTypes,
            definition.EndEventTypes,
            definition.Covers,
            definition.DeviceComponent,
            definition.TriggerNotesContains,
            definition.LifespanHours,
            definition.LowReservoirUnits,
            definition.LowReservoirUrgency,
            definition.IsFavorite,
            definition.DashboardVisibility,
            definition.StartEventType,
            definition.CompletionEventType,
            definition.CreatedAt,
            definition.UpdatedAt,
            [
                .. definition.NotificationThresholds.Select(t =>
                    t.Id == Guid.Empty ? t with { Id = Guid.NewGuid() } : t),
            ]);
}
