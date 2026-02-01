using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Interface for documents that can be processed for sanitization and timestamp conversion
/// </summary>
public interface IProcessableDocument
{
    /// <summary>
    /// Gets or sets the MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    string? Id { get; set; }

    /// <summary>
    /// Gets or sets the ISO 8601 formatted creation timestamp
    /// </summary>
    string? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp in milliseconds since Unix epoch
    /// </summary>
    long Mills { get; set; }

    /// <summary>
    /// Gets or sets the UTC offset in minutes
    /// </summary>
    int? UtcOffset { get; set; }

    /// <summary>
    /// Gets a dictionary of text fields that should be sanitized for HTML content
    /// Key is the field name, value is the field content
    /// </summary>
    /// <returns>Dictionary of field names and their text content to sanitize</returns>
    Dictionary<string, string?> GetSanitizableFields();

    /// <summary>
    /// Sets a sanitized text field value
    /// </summary>
    /// <param name="fieldName">The field name</param>
    /// <param name="sanitizedValue">The sanitized value</param>
    void SetSanitizedField(string fieldName, string? sanitizedValue);
}
