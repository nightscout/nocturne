using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between DeviceEvent domain models and DeviceEventEntity database entities
/// </summary>
public static class DeviceEventMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static DeviceEventEntity ToEntity(DeviceEvent model)
    {
        return new DeviceEventEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Mills = model.Mills,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            EventType = model.EventType.ToString(),
            Notes = model.Notes,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static DeviceEvent ToDomainModel(DeviceEventEntity entity)
    {
        return new DeviceEvent
        {
            Id = entity.Id,
            Mills = entity.Mills,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            App = entity.App,
            DataSource = entity.DataSource,
            CorrelationId = entity.CorrelationId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            EventType = Enum.TryParse<DeviceEventType>(entity.EventType, ignoreCase: true, out var parsed)
                ? parsed
                : DeviceEventType.SiteChange,
            Notes = entity.Notes,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(DeviceEventEntity entity, DeviceEvent model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.EventType = model.EventType.ToString();
        entity.Notes = model.Notes;
    }
}
