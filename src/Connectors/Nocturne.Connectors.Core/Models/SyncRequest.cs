using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

public class SyncRequest
{
    /// <summary>
    ///     The window's lower bound. On a request that names a <see cref="To"/> (every cursor reset,
    ///     every ranged manual sync) a null one means from the beginning: the connector's base resolves
    ///     it to <see cref="Services.BaseConnectorService{TConfig}.HistoryStart"/>, as far back as the
    ///     source serves, before the connector reads it. A request with neither bound is a catch-up
    ///     (<see cref="IsCatchUp"/>).
    /// </summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    /// <summary>
    ///     Whether this request names no bound at all: the "Sync now" button, the Google Health managed
    ///     backfill and the dev sync-all all send one. The base runs it as the scheduled sync does,
    ///     each family resuming from its own point (<see cref="Resume"/>), so it reads what the next
    ///     poll would read and never re-crawls a whole history. A connector with its own window policy
    ///     (<c>RunScheduledAsync</c>) applies it here too.
    /// </summary>
    [JsonIgnore]
    public bool IsCatchUp => From is null && To is null;

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
    ///     A cursor reset over <paramref name="from"/> (from the beginning when null, as <see cref="From"/>
    ///     says) to <paramref name="to"/>.
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
