using System.Text.Json;

using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Copies the <see cref="IV4Record"/> header between a V4 domain model and its entity, leaving each
/// type's mapper to spell out only its own fields.
/// </summary>
internal static class V4RecordHeaderMapper
{
    /// <summary>
    /// Stamp a newly built entity with the model's header, minting a UUID v7 when the model carries
    /// no id and setting both system timestamps to now.
    /// </summary>
    public static TEntity WithHeaderFrom<TEntity>(this TEntity entity, V4RecordBase model)
        where TEntity : V4TimeSeriesEntityBase
    {
        entity.Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id;
        entity.SysCreatedAt = DateTime.UtcNow;
        entity.SysUpdatedAt = DateTime.UtcNow;
        UpdateHeader(entity, model);
        return entity;
    }

    /// <summary>
    /// Fill a newly built domain model from the entity's header.
    /// </summary>
    public static TModel WithHeaderFrom<TModel>(this TModel model, V4TimeSeriesEntityBase entity)
        where TModel : V4RecordBase
    {
        model.Id = entity.Id;
        model.Timestamp = entity.Timestamp;
        model.UtcOffset = entity.UtcOffset;
        model.Device = entity.Device;
        model.App = entity.App;
        model.DataSource = entity.DataSource;
        model.CorrelationId = entity.CorrelationId;
        model.LegacyId = entity.LegacyId;
        model.CreatedAt = entity.SysCreatedAt;
        model.ModifiedAt = entity.SysUpdatedAt;
        model.AdditionalProperties = MapperHelpers.DeserializeJson<Dictionary<string, object?>>(
            entity.AdditionalPropertiesJson
        );
        return model;
    }

    /// <summary>
    /// Overwrite a tracked entity's header from the model. The id and the system timestamps are
    /// left alone: <see cref="NocturneDbContext"/> owns <c>sys_updated_at</c> on save.
    /// </summary>
    public static void UpdateHeader(V4TimeSeriesEntityBase entity, V4RecordBase model)
    {
        entity.Timestamp = model.Timestamp;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }
}
