using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between PatientDevice domain models and PatientDeviceEntity database entities
/// </summary>
public static class PatientDeviceMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of PatientDeviceEntity.</returns>
    public static PatientDeviceEntity ToEntity(PatientDevice model)
    {
        return new PatientDeviceEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            DeviceCategory = model.DeviceCategory.ToString(),
            Manufacturer = model.Manufacturer,
            Model = model.Model,
            CatalogId = model.CatalogId,
            AidAlgorithm = model.AidAlgorithm?.ToString(),
            SerialNumber = model.SerialNumber,
            DeviceId = model.DeviceId,
            StartDate = model.StartDate,
            EndDate = model.EndDate,
            IsCurrent = model.IsCurrent,
            Rank = model.Rank,
            Notes = model.Notes,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of PatientDevice domain model.</returns>
    public static PatientDevice ToDomainModel(PatientDeviceEntity entity)
    {
        return new PatientDevice
        {
            Id = entity.Id,
            DeviceCategory = Enum.TryParse<DeviceCategory>(entity.DeviceCategory, ignoreCase: true, out var category)
                ? category
                : DeviceCategory.CGM,
            Manufacturer = entity.Manufacturer,
            Model = entity.Model,
            CatalogId = entity.CatalogId,
            AidAlgorithm = entity.AidAlgorithm is not null
                && Enum.TryParse<AidAlgorithm>(entity.AidAlgorithm, ignoreCase: true, out var algorithm)
                    ? algorithm
                    : null,
            SerialNumber = entity.SerialNumber,
            DeviceId = entity.DeviceId,
            StartDate = entity.StartDate,
            EndDate = entity.EndDate,
            IsCurrent = entity.IsCurrent,
            Rank = entity.Rank,
            Notes = entity.Notes,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(PatientDeviceEntity entity, PatientDevice model)
    {
        entity.DeviceCategory = model.DeviceCategory.ToString();
        entity.Manufacturer = model.Manufacturer;
        entity.Model = model.Model;
        entity.CatalogId = model.CatalogId;
        entity.AidAlgorithm = model.AidAlgorithm?.ToString();
        entity.SerialNumber = model.SerialNumber;
        entity.DeviceId = model.DeviceId;
        entity.StartDate = model.StartDate;
        entity.EndDate = model.EndDate;
        entity.IsCurrent = model.IsCurrent;
        entity.Rank = model.Rank;
        entity.Notes = model.Notes;
        entity.SysUpdatedAt = DateTime.UtcNow;
    }
}
