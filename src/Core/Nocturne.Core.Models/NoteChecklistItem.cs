using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a checklist item within a note
/// </summary>
public class NoteChecklistItem
{
    /// <summary>
    /// Gets or sets the unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the checklist item text (max 500 characters)
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the checklist item is completed
    /// </summary>
    [JsonPropertyName("isCompleted")]
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Gets or sets when the checklist item was completed
    /// </summary>
    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the sort order for display
    /// </summary>
    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}
