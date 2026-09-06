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
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of DeviceEventEntity.</returns>
    public static DeviceEventEntity ToEntity(DeviceEvent model)
    {
        return new DeviceEventEntity
        {
            DeviceId = model.DeviceId,
            PatientDeviceId = model.PatientDeviceId,
            EventType = model.EventType.ToString(),
            Notes = model.Notes,
            SyncIdentifier = model.SyncIdentifier,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of DeviceEvent domain model.</returns>
    public static DeviceEvent ToDomainModel(DeviceEventEntity entity)
    {
        return new DeviceEvent
        {
            DeviceId = entity.DeviceId,
            PatientDeviceId = entity.PatientDeviceId,
            EventType = Enum.TryParse<DeviceEventType>(entity.EventType, ignoreCase: true, out var parsed)
                ? parsed
                : DeviceEventType.SiteChange,
            Notes = entity.Notes,
            SyncIdentifier = entity.SyncIdentifier,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(DeviceEventEntity entity, DeviceEvent model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.DeviceId = model.DeviceId;
        entity.PatientDeviceId = model.PatientDeviceId;
        entity.EventType = model.EventType.ToString();
        entity.Notes = model.Notes;
        entity.SyncIdentifier = model.SyncIdentifier;
    }
}
