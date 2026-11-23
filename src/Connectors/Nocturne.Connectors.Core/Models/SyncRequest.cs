using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

public class SyncRequest
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public List<SyncDataType> DataTypes { get; set; } = [];

    /// <summary>
    ///     Where each family of the run resumes, set by the base on the copy of the request it hands
    ///     the sync. Internal to set so a request body cannot supply it.
    /// </summary>
    [JsonIgnore]
    public ResumePoints Resume { get; internal set; } = ResumePoints.Unresolved;

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
