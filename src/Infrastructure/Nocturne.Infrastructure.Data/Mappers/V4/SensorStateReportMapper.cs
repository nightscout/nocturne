using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between SensorStateReport domain models and SensorStateReportEntity database entities
/// </summary>
public static class SensorStateReportMapper
{
    /// <summary>Convert domain model to database entity.</summary>
    public static SensorStateReportEntity ToEntity(SensorStateReport model) =>
        new SensorStateReportEntity
        {
            State = model.State?.ToString(),
            ReportedField = model.ReportedField,
            ReportedValue = model.ReportedValue,
        }.WithHeaderFrom(model);

    /// <summary>Convert database entity to domain model.</summary>
    public static SensorStateReport ToDomainModel(SensorStateReportEntity entity) =>
        new SensorStateReport
        {
            State = Enum.TryParse<SensorState>(entity.State, ignoreCase: false, out var state)
                    && Enum.IsDefined(state)
                    && entity.State == state.ToString()
                ? state
                : null,
            ReportedField = entity.ReportedField,
            ReportedValue = entity.ReportedValue,
        }.WithHeaderFrom(entity);

    /// <summary>Update existing entity with data from domain model.</summary>
    public static void UpdateEntity(SensorStateReportEntity entity, SensorStateReport model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.State = model.State?.ToString();
        entity.ReportedField = model.ReportedField;
        entity.ReportedValue = model.ReportedValue;
    }
}
