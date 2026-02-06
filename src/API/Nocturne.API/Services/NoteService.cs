using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories.Interfaces;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service implementation for note operations including checklists, attachments, and tracker/state span linking
/// </summary>
public class NoteService : INoteService
{
    private readonly INoteRepository _repository;
    private readonly ILogger<NoteService> _logger;

    /// <summary>
    /// Maximum attachment size in bytes (5MB)
    /// </summary>
    private const long MaxAttachmentSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Allowed MIME types for attachments
    /// </summary>
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        "image/svg+xml",
        // Documents
        "application/pdf",
        "text/plain",
        "text/csv",
        // JSON
        "application/json",
    };

    public NoteService(
        INoteRepository repository,
        ILogger<NoteService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Note> CreateNoteAsync(Note note, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating note for user {UserId} with category {Category}", note.UserId, note.Category);

        // Convert to entity (ID and timestamps are handled by repository/DbContext)
        var entity = NoteMapper.ToEntity(note);

        // Create in repository
        var createdEntity = await _repository.CreateAsync(entity, cancellationToken);

        // Fetch with all related entities to return complete model
        var completeEntity = await _repository.GetByIdAsync(createdEntity.Id, true, cancellationToken);

        var result = NoteMapper.ToModel(completeEntity!);
        _logger.LogInformation("Created note {NoteId} for user {UserId}", result.Id, result.UserId);

        return result;
    }

    /// <inheritdoc />
    public async Task<Note?> GetNoteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting note by ID: {NoteId}", id);

        var entity = await _repository.GetByIdAsync(id, true, cancellationToken);

        if (entity == null)
        {
            _logger.LogDebug("Note {NoteId} not found", id);
            return null;
        }

        return NoteMapper.ToModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Note>> GetNotesAsync(NoteQueryOptions options, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Getting notes for user {UserId} with category: {Category}, archived: {Archived}, limit: {Limit}",
            options.UserId, options.Category, options.IsArchived, options.Limit);

        var entities = await _repository.GetByUserIdAsync(options.UserId, options, cancellationToken);

        var notes = entities.Select(NoteMapper.ToModel).ToList();
        _logger.LogDebug("Retrieved {Count} notes for user {UserId}", notes.Count, options.UserId);

        return notes;
    }

    /// <inheritdoc />
    public async Task<Note> UpdateNoteAsync(Note note, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(note.Id))
        {
            throw new ArgumentException("Note ID is required for update", nameof(note));
        }

        if (!Guid.TryParse(note.Id, out var noteId))
        {
            throw new ArgumentException("Invalid note ID format", nameof(note));
        }

        _logger.LogDebug("Updating note {NoteId}", note.Id);

        // Fetch existing entity
        var existingEntity = await _repository.GetByIdAsync(noteId, true, cancellationToken);
        if (existingEntity == null)
        {
            throw new KeyNotFoundException($"Note {note.Id} not found");
        }

        // Update entity with new values
        NoteMapper.UpdateEntity(existingEntity, note);

        // Save changes
        var updatedEntity = await _repository.UpdateAsync(existingEntity, cancellationToken);

        // Fetch fresh with all related entities
        var completeEntity = await _repository.GetByIdAsync(updatedEntity.Id, true, cancellationToken);

        var result = NoteMapper.ToModel(completeEntity!);
        _logger.LogInformation("Updated note {NoteId}", result.Id);

        return result;
    }

    /// <inheritdoc />
    public async Task DeleteNoteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting note {NoteId}", id);

        await _repository.DeleteAsync(id, cancellationToken);

        _logger.LogInformation("Deleted note {NoteId}", id);
    }

    /// <inheritdoc />
    public async Task<Note> ArchiveNoteAsync(Guid id, bool archive, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("{Action} note {NoteId}", archive ? "Archiving" : "Unarchiving", id);

        var entity = await _repository.GetByIdAsync(id, true, cancellationToken);
        if (entity == null)
        {
            throw new KeyNotFoundException($"Note {id} not found");
        }

        entity.IsArchived = archive;
        entity.UpdatedAt = DateTime.UtcNow;

        var updatedEntity = await _repository.UpdateAsync(entity, cancellationToken);

        var result = NoteMapper.ToModel(updatedEntity);
        _logger.LogInformation("{Action} note {NoteId}", archive ? "Archived" : "Unarchived", id);

        return result;
    }

    /// <inheritdoc />
    public async Task<NoteChecklistItem> ToggleChecklistItemAsync(Guid noteId, Guid itemId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Toggling checklist item {ItemId} in note {NoteId}", itemId, noteId);

        var item = await _repository.GetChecklistItemAsync(noteId, itemId, cancellationToken);
        if (item == null)
        {
            throw new KeyNotFoundException($"Checklist item {itemId} not found in note {noteId}");
        }

        // Toggle completion status
        item.IsCompleted = !item.IsCompleted;
        item.CompletedAt = item.IsCompleted ? DateTime.UtcNow : null;

        var updatedItem = await _repository.UpdateChecklistItemAsync(item, cancellationToken);

        var result = NoteMapper.ToChecklistItemModel(updatedItem);
        _logger.LogInformation(
            "Toggled checklist item {ItemId} in note {NoteId} to {Status}",
            itemId, noteId, result.IsCompleted ? "completed" : "incomplete");

        return result;
    }

    /// <inheritdoc />
    public async Task<NoteAttachment> AddAttachmentAsync(
        Guid noteId,
        string fileName,
        string mimeType,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding attachment {FileName} to note {NoteId}", fileName, noteId);

        // Validate size
        if (data.Length > MaxAttachmentSizeBytes)
        {
            throw new ArgumentException(
                $"Attachment exceeds maximum size of {MaxAttachmentSizeBytes / (1024 * 1024)}MB",
                nameof(data));
        }

        // Validate MIME type
        if (!AllowedMimeTypes.Contains(mimeType))
        {
            throw new ArgumentException(
                $"MIME type '{mimeType}' is not allowed. Allowed types: {string.Join(", ", AllowedMimeTypes)}",
                nameof(mimeType));
        }

        // Verify note exists
        var note = await _repository.GetByIdAsync(noteId, false, cancellationToken);
        if (note == null)
        {
            throw new KeyNotFoundException($"Note {noteId} not found");
        }

        var attachmentEntity = new NoteAttachmentEntity
        {
            NoteId = noteId,
            FileName = fileName,
            MimeType = mimeType,
            Data = data,
            SizeBytes = data.Length,
        };

        var createdAttachment = await _repository.AddAttachmentAsync(attachmentEntity, cancellationToken);

        var result = NoteMapper.ToAttachmentModel(createdAttachment);
        _logger.LogInformation(
            "Added attachment {AttachmentId} ({FileName}, {Size} bytes) to note {NoteId}",
            result.Id, fileName, data.Length, noteId);

        return result;
    }

    /// <inheritdoc />
    public async Task<(byte[] Data, string MimeType, string FileName)> GetAttachmentAsync(
        Guid noteId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting attachment {AttachmentId} from note {NoteId}", attachmentId, noteId);

        var attachment = await _repository.GetAttachmentAsync(noteId, attachmentId, cancellationToken);
        if (attachment == null)
        {
            throw new KeyNotFoundException($"Attachment {attachmentId} not found in note {noteId}");
        }

        return (attachment.Data, attachment.MimeType, attachment.FileName);
    }

    /// <inheritdoc />
    public async Task DeleteAttachmentAsync(Guid noteId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting attachment {AttachmentId} from note {NoteId}", attachmentId, noteId);

        await _repository.DeleteAttachmentAsync(noteId, attachmentId, cancellationToken);

        _logger.LogInformation("Deleted attachment {AttachmentId} from note {NoteId}", attachmentId, noteId);
    }

    /// <inheritdoc />
    public async Task<NoteTrackerLink> LinkTrackerAsync(
        Guid noteId,
        Guid trackerDefinitionId,
        List<NoteTrackerThreshold> thresholds,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Linking tracker {TrackerId} to note {NoteId}", trackerDefinitionId, noteId);

        // Verify note exists
        var note = await _repository.GetByIdAsync(noteId, false, cancellationToken);
        if (note == null)
        {
            throw new KeyNotFoundException($"Note {noteId} not found");
        }

        var linkEntity = new NoteTrackerLinkEntity
        {
            NoteId = noteId,
            TrackerDefinitionId = trackerDefinitionId,
        };

        // Add thresholds to the link entity BEFORE persisting so they are saved together
        foreach (var threshold in thresholds)
        {
            var thresholdEntity = NoteMapper.ToThresholdEntity(threshold, Guid.Empty);
            linkEntity.Thresholds.Add(thresholdEntity);
        }

        var createdLink = await _repository.AddTrackerLinkAsync(linkEntity, cancellationToken);

        var result = NoteMapper.ToTrackerLinkModel(createdLink);
        _logger.LogInformation(
            "Linked tracker {TrackerId} to note {NoteId} with {ThresholdCount} thresholds",
            trackerDefinitionId, noteId, thresholds.Count);

        return result;
    }

    /// <inheritdoc />
    public async Task UnlinkTrackerAsync(Guid noteId, Guid linkId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Unlinking tracker link {LinkId} from note {NoteId}", linkId, noteId);

        await _repository.DeleteTrackerLinkAsync(noteId, linkId, cancellationToken);

        _logger.LogInformation("Unlinked tracker link {LinkId} from note {NoteId}", linkId, noteId);
    }

    /// <inheritdoc />
    public async Task<NoteStateSpanLink> LinkStateSpanAsync(
        Guid noteId,
        Guid stateSpanId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Linking state span {StateSpanId} to note {NoteId}", stateSpanId, noteId);

        // Verify note exists
        var note = await _repository.GetByIdAsync(noteId, false, cancellationToken);
        if (note == null)
        {
            throw new KeyNotFoundException($"Note {noteId} not found");
        }

        var linkEntity = new NoteStateSpanLinkEntity
        {
            NoteId = noteId,
            StateSpanId = stateSpanId,
        };

        var createdLink = await _repository.AddStateSpanLinkAsync(linkEntity, cancellationToken);

        var result = NoteMapper.ToStateSpanLinkModel(createdLink);
        _logger.LogInformation("Linked state span {StateSpanId} to note {NoteId}", stateSpanId, noteId);

        return result;
    }

    /// <inheritdoc />
    public async Task UnlinkStateSpanAsync(Guid noteId, Guid linkId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Unlinking state span link {LinkId} from note {NoteId}", linkId, noteId);

        await _repository.DeleteStateSpanLinkAsync(noteId, linkId, cancellationToken);

        _logger.LogInformation("Unlinked state span link {LinkId} from note {NoteId}", linkId, noteId);
    }
}
