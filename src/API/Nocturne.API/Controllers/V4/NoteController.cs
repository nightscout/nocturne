using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing user notes with checklists, attachments, and links to trackers and state spans
/// </summary>
[ApiController]
[Route("api/v4/notes")]
[Authorize]
[Tags("V4 Notes")]
public class NoteController : ControllerBase
{
    private readonly INoteService _noteService;
    private readonly ILogger<NoteController> _logger;

    /// <summary>
    /// Maximum file size for attachments (5MB)
    /// </summary>
    private const long MaxAttachmentSizeBytes = 5 * 1024 * 1024;

    public NoteController(INoteService noteService, ILogger<NoteController> logger)
    {
        _noteService = noteService;
        _logger = logger;
    }

    /// <summary>
    /// Create a new note
    /// </summary>
    /// <param name="request">Note creation request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created note with assigned ID</returns>
    /// <response code="201">Note created successfully</response>
    /// <response code="400">Invalid request data</response>
    /// <response code="401">Not authenticated</response>
    [HttpPost]
    [ProducesResponseType(typeof(Note), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<Note>> CreateNote(
        [FromBody] CreateNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var note = new Note
        {
            UserId = userId.Value,
            Category = request.Category,
            Title = request.Title,
            Content = request.Content,
            OccurredAt = request.OccurredAt,
            ChecklistItems = request.ChecklistItems?.Select((item, index) => new NoteChecklistItem
            {
                Text = item.Text,
                IsCompleted = item.IsCompleted,
                SortOrder = item.SortOrder ?? index
            }).ToList() ?? new List<NoteChecklistItem>()
        };

        var created = await _noteService.CreateNoteAsync(note, cancellationToken);

        _logger.LogInformation("Created note {NoteId} for user {UserId}", created.Id, userId);

        return CreatedAtAction(nameof(GetNote), new { id = created.Id }, created);
    }

    /// <summary>
    /// List notes with optional filtering
    /// </summary>
    /// <param name="category">Filter by note category</param>
    /// <param name="isArchived">Filter by archive status</param>
    /// <param name="trackerDefinitionId">Filter by linked tracker definition</param>
    /// <param name="stateSpanId">Filter by linked state span</param>
    /// <param name="fromDate">Filter notes created on or after this date</param>
    /// <param name="toDate">Filter notes created on or before this date</param>
    /// <param name="limit">Maximum number of notes to return (default: 100)</param>
    /// <param name="offset">Number of notes to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of notes matching the filter criteria</returns>
    /// <response code="200">Notes retrieved successfully</response>
    /// <response code="401">Not authenticated</response>
    [HttpGet]
    [ProducesResponseType(typeof(Note[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<Note[]>> GetNotes(
        [FromQuery] NoteCategory? category = null,
        [FromQuery] bool? isArchived = null,
        [FromQuery] Guid? trackerDefinitionId = null,
        [FromQuery] Guid? stateSpanId = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var options = new NoteQueryOptions
        {
            UserId = userId.Value,
            Category = category,
            IsArchived = isArchived,
            TrackerDefinitionId = trackerDefinitionId,
            StateSpanId = stateSpanId,
            FromDate = fromDate,
            ToDate = toDate,
            Limit = limit,
            Offset = offset
        };

        var notes = await _noteService.GetNotesAsync(options, cancellationToken);
        return Ok(notes.ToArray());
    }

    /// <summary>
    /// Get a single note by ID
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The note with all related data (checklist, attachments metadata, links)</returns>
    /// <response code="200">Note retrieved successfully</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to access this note</response>
    /// <response code="404">Note not found</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(Note), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Note>> GetNote(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var note = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (note == null)
            return NotFound();

        if (note.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        return Ok(note);
    }

    /// <summary>
    /// Update an existing note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="request">Updated note data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated note</returns>
    /// <response code="200">Note updated successfully</response>
    /// <response code="400">Invalid request data</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to update this note</response>
    /// <response code="404">Note not found</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(Note), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Note>> UpdateNote(
        Guid id,
        [FromBody] UpdateNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        existing.Category = request.Category ?? existing.Category;
        existing.Title = request.Title ?? existing.Title;
        existing.Content = request.Content ?? existing.Content;
        existing.OccurredAt = request.OccurredAt ?? existing.OccurredAt;

        if (request.ChecklistItems != null)
        {
            existing.ChecklistItems = request.ChecklistItems.Select((item, index) => new NoteChecklistItem
            {
                Id = item.Id,
                Text = item.Text,
                IsCompleted = item.IsCompleted,
                CompletedAt = item.CompletedAt,
                SortOrder = item.SortOrder ?? index
            }).ToList();
        }

        var updated = await _noteService.UpdateNoteAsync(existing, cancellationToken);

        _logger.LogInformation("Updated note {NoteId}", id);

        return Ok(updated);
    }

    /// <summary>
    /// Delete a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Note deleted successfully</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to delete this note</response>
    /// <response code="404">Note not found</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteNote(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        await _noteService.DeleteNoteAsync(id, cancellationToken);

        _logger.LogInformation("Deleted note {NoteId}", id);

        return NoContent();
    }

    /// <summary>
    /// Toggle archive status of a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="request">Archive request with archive flag</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated note with new archive status</returns>
    /// <response code="200">Archive status updated successfully</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to modify this note</response>
    /// <response code="404">Note not found</response>
    [HttpPatch("{id:guid}/archive")]
    [ProducesResponseType(typeof(Note), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Note>> ArchiveNote(
        Guid id,
        [FromBody] ArchiveNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        var updated = await _noteService.ArchiveNoteAsync(id, request.Archive, cancellationToken);

        _logger.LogInformation("Set archive status to {Archive} for note {NoteId}", request.Archive, id);

        return Ok(updated);
    }

    /// <summary>
    /// Upload an attachment to a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="file">File to upload (max 5MB)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Attachment metadata</returns>
    /// <response code="201">Attachment uploaded successfully</response>
    /// <response code="400">Invalid file or file too large</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to modify this note</response>
    /// <response code="404">Note not found</response>
    [HttpPost("{id:guid}/attachments")]
    [ProducesResponseType(typeof(NoteAttachment), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteAttachment>> UploadAttachment(
        Guid id,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        if (file.Length > MaxAttachmentSizeBytes)
            return BadRequest($"File size exceeds maximum allowed size of {MaxAttachmentSizeBytes / (1024 * 1024)}MB");

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream, cancellationToken);
        var data = memoryStream.ToArray();

        var attachment = await _noteService.AddAttachmentAsync(
            id,
            file.FileName,
            file.ContentType,
            data,
            cancellationToken);

        _logger.LogInformation("Uploaded attachment {AttachmentId} to note {NoteId}", attachment.Id, id);

        return CreatedAtAction(
            nameof(DownloadAttachment),
            new { id, attachmentId = attachment.Id },
            attachment);
    }

    /// <summary>
    /// Download an attachment from a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="attachmentId">Attachment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File content with proper content-type</returns>
    /// <response code="200">Attachment downloaded successfully</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to access this note</response>
    /// <response code="404">Note or attachment not found</response>
    [HttpGet("{id:guid}/attachments/{attachmentId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAttachment(
        Guid id,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        try
        {
            var (data, mimeType, fileName) = await _noteService.GetAttachmentAsync(id, attachmentId, cancellationToken);
            return File(data, mimeType, fileName);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete an attachment from a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="attachmentId">Attachment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Attachment deleted successfully</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to modify this note</response>
    /// <response code="404">Note or attachment not found</response>
    [HttpDelete("{id:guid}/attachments/{attachmentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAttachment(
        Guid id,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        try
        {
            await _noteService.DeleteAttachmentAsync(id, attachmentId, cancellationToken);
            _logger.LogInformation("Deleted attachment {AttachmentId} from note {NoteId}", attachmentId, id);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Link a tracker to a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="request">Tracker link request with tracker definition ID and thresholds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created tracker link</returns>
    /// <response code="201">Tracker linked successfully</response>
    /// <response code="400">Invalid request data</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to modify this note</response>
    /// <response code="404">Note not found</response>
    [HttpPost("{id:guid}/trackers")]
    [ProducesResponseType(typeof(NoteTrackerLink), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteTrackerLink>> LinkTracker(
        Guid id,
        [FromBody] LinkTrackerRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        var thresholds = request.Thresholds?.Select(t => new NoteTrackerThreshold
        {
            HoursOffset = t.HoursOffset,
            Urgency = t.Urgency,
            Description = t.Description
        }).ToList() ?? new List<NoteTrackerThreshold>();

        var link = await _noteService.LinkTrackerAsync(
            id,
            request.TrackerDefinitionId,
            thresholds,
            cancellationToken);

        _logger.LogInformation(
            "Linked tracker {TrackerDefinitionId} to note {NoteId}",
            request.TrackerDefinitionId,
            id);

        return Created($"/api/v4/notes/{id}/trackers/{link.Id}", link);
    }

    /// <summary>
    /// Unlink a tracker from a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="linkId">Tracker link ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Tracker unlinked successfully</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to modify this note</response>
    /// <response code="404">Note or tracker link not found</response>
    [HttpDelete("{id:guid}/trackers/{linkId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnlinkTracker(
        Guid id,
        Guid linkId,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        try
        {
            await _noteService.UnlinkTrackerAsync(id, linkId, cancellationToken);
            _logger.LogInformation("Unlinked tracker link {LinkId} from note {NoteId}", linkId, id);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Link a state span to a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="request">State span link request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created state span link</returns>
    /// <response code="201">State span linked successfully</response>
    /// <response code="400">Invalid request data</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to modify this note</response>
    /// <response code="404">Note not found</response>
    [HttpPost("{id:guid}/statespans")]
    [ProducesResponseType(typeof(NoteStateSpanLink), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteStateSpanLink>> LinkStateSpan(
        Guid id,
        [FromBody] LinkStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        var link = await _noteService.LinkStateSpanAsync(id, request.StateSpanId, cancellationToken);

        _logger.LogInformation("Linked state span {StateSpanId} to note {NoteId}", request.StateSpanId, id);

        return Created($"/api/v4/notes/{id}/statespans/{link.Id}", link);
    }

    /// <summary>
    /// Unlink a state span from a note
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="linkId">State span link ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    /// <response code="204">State span unlinked successfully</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to modify this note</response>
    /// <response code="404">Note or state span link not found</response>
    [HttpDelete("{id:guid}/statespans/{linkId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnlinkStateSpan(
        Guid id,
        Guid linkId,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        try
        {
            await _noteService.UnlinkStateSpanAsync(id, linkId, cancellationToken);
            _logger.LogInformation("Unlinked state span link {LinkId} from note {NoteId}", linkId, id);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Toggle a checklist item completion status
    /// </summary>
    /// <param name="id">Note ID</param>
    /// <param name="itemId">Checklist item ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated checklist item with new completion status</returns>
    /// <response code="200">Checklist item toggled successfully</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Not authorized to modify this note</response>
    /// <response code="404">Note or checklist item not found</response>
    [HttpPatch("{id:guid}/checklist/{itemId:guid}")]
    [ProducesResponseType(typeof(NoteChecklistItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteChecklistItem>> ToggleChecklistItem(
        Guid id,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetSubjectId();
        if (userId == null)
            return Unauthorized();

        var existing = await _noteService.GetNoteByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        try
        {
            var updatedItem = await _noteService.ToggleChecklistItemAsync(id, itemId, cancellationToken);
            _logger.LogInformation(
                "Toggled checklist item {ItemId} in note {NoteId} to {IsCompleted}",
                itemId,
                id,
                updatedItem.IsCompleted);
            return Ok(updatedItem);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}

#region Request Models

/// <summary>
/// Request model for creating a new note
/// </summary>
public class CreateNoteRequest
{
    /// <summary>
    /// The note category
    /// </summary>
    [Required]
    public NoteCategory Category { get; set; }

    /// <summary>
    /// Optional title for the note (max 200 characters)
    /// </summary>
    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>
    /// The note content (max 10000 characters)
    /// </summary>
    [Required]
    [MaxLength(10000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Optional contextual timestamp for when the note event occurred
    /// </summary>
    public DateTime? OccurredAt { get; set; }

    /// <summary>
    /// Optional checklist items to create with the note
    /// </summary>
    public List<CreateChecklistItemRequest>? ChecklistItems { get; set; }
}

/// <summary>
/// Request model for creating a checklist item
/// </summary>
public class CreateChecklistItemRequest
{
    /// <summary>
    /// The checklist item text (max 500 characters)
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Whether the item is completed
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Optional sort order (defaults to creation order)
    /// </summary>
    public int? SortOrder { get; set; }
}

/// <summary>
/// Request model for updating an existing note
/// </summary>
public class UpdateNoteRequest
{
    /// <summary>
    /// Updated category (optional)
    /// </summary>
    public NoteCategory? Category { get; set; }

    /// <summary>
    /// Updated title (optional, max 200 characters)
    /// </summary>
    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>
    /// Updated content (optional, max 10000 characters)
    /// </summary>
    [MaxLength(10000)]
    public string? Content { get; set; }

    /// <summary>
    /// Updated contextual timestamp (optional)
    /// </summary>
    public DateTime? OccurredAt { get; set; }

    /// <summary>
    /// Updated checklist items (if provided, replaces all existing items)
    /// </summary>
    public List<UpdateChecklistItemRequest>? ChecklistItems { get; set; }
}

/// <summary>
/// Request model for updating a checklist item
/// </summary>
public class UpdateChecklistItemRequest
{
    /// <summary>
    /// Item ID (null for new items)
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The checklist item text (max 500 characters)
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Whether the item is completed
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// When the item was completed (optional)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Optional sort order (defaults to creation order)
    /// </summary>
    public int? SortOrder { get; set; }
}

/// <summary>
/// Request model for archiving/unarchiving a note
/// </summary>
public class ArchiveNoteRequest
{
    /// <summary>
    /// True to archive, false to unarchive
    /// </summary>
    [Required]
    public bool Archive { get; set; }
}

/// <summary>
/// Request model for linking a tracker to a note
/// </summary>
public class LinkTrackerRequest
{
    /// <summary>
    /// The tracker definition ID to link
    /// </summary>
    [Required]
    public Guid TrackerDefinitionId { get; set; }

    /// <summary>
    /// Optional notification thresholds for this link
    /// </summary>
    public List<CreateTrackerThresholdRequest>? Thresholds { get; set; }
}

/// <summary>
/// Request model for creating a tracker notification threshold
/// </summary>
public class CreateTrackerThresholdRequest
{
    /// <summary>
    /// Hours offset from tracker start (positive) or end (negative)
    /// </summary>
    public decimal HoursOffset { get; set; }

    /// <summary>
    /// The notification urgency level
    /// </summary>
    public NotificationUrgency Urgency { get; set; }

    /// <summary>
    /// Optional description for this threshold
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Request model for linking a state span to a note
/// </summary>
public class LinkStateSpanRequest
{
    /// <summary>
    /// The state span ID to link
    /// </summary>
    [Required]
    public Guid StateSpanId { get; set; }
}

#endregion
