using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Core.Models;

/// <summary>
/// Nightscout settings record.
/// Settings are configuration key-value pairs for the Nightscout instance,
/// stored in the <c>settings</c> collection. Each record holds a single key/value pair.
/// </summary>
/// <seealso cref="ProcessableDocumentBase"/>
public class Settings : ProcessableDocumentBase
{
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>
    /// The settings key, which is also the document's V3 identifier.
    /// </summary>
    [JsonIgnore]
    [Required]
    [Sanitizable]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// A document's own <c>key</c> field when it is not a string, such as the answer AAPS writes
    /// to a client-control key exchange (<c>aaps_clientcontrol_key_&lt;clientId&gt;</c>). Such a
    /// document is addressed by its identifier, and its <c>key</c> field holds data.
    /// </summary>
    [JsonIgnore]
    public JsonElement? DocumentKey { get; set; }

    /// <summary>
    /// The <c>key</c> field on the wire: the document's own <see cref="DocumentKey"/> when it has
    /// one, otherwise the settings <see cref="Key"/>. A string read here is the settings key; an
    /// object or array is kept as the document's own field.
    /// </summary>
    [JsonPropertyName("key")]
    public JsonElement KeyField
    {
        get => DocumentKey ?? JsonSerializer.SerializeToElement(Key);
        set
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    Key = value.GetString() ?? string.Empty;
                    DocumentKey = null;
                    break;
                case JsonValueKind.Object or JsonValueKind.Array:
                    DocumentKey = value.Clone();
                    break;
            }
        }
    }

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
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? SrvCreated { get; set; }

    /// <summary>
    /// Server timestamp when the setting was last modified (Unix timestamp in milliseconds)
    /// </summary>
    [JsonPropertyName("srvModified")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? SrvModified { get; set; }

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

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    /// <summary>
    /// Whether this setting is currently active/enabled
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("notes")]
    [Sanitizable]
    public string? Notes { get; set; }

    /// <summary>
    /// The V3 identifier, which addresses a settings document by its key.
    /// </summary>
    [JsonPropertyName("identifier")]
    public string? Identifier => string.IsNullOrEmpty(Key) ? null : Key;

    /// <summary>
    /// The writer's own fields, kept as sent: upstream stores a v3 settings document whole, and
    /// AAPS reads back its <c>aaps</c> document's fields (e.g. <c>runningConfig</c>, <c>date</c>).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
