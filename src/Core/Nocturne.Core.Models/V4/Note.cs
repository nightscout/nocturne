namespace Nocturne.Core.Models.V4;

/// <summary>
/// User note or annotation record
/// </summary>
public class Note
{
    /// <summary>
    /// UUID v7 primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical timestamp in Unix milliseconds
    /// </summary>
    public long Mills { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that created this note
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this note
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Origin data source identifier
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Links records that were split from the same legacy Treatment
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    public string? LegacyId { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; }

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
}
