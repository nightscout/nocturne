using System.Globalization;
using Nocturne.Core.Models.Queries;

namespace Nocturne.API.Helpers;

/// <summary>
/// Legacy Nightscout's default date window on a v1 find: the last four days, unless the find names a
/// date bound or an id.
/// </summary>
/// <remarks>
/// <para>
/// Nightscout's <c>lib/server/query.js</c> (<c>enforceDateFilter</c>, reached from
/// <c>treatments.query_for</c>, <c>activity.list</c> and the entries read) adds
/// <c>created_at &gt;= now - 4 days</c> (<c>date</c> for entries) to every find whose top level
/// names neither <c>_id</c>, that date field nor <c>dateString</c>. Field filters (<c>eventType</c>, a client <c>id</c>, …) can only be matched
/// after projection, so without the window a v1 find, count or delete projects the tenant's whole
/// history into memory, and one past <c>TreatmentReadService.MaxFilterFetch</c> silently
/// under-matches.
/// </para>
/// <para>
/// Treatments window only finds with field filters: an empty or time-only find keeps its
/// unwindowed behaviour, since its limit is pushed down to the database. Activity windows every
/// find, an empty one included, through <c>windowUnfiltered</c>, and so do entries, which window on
/// <c>date</c>.
/// </para>
/// </remarks>
public static class LegacyFindDateWindow
{
    /// <summary>Legacy's <c>deltaAgo</c> default: <c>TWO_DAYS * 2</c>.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(4);

    /// <summary>
    /// Returns <paramref name="find"/> with <c>$gte now - 4 days</c> on <paramref name="dateField"/>
    /// added in the same wire form, or unchanged when the window does not apply.
    /// </summary>
    /// <param name="find">The resolved find, or <see langword="null"/> when the request carries none.</param>
    /// <param name="now">The instant the window is measured back from.</param>
    /// <param name="dateField">The field the window bounds; a condition on it disables the window.</param>
    /// <param name="windowUnfiltered">
    /// Window a find with no field filters too, including an absent one.
    /// </param>
    public static string? Apply(string? find, DateTimeOffset now,
        string dateField = "created_at",
        bool windowUnfiltered = false)
    {
        var parsed = FindQuery.Parse(find);
        if ((!parsed.HasFieldFilters && !windowUnfiltered)
            || parsed.HasTopLevelCondition("_id")
            || parsed.HasTopLevelCondition(dateField)
            || parsed.HasTopLevelCondition("dateString"))
            return find;

        var minDate = (now - DefaultWindow).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // A find that parses empty may still hold unreadable keys or operands, which match nothing;
        // it takes the window appended, so those survive.
        if (string.IsNullOrWhiteSpace(find)
            || (parsed.IsEmpty && !parsed.HasInexactCondition && find.TrimStart().StartsWith('{')))
            return $"find[{dateField}][$gte]={Uri.EscapeDataString(minDate)}";

        if (find.TrimStart().StartsWith('{'))
        {
            // Spliced into the text rather than rebuilt through JsonNode, which throws on the
            // duplicate keys FindQuery.Parse accepts; the original members parse unchanged.
            var body = find.TrimEnd();
            return $"{body[..^1]},\"{dateField}\":{{\"$gte\":\"{minDate}\"}}}}";
        }

        return $"{find}&find[{dateField}][$gte]={Uri.EscapeDataString(minDate)}";
    }
}
