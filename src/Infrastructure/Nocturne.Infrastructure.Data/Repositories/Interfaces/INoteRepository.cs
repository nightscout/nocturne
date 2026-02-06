using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories.Interfaces;

/// <summary>
/// Repository interface for Note operations
/// </summary>
public interface INoteRepository
{
    /// <summary>
    /// Create a new note
    /// </summary>
    /// <param name="entity">The note entity to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created note entity with generated ID</returns>
    Task<NoteEntity> CreateAsync(NoteEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a note by ID
    /// </summary>
    /// <param name="id">The note ID</param>
    /// <param name="includeRelated">Whether to include related entities (ChecklistItems, Attachments, TrackerLinks, StateSpanLinks)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The note entity or null if not found</returns>
    Task<NoteEntity?> GetByIdAsync(Guid id, bool includeRelated = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notes for a specific user with optional filtering
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="options">Optional query options for filtering and pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of note entities ordered by OccurredAt descending</returns>
    Task<IEnumerable<NoteEntity>> GetByUserIdAsync(Guid userId, NoteQueryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing note
    /// </summary>
    /// <param name="entity">The note entity with updated values</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated note entity</returns>
    Task<NoteEntity> UpdateAsync(NoteEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a note by ID
    /// </summary>
    /// <param name="id">The note ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific checklist item from a note
    /// </summary>
    /// <param name="noteId">The parent note ID</param>
    /// <param name="itemId">The checklist item ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The checklist item entity or null if not found</returns>
    Task<NoteChecklistItemEntity?> GetChecklistItemAsync(Guid noteId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update a checklist item
    /// </summary>
    /// <param name="item">The checklist item entity with updated values</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated checklist item entity</returns>
    Task<NoteChecklistItemEntity> UpdateChecklistItemAsync(NoteChecklistItemEntity item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add an attachment to a note
    /// </summary>
    /// <param name="attachment">The attachment entity to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created attachment entity with generated ID</returns>
    Task<NoteAttachmentEntity> AddAttachmentAsync(NoteAttachmentEntity attachment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific attachment from a note
    /// </summary>
    /// <param name="noteId">The parent note ID</param>
    /// <param name="attachmentId">The attachment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The attachment entity or null if not found</returns>
    Task<NoteAttachmentEntity?> GetAttachmentAsync(Guid noteId, Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete an attachment from a note
    /// </summary>
    /// <param name="noteId">The parent note ID</param>
    /// <param name="attachmentId">The attachment ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAttachmentAsync(Guid noteId, Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a tracker link to a note
    /// </summary>
    /// <param name="link">The tracker link entity to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created tracker link entity with generated ID</returns>
    Task<NoteTrackerLinkEntity> AddTrackerLinkAsync(NoteTrackerLinkEntity link, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a tracker link from a note
    /// </summary>
    /// <param name="noteId">The parent note ID</param>
    /// <param name="linkId">The tracker link ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteTrackerLinkAsync(Guid noteId, Guid linkId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a state span link to a note
    /// </summary>
    /// <param name="link">The state span link entity to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created state span link entity with generated ID</returns>
    Task<NoteStateSpanLinkEntity> AddStateSpanLinkAsync(NoteStateSpanLinkEntity link, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a state span link from a note
    /// </summary>
    /// <param name="noteId">The parent note ID</param>
    /// <param name="linkId">The state span link ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteStateSpanLinkAsync(Guid noteId, Guid linkId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all notes linked to a specific tracker definition
    /// </summary>
    /// <param name="trackerDefinitionId">The tracker definition ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of note entities linked to the tracker definition</returns>
    Task<IEnumerable<NoteEntity>> GetNotesLinkedToTrackerDefinitionAsync(Guid trackerDefinitionId, CancellationToken cancellationToken = default);
}
