using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a user note with optional checklist items and links to trackers and state spans
/// </summary>
public class Note
{
    /// <summary>
    /// Gets or sets the unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the user identifier who owns this note
    /// </summary>
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the note category
    /// </summary>
    [JsonPropertyName("category")]
    public NoteCategory Category { get; set; }

    /// <summary>
    /// Gets or sets the note title (max 200 characters)
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the note content (max 10000 characters)
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the note event occurred (optional contextual timestamp)
    /// </summary>
    [JsonPropertyName("occurredAt")]
    public DateTime? OccurredAt { get; set; }

    /// <summary>
    /// Gets or sets whether the note is archived
    /// </summary>
    [JsonPropertyName("isArchived")]
    public bool IsArchived { get; set; }

    /// <summary>
    /// Gets or sets when the note was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when the note was last updated
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the checklist items associated with this note
    /// </summary>
    [JsonPropertyName("checklistItems")]
    public List<NoteChecklistItem> ChecklistItems { get; set; } = new();

    /// <summary>
    /// Gets or sets the tracker links associated with this note
    /// </summary>
    [JsonPropertyName("trackerLinks")]
    public List<NoteTrackerLink> TrackerLinks { get; set; } = new();

    /// <summary>
    /// Gets or sets the state span links associated with this note
    /// </summary>
    [JsonPropertyName("stateSpanLinks")]
    public List<NoteStateSpanLink> StateSpanLinks { get; set; } = new();

    /// <summary>
    /// Gets or sets the attachments associated with this note (metadata only)
    /// </summary>
    [JsonPropertyName("attachments")]
    public List<NoteAttachment> Attachments { get; set; } = new();
}
