using System.Globalization;

namespace Nocturne.Core.Models;

/// <summary>
/// Resolves the V3 compatibility timestamps (<c>srvModified</c>, <c>srvCreated</c>) that every
/// document broadcast as a realtime storage event must carry.
/// </summary>
/// <remarks>
/// AAPS's NS v3 socket handler reads <c>doc.srvModified</c> with <c>getLong</c> before it
/// dispatches on the collection, so an event whose doc has no numeric value there throws on the
/// background thread and takes the AAPS process down. Read-only: no leg here feeds a document's
/// own event time, so ingest and decomposition timelines are unaffected.
/// </remarks>
internal static class V3Timestamps
{
    /// <summary>
    /// Returns <paramref name="mills"/> when it carries an instant, otherwise the first of
    /// <paramref name="isoFallbacks"/> that parses, otherwise null.
    /// </summary>
    /// <remarks>
    /// Zero means "not set"; any other value — including a pre-1970 negative one — is an instant,
    /// because dropping it puts the document back on the crashing path. Parsing is deliberately as
    /// permissive as the reference server's: an offset-bearing string is honoured and a zone-less
    /// one is read as UTC rather than as server-local time, but a bare time resolves against
    /// today's date and a date-only string against midnight. Culture-invariant, so an
    /// ambiguous numeric date is always read month-first.
    /// </remarks>
    internal static long? Resolve(long? mills, params ReadOnlySpan<string?> isoFallbacks)
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
