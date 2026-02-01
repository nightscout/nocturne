using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Domain service for note operations including checklists, attachments, and tracker/state span linking
/// </summary>
public interface INoteService
{
    /// <summary>
    /// Create a new note
    /// </summary>
    /// <param name="note">Note to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created note with assigned ID</returns>
    Task<Note> CreateNoteAsync(Note note, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific note by ID
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Note if found, null otherwise</returns>
    Task<Note?> GetNoteByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notes with optional filtering and pagination
    /// </summary>
    /// <param name="options">Query options for filtering notes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of notes matching the query options</returns>
    Task<IEnumerable<Note>> GetNotesAsync(NoteQueryOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing note
    /// </summary>
    /// <param name="note">Updated note data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated note</returns>
    Task<Note> UpdateNoteAsync(Note note, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a note
    /// </summary>
    /// <param name="id">Note ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteNoteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archive or unarchive a note
    /// </summary>
    /// <param name="id">Note ID to archive/unarchive</param>
    /// <param name="archive">True to archive, false to unarchive</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated note with new archive status</returns>
    Task<Note> ArchiveNoteAsync(Guid id, bool archive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggle the completion status of a checklist item
    /// </summary>
    /// <param name="noteId">Note ID containing the checklist item</param>
    /// <param name="itemId">Checklist item ID to toggle</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated checklist item with new completion status</returns>
    Task<NoteChecklistItem> ToggleChecklistItemAsync(Guid noteId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add an attachment to a note
    /// </summary>
    /// <param name="noteId">Note ID to attach to</param>
    /// <param name="fileName">Original file name</param>
    /// <param name="mimeType">MIME type of the attachment</param>
    /// <param name="data">File data bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created attachment metadata</returns>
    Task<NoteAttachment> AddAttachmentAsync(Guid noteId, string fileName, string mimeType, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get an attachment's data and metadata
    /// </summary>
    /// <param name="noteId">Note ID containing the attachment</param>
    /// <param name="attachmentId">Attachment ID to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the file data, MIME type, and file name</returns>
    Task<(byte[] Data, string MimeType, string FileName)> GetAttachmentAsync(Guid noteId, Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete an attachment from a note
    /// </summary>
    /// <param name="noteId">Note ID containing the attachment</param>
    /// <param name="attachmentId">Attachment ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAttachmentAsync(Guid noteId, Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Link a note to a tracker definition with notification thresholds
    /// </summary>
    /// <param name="noteId">Note ID to link</param>
    /// <param name="trackerDefinitionId">Tracker definition ID to link to</param>
    /// <param name="thresholds">Notification thresholds for this link</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created tracker link</returns>
    Task<NoteTrackerLink> LinkTrackerAsync(Guid noteId, Guid trackerDefinitionId, List<NoteTrackerThreshold> thresholds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a tracker link from a note
    /// </summary>
    /// <param name="noteId">Note ID containing the link</param>
    /// <param name="linkId">Tracker link ID to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UnlinkTrackerAsync(Guid noteId, Guid linkId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Link a note to a state span
    /// </summary>
    /// <param name="noteId">Note ID to link</param>
    /// <param name="stateSpanId">State span ID to link to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created state span link</returns>
    Task<NoteStateSpanLink> LinkStateSpanAsync(Guid noteId, Guid stateSpanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a state span link from a note
    /// </summary>
    /// <param name="noteId">Note ID containing the link</param>
    /// <param name="linkId">State span link ID to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UnlinkStateSpanAsync(Guid noteId, Guid linkId, CancellationToken cancellationToken = default);
}
