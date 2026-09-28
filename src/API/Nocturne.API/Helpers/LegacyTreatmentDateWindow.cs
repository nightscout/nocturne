using System.Globalization;
using Nocturne.Core.Models.Queries;

namespace Nocturne.API.Helpers;

/// <summary>
/// Legacy Nightscout's default <c>created_at</c> window for v1 treatment finds that carry field
/// filters.
/// </summary>
/// <remarks>
/// <para>
/// Nightscout's <c>lib/server/query.js</c> (<c>enforceDateFilter</c>, reached from
/// <c>treatments.query_for</c>) adds <c>created_at &gt;= now - 4 days</c> to every treatment find
/// whose top level names neither <c>_id</c>, <c>created_at</c> nor <c>dateString</c>. Field
/// filters (<c>eventType</c>, a client <c>id</c>, …) can only be matched after projection, so
/// without the window a v1 find, count or delete projects the tenant's whole treatment history
/// into memory, and one past <c>TreatmentReadService.MaxFilterFetch</c> silently under-matches.
/// </para>
/// <para>
/// Applied only to finds with field filters: an empty or time-only find keeps its unwindowed
/// behaviour, since its limit is pushed down to the database.
/// </para>
/// </remarks>
public static class LegacyTreatmentDateWindow
{
    /// <summary>Legacy's <c>deltaAgo</c> default: <c>TWO_DAYS * 2</c>.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(4);

    /// <summary>
    /// Returns <paramref name="find"/> with <c>created_at $gte now - 4 days</c> added in the same
    /// wire form, or unchanged when the window does not apply.
    /// </summary>
    public static string? Apply(string? find, DateTimeOffset now)
    {
        var parsed = FindQuery.Parse(find);
        if (!parsed.HasFieldFilters
            || parsed.HasTopLevelCondition("_id")
            || parsed.HasTopLevelCondition("created_at")
            || parsed.HasTopLevelCondition("dateString"))
            return find;

        var minDate = (now - DefaultWindow).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        if (find!.TrimStart().StartsWith('{'))
        {
            // Spliced into the text rather than rebuilt through JsonNode, which throws on the
            // duplicate keys FindQuery.Parse accepts; the original members parse unchanged.
            var body = find.TrimEnd();
            return $"{body[..^1]},\"created_at\":{{\"$gte\":\"{minDate}\"}}}}";
        }

        return $"{find}&find[created_at][$gte]={Uri.EscapeDataString(minDate)}";
    }
}
