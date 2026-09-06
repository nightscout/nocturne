using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between PatientRecord domain models and PatientRecordEntity database entities
/// </summary>
public static class PatientRecordMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of PatientRecordEntity.</returns>
    public static PatientRecordEntity ToEntity(PatientRecord model)
    {
        return new PatientRecordEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            DiabetesType = model.DiabetesType?.ToString(),
            DiabetesTypeOther = model.DiabetesTypeOther,
            DiagnosisDate = model.DiagnosisDate,
            DateOfBirth = model.DateOfBirth,
            Sex = model.Sex?.ToString(),
            PreferredName = model.PreferredName,
            Pronouns = model.Pronouns,
            AvatarUrl = model.AvatarUrl,
            Timezone = model.Timezone,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of PatientRecord domain model.</returns>
    public static PatientRecord ToDomainModel(PatientRecordEntity entity)
    {
        return new PatientRecord
        {
            Id = entity.Id,
            DiabetesType = entity.DiabetesType is not null
                && Enum.TryParse<DiabetesType>(entity.DiabetesType, ignoreCase: true, out var parsed)
                    ? parsed
                    : null,
            DiabetesTypeOther = entity.DiabetesTypeOther,
            DiagnosisDate = entity.DiagnosisDate,
            DateOfBirth = entity.DateOfBirth,
            Sex = entity.Sex is not null
                && Enum.TryParse<BiologicalSex>(entity.Sex, ignoreCase: true, out var parsedSex)
                    ? parsedSex
                    : null,
            PreferredName = entity.PreferredName,
            Pronouns = entity.Pronouns,
            AvatarUrl = entity.AvatarUrl,
            Timezone = entity.Timezone,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(PatientRecordEntity entity, PatientRecord model)
    {
        entity.DiabetesType = model.DiabetesType?.ToString();
        entity.DiabetesTypeOther = model.DiabetesTypeOther;
        entity.DiagnosisDate = model.DiagnosisDate;
        entity.DateOfBirth = model.DateOfBirth;
        entity.Sex = model.Sex?.ToString();
        entity.PreferredName = model.PreferredName;
        entity.Pronouns = model.Pronouns;
        entity.AvatarUrl = model.AvatarUrl;
        entity.Timezone = model.Timezone;
    }
}
