namespace Nocturne.Connectors.Core.Models;

public class SyncRequest
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public List<SyncDataType> DataTypes { get; set; } = [];

    /// <summary>
    ///     Whether <see cref="From"/> is the glucose resume point the background run derived rather
    ///     than a bound a caller chose. Internal so a request body cannot claim it.
    /// </summary>
    internal bool FromIsGlucoseResumePoint { get; set; }

    /// <summary>
    ///     Why this window covers no time — <see cref="From"/> not before <see cref="To"/> — or
    ///     <c>null</c> when it is usable. Every connector answers such a window with zero work and a
    ///     success, so it is refused where a request is accepted rather than run.
    /// </summary>
    public string? WindowError() =>
        From is { } from && To is { } to && from >= to
            ? $"'from' ({from:o}) must be before 'to' ({to:o})"
            : null;
}
