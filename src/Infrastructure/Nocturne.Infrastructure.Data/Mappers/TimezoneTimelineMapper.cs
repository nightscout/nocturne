using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Maps between <see cref="TimezoneTimelineEntry"/> domain models and <see cref="TimezoneTimelineEntity"/>
/// database entities.
/// </summary>
public static class TimezoneTimelineMapper
{
    /// <summary>Convert a database entity to a domain model.</summary>
    public static TimezoneTimelineEntry ToDomainModel(TimezoneTimelineEntity entity) =>
        new()
        {
            Id = entity.Id,
            // The column is stored without a time zone; surface it as an explicit wall-clock value.
            EffectiveFrom = DateTime.SpecifyKind(entity.EffectiveFrom, DateTimeKind.Unspecified),
            Timezone = entity.Timezone,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };

    /// <summary>Convert a domain model to a database entity.</summary>
    public static TimezoneTimelineEntity ToEntity(TimezoneTimelineEntry model, Guid tenantId) =>
        new()
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            TenantId = tenantId,
            EffectiveFrom = DateTime.SpecifyKind(model.EffectiveFrom, DateTimeKind.Unspecified),
            Timezone = model.Timezone,
            CreatedAt = model.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = model.UpdatedAt ?? DateTime.UtcNow,
        };
}
