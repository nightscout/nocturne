namespace Nocturne.Core.Models;

/// <summary>
/// A submitted record Nocturne answered but did not store, because a value in it could never be
/// stored as sent. Kept, once however often it is resent, so the owner can see what an uploader
/// believes it saved.
/// </summary>
public class RejectedUpload
{
    public Guid Id { get; set; }

    /// <summary>The kind of record, e.g. <c>treatment</c>.</summary>
    public string RecordType { get; set; } = string.Empty;

    /// <summary>What makes a resend the same rejected upload; see <c>RejectedUploadRecorder</c>.</summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>
    /// The record as Nocturne received it, as JSON: after the endpoint's own preparation (v1 fills
    /// a missing <c>eventType</c> and <c>created_at</c>), with long string values shortened and
    /// characters JSON storage cannot hold replaced. <see cref="PayloadBytes"/> is its size before.
    /// </summary>
    public string Payload { get; set; } = "{}";

    public int PayloadBytes { get; set; }

    /// <summary>Why it could not be stored, the last time it was sent.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Where it came from: <c>api</c>, or the connector that fetched it.</summary>
    public string? Origin { get; set; }

    /// <summary>The app that sent it, as the record names it (Nightscout's <c>enteredBy</c>).</summary>
    public string? App { get; set; }

    /// <summary>The device that sent it, as the record names it.</summary>
    public string? Device { get; set; }

    /// <summary>How many times it has been sent and refused.</summary>
    public int Occurrences { get; set; } = 1;

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}
