namespace Nocturne.Core.Models;

/// <summary>
/// A submitted record Nocturne answered but did not store, because a value in it could never be
/// stored as sent. Kept so the owner can see what an uploader believes it saved.
/// </summary>
public class RejectedUpload
{
    public Guid Id { get; set; }

    /// <summary>The kind of record, e.g. <c>treatment</c>.</summary>
    public string RecordType { get; set; } = string.Empty;

    /// <summary>The record as received, as JSON.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>Why it could not be stored.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Where it came from: <c>api</c>, or the connector that fetched it.</summary>
    public string? Origin { get; set; }

    /// <summary>The app that sent it, as the record names it (Nightscout's <c>enteredBy</c>).</summary>
    public string? App { get; set; }

    /// <summary>The device that sent it, as the record names it.</summary>
    public string? Device { get; set; }

    public DateTime ReceivedAt { get; set; }
}
