using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Interface for documents that can be processed for sanitization and timestamp conversion.
/// </summary>
/// <seealso cref="ProcessableDocumentBase"/>
/// <seealso cref="SanitizableAttribute"/>
/// <seealso cref="Entry"/>
/// <seealso cref="Treatment"/>
/// <seealso cref="DeviceStatus"/>
/// <seealso cref="Activity"/>
public interface IProcessableDocument
{
    /// <summary>
    /// MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    string? Id { get; set; }

    /// <summary>
    /// ISO 8601 formatted creation timestamp
    /// </summary>
    string? CreatedAt { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    long Mills { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    int? UtcOffset { get; set; }

    /// <summary>
    /// Gets a dictionary of text fields that should be sanitized for HTML content
    /// Key is the field name, value is the field content
    /// </summary>
    /// <returns>Dictionary of field names and their text content to sanitize</returns>
    Dictionary<string, string?> GetSanitizableFields();

    void SetSanitizedField(string fieldName, string? sanitizedValue);
}
