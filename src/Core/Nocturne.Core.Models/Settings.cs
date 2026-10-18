using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a Nightscout settings record.
/// Settings are configuration key-value pairs for the Nightscout instance,
/// stored in the <c>settings</c> collection. Each record holds a single key/value pair.
/// </summary>
/// <seealso cref="ProcessableDocumentBase"/>
public class Settings : ProcessableDocumentBase
{
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    [JsonPropertyName("key")]
    [Required]
    [Sanitizable]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The setting value (can be any JSON value)
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    /// <summary>
    /// ISO 8601 formatted creation timestamp
    /// </summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Server timestamp when the setting was created (Unix timestamp in milliseconds)
    /// </summary>
    [JsonPropertyName("srvCreated")]
    public DateTimeOffset? SrvCreated { get; set; }

    /// <summary>
    /// Server timestamp when the setting was last modified (Unix timestamp in milliseconds)
    /// </summary>
    [JsonPropertyName("srvModified")]
    public DateTimeOffset? SrvModified { get; set; }

    /// <summary>
    /// Optional app field indicating which application created/modified this setting
    /// </summary>
    [JsonPropertyName("app")]
    public string? App { get; set; }

    /// <summary>
    /// Optional device field indicating which device created/modified this setting
    /// </summary>
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    /// <summary>
    /// User that created or last modified this setting
    /// </summary>
    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Version or revision number for this setting
    /// </summary>
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    /// <summary>
    /// Whether this setting is currently active/enabled
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional notes or description for this setting
    /// </summary>
    [JsonPropertyName("notes")]
    [Sanitizable]
    public string? Notes { get; set; }
}
