namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a user note or announcement via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.UpsertNoteRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Treatments.NoteController"/>
public class UpsertNoteRequest : IBulkUpsertRequest
{
    /// <summary>
    /// When the note was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device that created the note.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Note body text (capped at 10,000 characters).
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Nightscout-compatible event type string (capped at 200 characters).
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// When true, the note is displayed as a prominent announcement.
    /// </summary>
    public bool IsAnnouncement { get; set; }

    /// <summary>
    /// Upstream sync identifier for deduplication.
    /// </summary>
    public string? SyncIdentifier { get; set; }
}
