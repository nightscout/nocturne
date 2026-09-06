using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between PatientInsulin domain models and PatientInsulinEntity database entities
/// </summary>
public static class PatientInsulinMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of PatientInsulinEntity.</returns>
    public static PatientInsulinEntity ToEntity(PatientInsulin model)
    {
        return new PatientInsulinEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            InsulinCategory = model.InsulinCategory.ToString(),
            Name = model.Name,
            StartDate = model.StartDate,
            EndDate = model.EndDate,
            IsCurrent = model.IsCurrent,
            Notes = model.Notes,
            FormulationId = model.FormulationId,
            Dia = model.Dia,
            Peak = model.Peak,
            Curve = model.Curve,
            Concentration = model.Concentration,
            Role = model.Role.ToString(),
            IsPrimary = model.IsPrimary,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of PatientInsulin domain model.</returns>
    public static PatientInsulin ToDomainModel(PatientInsulinEntity entity)
    {
        return new PatientInsulin
        {
            Id = entity.Id,
            InsulinCategory = Enum.TryParse<InsulinCategory>(entity.InsulinCategory, ignoreCase: true, out var category)
                ? category
                : InsulinCategory.RapidActing,
            Name = entity.Name,
            StartDate = entity.StartDate,
            EndDate = entity.EndDate,
            IsCurrent = entity.IsCurrent,
            Notes = entity.Notes,
            FormulationId = entity.FormulationId,
            Dia = entity.Dia,
            Peak = entity.Peak,
            Curve = entity.Curve,
            Concentration = entity.Concentration,
            Role = Enum.TryParse<InsulinRole>(entity.Role, ignoreCase: true, out var role)
                ? role
                : InsulinRole.Both,
            IsPrimary = entity.IsPrimary,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(PatientInsulinEntity entity, PatientInsulin model)
    {
        entity.InsulinCategory = model.InsulinCategory.ToString();
        entity.Name = model.Name;
        entity.StartDate = model.StartDate;
        entity.EndDate = model.EndDate;
        entity.IsCurrent = model.IsCurrent;
        entity.Notes = model.Notes;
        entity.FormulationId = model.FormulationId;
        entity.Dia = model.Dia;
        entity.Peak = model.Peak;
        entity.Curve = model.Curve;
        entity.Concentration = model.Concentration;
        entity.Role = model.Role.ToString();
        entity.IsPrimary = model.IsPrimary;
    }
}
