using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between Note domain models and NoteEntity database entities
/// </summary>
public static class NoteMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static NoteEntity ToEntity(Note model)
    {
        return new NoteEntity
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
            Text = model.Text,
            EventType = model.EventType,
            IsAnnouncement = model.IsAnnouncement,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static Note ToDomainModel(NoteEntity entity)
    {
        return new Note
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
            Text = entity.Text,
            EventType = entity.EventType,
            IsAnnouncement = entity.IsAnnouncement,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(NoteEntity entity, Note model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Text = model.Text;
        entity.EventType = model.EventType;
        entity.IsAnnouncement = model.IsAnnouncement;
    }
}
