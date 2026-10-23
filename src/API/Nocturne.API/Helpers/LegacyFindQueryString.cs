using Nocturne.Core.Models.Queries;

namespace Nocturne.API.Helpers;

/// <summary>
/// Resolves a v1 find from a request that may carry it in either wire form.
/// </summary>
/// <remarks>
/// Legacy clients send <c>find[field]=…</c> query-string filters, which never bind to a
/// <c>[FromQuery] string? find</c> parameter; the whole query string is the find in that case.
/// </remarks>
public static class LegacyFindQueryString
{
    /// <summary>
    /// The raw query string (without <c>?</c>) when it carries <c>find[…]</c> parameters,
    /// otherwise <paramref name="boundFind"/>.
    /// </summary>
    public static string? Resolve(HttpRequest? request, string? boundFind)
    {
        var queryString = request?.QueryString.ToString() ?? string.Empty;
        if (queryString.StartsWith('?'))
            queryString = queryString[1..];

        if (CarriesFindKeys(queryString))
            return queryString;

        return string.IsNullOrEmpty(boundFind) ? null : boundFind;
    }

    /// <summary>
    /// <c>false</c> when <paramref name="find"/> parses to no conditions: absent, <c>{}</c>, a query
    /// string with no <c>find[…]</c> key, or one the parser cannot read. A legacy bulk delete refuses
    /// such a find rather than reading it as a request to delete everything, and the decomposers
    /// refuse it too through <see cref="FindQuery.IsEmpty"/>.
    /// </summary>
    public static bool HasConditions(string? find) => !FindQuery.Parse(find).IsEmpty;

    /// <summary>
    /// <c>true</c> when <paramref name="find"/> is a query string with <c>find[…]</c> keys of which the
    /// parser can read none, so it names no condition.
    /// </summary>
    public static bool IsUnreadableQueryString(string? find) =>
        find is not null && CarriesFindKeys(find) && FindQuery.Parse(find).IsEmpty;

    private static bool CarriesFindKeys(string queryString) =>
        queryString.Contains("find[") || queryString.Contains("find%5B", StringComparison.OrdinalIgnoreCase);
}
