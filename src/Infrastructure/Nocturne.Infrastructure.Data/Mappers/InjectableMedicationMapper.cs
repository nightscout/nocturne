using Nocturne.Core.Models.Injectables;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between InjectableMedication domain models and InjectableMedicationEntity database entities
/// </summary>
public static class InjectableMedicationMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static InjectableMedicationEntity ToEntity(InjectableMedication medication)
    {
        return new InjectableMedicationEntity
        {
            Id = medication.Id == Guid.Empty ? Guid.CreateVersion7() : medication.Id,
            Name = medication.Name,
            Category = medication.Category.ToString(),
            Concentration = medication.Concentration,
            UnitType = medication.UnitType.ToString(),
            Dia = medication.Dia,
            Onset = medication.Onset,
            Peak = medication.Peak,
            Duration = medication.Duration,
            DefaultDose = medication.DefaultDose,
            SortOrder = medication.SortOrder,
            IsArchived = medication.IsArchived,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static InjectableMedication ToDomainModel(InjectableMedicationEntity entity)
    {
        return new InjectableMedication
        {
            Id = entity.Id,
            Name = entity.Name,
            Category = ParseCategory(entity.Category),
            Concentration = entity.Concentration,
            UnitType = ParseUnitType(entity.UnitType),
            Dia = entity.Dia,
            Onset = entity.Onset,
            Peak = entity.Peak,
            Duration = entity.Duration,
            DefaultDose = entity.DefaultDose,
            SortOrder = entity.SortOrder,
            IsArchived = entity.IsArchived,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(InjectableMedicationEntity entity, InjectableMedication medication)
    {
        entity.Name = medication.Name;
        entity.Category = medication.Category.ToString();
        entity.Concentration = medication.Concentration;
        entity.UnitType = medication.UnitType.ToString();
        entity.Dia = medication.Dia;
        entity.Onset = medication.Onset;
        entity.Peak = medication.Peak;
        entity.Duration = medication.Duration;
        entity.DefaultDose = medication.DefaultDose;
        entity.SortOrder = medication.SortOrder;
        entity.IsArchived = medication.IsArchived;
        entity.SysUpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Parse string to InjectableCategory enum
    /// </summary>
    private static InjectableCategory ParseCategory(string? category)
    {
        if (string.IsNullOrEmpty(category))
            return InjectableCategory.Other;

        return Enum.TryParse<InjectableCategory>(category, ignoreCase: true, out var result)
            ? result
            : InjectableCategory.Other;
    }

    /// <summary>
    /// Parse string to UnitType enum
    /// </summary>
    private static UnitType ParseUnitType(string? unitType)
    {
        if (string.IsNullOrEmpty(unitType))
            return UnitType.Units;

        return Enum.TryParse<UnitType>(unitType, ignoreCase: true, out var result)
            ? result
            : UnitType.Units;
    }
}
