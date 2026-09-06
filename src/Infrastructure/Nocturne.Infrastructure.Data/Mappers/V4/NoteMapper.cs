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
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of NoteEntity.</returns>
    public static NoteEntity ToEntity(Note model)
    {
        return new NoteEntity
        {
            Text = model.Text,
            EventType = model.EventType,
            IsAnnouncement = model.IsAnnouncement,
            SyncIdentifier = model.SyncIdentifier,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of Note domain model.</returns>
    public static Note ToDomainModel(NoteEntity entity)
    {
        return new Note
        {
            Text = entity.Text,
            EventType = entity.EventType,
            IsAnnouncement = entity.IsAnnouncement,
            SyncIdentifier = entity.SyncIdentifier,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(NoteEntity entity, Note model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.Text = model.Text;
        entity.EventType = model.EventType;
        entity.IsAnnouncement = model.IsAnnouncement;
        entity.SyncIdentifier = model.SyncIdentifier;
    }
}
