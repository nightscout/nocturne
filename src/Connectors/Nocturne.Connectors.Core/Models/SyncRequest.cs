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
    ///     Whether this is a cursor reset: once signed in, the connector forgets how far it got with
    ///     what it re-pulls, then fetches the window. Set only by <see cref="Reset"/>, so a request body
    ///     cannot ask for one.
    /// </summary>
    [JsonIgnore]
    public bool IsReset { get; private init; }

    /// <summary>
    ///     A cursor reset over <paramref name="from"/> (no lower bound when null) to <paramref name="to"/>.
    ///     The connector clears what <see cref="Services.BaseConnectorService{TConfig}"/> says a reset
    ///     clears, inside the run once it has signed in, then fetches the window.
    /// </summary>
    public static SyncRequest Reset(DateTime? from, DateTime to, List<SyncDataType>? dataTypes) => new()
    {
        From = from,
        To = to,
        DataTypes = dataTypes ?? [],
        IsReset = true,
    };

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
