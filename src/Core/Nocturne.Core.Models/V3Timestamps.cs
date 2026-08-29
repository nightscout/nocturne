using System.Globalization;

namespace Nocturne.Core.Models;

/// <summary>
/// Resolves the V3 compatibility timestamps (<c>srvModified</c>, <c>srvCreated</c>) that every
/// document broadcast as a realtime storage event must carry.
/// </summary>
/// <remarks>
/// AAPS's NS v3 socket handler reads <c>doc.srvModified</c> with <c>getLong</c> before it
/// dispatches on the collection, so an event whose doc has no numeric value there throws on the
/// background thread and takes the AAPS process down. Read-only: none of these legs feed the
/// document's own event time, so ingest and decomposition timelines are unaffected.
/// </remarks>
internal static class V3Timestamps
{
    /// <summary>
    /// Returns <paramref name="mills"/> when it carries an instant, otherwise the first of
    /// <paramref name="isoFallbacks"/> that parses, otherwise null. An offset-bearing string is
    /// honoured; a zone-less one is read as UTC rather than as server-local time.
    /// </summary>
    internal static long? Resolve(long? mills, params string?[] isoFallbacks)
    {
        if (mills is not null and not 0)
            return mills;

        foreach (var iso in isoFallbacks)
        {
            if (
                DateTimeOffset.TryParse(
                    iso,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed
                )
            )
            {
                return parsed.ToUnixTimeMilliseconds();
            }
        }

        return null;
    }
}
