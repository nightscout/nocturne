using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// User note or annotation record, capturing free-text observations and announcements.
/// </summary>
/// <remarks>
/// This is the V4 equivalent of a legacy <see cref="Treatment"/> with an event type that
/// carries text content (e.g., "Note", "Announcement", "Question"). The
/// <see cref="EventType"/> field preserves the original legacy event type string.
/// </remarks>
/// <seealso cref="Treatment"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="DeviceEvent"/>
[JsonSchemaFlatten]
public class Note : V4RecordBase
{
    /// <summary>
    /// Note text content
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Original event type (user-specified freeform)
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// Whether this note is an announcement
    /// </summary>
    public bool IsAnnouncement { get; set; }

    /// <summary>
    /// APS system sync/deduplication identifier (used by Loop and AAPS)
    /// </summary>
    public string? SyncIdentifier { get; set; }
}
