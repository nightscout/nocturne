namespace Nocturne.API.Configuration;

/// <summary>
/// Resolves the Nightscout-compatible API version a request path belongs to.
/// </summary>
/// <seealso cref="NightscoutJsonFilter"/>
public static class NightscoutApiPath
{
    /// <summary>
    /// Returns 1, 2 or 3 for a <c>/api/v{n}/…</c> path, or <c>null</c> for anything else
    /// (v4 and above, and non-API paths).
    /// </summary>
    public static int? Version(PathString path)
    {
        var value = path.Value;

        if (value is null || value.Length < 8)
        {
            return null;
        }

        if (!value.StartsWith("/api/v", StringComparison.OrdinalIgnoreCase) || value[7] != '/')
        {
            return null;
        }

        return value[6] switch
        {
            '1' => 1,
            '2' => 2,
            '3' => 3,
            _ => null,
        };
    }
}
