using Nocturne.Core.Constants;

namespace Nocturne.Core.Models.Entries;

/// <summary>
/// Pure domain logic for <see cref="Entry"/> operations. All methods are static with zero I/O,
/// making them trivially testable without mocks.
/// </summary>
/// <seealso cref="Entry"/>
public static class EntryDomainLogic
{
    /// <summary>
    /// Builds a MongoDB-style JSON find query with data_source filter injected
    /// based on whether demo mode is enabled.
    /// </summary>
    /// <param name="demoEnabled">True to filter FOR demo data, false to filter it OUT.</param>
    /// <param name="existingQuery">Optional existing JSON query to merge with.</param>
    /// <returns>A JSON find query string with the data_source filter.</returns>
    public static string BuildDemoModeFilterQuery(bool demoEnabled, string? existingQuery)
    {
        string demoFilter;
        if (demoEnabled)
        {
            demoFilter = $"\"data_source\":\"{DataSources.DemoService}\"";
        }
        else
        {
            demoFilter = $"\"data_source\":{{\"$ne\":\"{DataSources.DemoService}\"}}";
        }

        if (string.IsNullOrWhiteSpace(existingQuery) || existingQuery == "{}")
        {
            return "{" + demoFilter + "}";
        }

        var trimmed = existingQuery.Trim();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (string.IsNullOrEmpty(inner))
            {
                return "{" + demoFilter + "}";
            }
            return "{" + demoFilter + "," + inner + "}";
        }

        // If query doesn't look like JSON, just return demo filter
        return "{" + demoFilter + "}";
    }

    /// <summary>
    /// Parses time range bounds from a Nightscout-style find query (querystring or JSON form,
    /// epoch-millisecond or ISO 8601 values) on recognised timestamp fields.
    /// Returns (null, null) if the query is absent or contains no time constraints.
    /// </summary>
    /// <param name="find">A Nightscout-style find query string (may be null).</param>
    /// <returns>A tuple of (From, To) timestamps in Unix milliseconds, either of which may be null.</returns>
    /// <seealso cref="Queries.FindQuery"/>
    public static (long? From, long? To) ParseTimeRangeFromFind(string? find)
    {
        var query = Queries.FindQuery.Parse(find);
        return (query.FromMills, query.ToMills);
    }

    /// <summary>
    /// Returns true for common entry counts that are worth caching (10, 50, 100).
    /// </summary>
    /// <param name="count">The entry count to check.</param>
    /// <returns><c>true</c> if <paramref name="count"/> is 10, 50, or 100.</returns>
    public static bool IsCommonEntryCount(int count) => count is 10 or 50 or 100;

}
