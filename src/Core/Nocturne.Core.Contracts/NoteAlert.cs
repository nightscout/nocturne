using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Represents a pending alert for a note based on tracker threshold evaluation
/// </summary>
public class NoteAlert
{
    /// <summary>
    /// Gets or sets the note ID that triggered the alert
    /// </summary>
    public Guid NoteId { get; set; }

    /// <summary>
    /// Gets or sets the note that triggered the alert
    /// </summary>
    public Note Note { get; set; } = null!;

    /// <summary>
    /// Gets or sets the tracker instance ID that crossed the threshold
    /// </summary>
    public Guid TrackerInstanceId { get; set; }

    /// <summary>
    /// Gets or sets the name of the tracker for display purposes
    /// </summary>
    public string TrackerName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the urgency level of the alert
    /// </summary>
    public NotificationUrgency Urgency { get; set; }

    /// <summary>
    /// Gets or sets the optional description of the threshold that was crossed
    /// </summary>
    public string? ThresholdDescription { get; set; }
}
