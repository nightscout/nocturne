using System.Text.Json;
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
    /// Parses $gte/$lte time range values from a MongoDB-style JSON find query.
    /// Walks the document looking for numeric $gte / $lte values on any field.
    /// Returns (null, null) if the query is absent or contains no time constraints.
    /// </summary>
    /// <param name="find">A MongoDB-style JSON query string (may be null).</param>
    /// <returns>A tuple of (From, To) timestamps in Unix milliseconds, either of which may be null.</returns>
    public static (long? From, long? To) ParseTimeRangeFromFind(string? find)
    {
        if (string.IsNullOrEmpty(find))
            return (null, null);

        long? from = null;
        long? to = null;

        try
        {
            using var doc = JsonDocument.Parse(find);
            foreach (var field in doc.RootElement.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var op in field.Value.EnumerateObject())
                {
                    if (op.Value.ValueKind != JsonValueKind.Number)
                        continue;

                    if ((op.Name == "$gte" || op.Name == "$gt") && op.Value.TryGetInt64(out var gte))
                        from = gte;
                    else if ((op.Name == "$lte" || op.Name == "$lt") && op.Value.TryGetInt64(out var lte))
                        to = lte;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed query — return no time bounds, which is safe.
        }

        return (from, to);
    }

    /// <summary>
    /// Returns true for common entry counts that are worth caching (10, 50, 100).
    /// </summary>
    /// <param name="count">The entry count to check.</param>
    /// <returns><c>true</c> if <paramref name="count"/> is 10, 50, or 100.</returns>
    public static bool IsCommonEntryCount(int count) => count is 10 or 50 or 100;

}
