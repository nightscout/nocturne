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

        if (queryString.Contains("find[") || queryString.Contains("find%5B", StringComparison.OrdinalIgnoreCase))
            return queryString;

        return string.IsNullOrEmpty(boundFind) ? null : boundFind;
    }
}
