using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents attachment metadata for a note (blob storage handled separately)
/// </summary>
public class NoteAttachment
{
    /// <summary>
    /// Gets or sets the unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the original file name
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MIME type of the attachment
    /// </summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes
    /// </summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets when the attachment was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}
