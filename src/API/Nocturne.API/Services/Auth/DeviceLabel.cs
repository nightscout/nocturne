namespace Nocturne.API.Services.Auth;

/// <summary>
/// Reads a "browser on platform" label out of a user agent, for showing a person which of their
/// devices a row stands for. A label, not an identity: nothing is decided from it.
/// </summary>
internal static class DeviceLabel
{
    private const int MaxLength = 255;

    // Order matters in both tables: an Android agent also says "Linux", an iPad one "Mac OS X",
    // and every Chromium browser also says "Chrome" and "Safari".
    private static readonly (string Marker, string Name)[] Platforms =
    [
        ("iPhone", "iPhone"),
        ("iPad", "iPad"),
        ("Android", "Android"),
        ("Windows", "Windows"),
        ("Macintosh", "Mac"),
        ("CrOS", "ChromeOS"),
        ("Linux", "Linux"),
    ];

    private static readonly (string Marker, string Name)[] Browsers =
    [
        ("Edg/", "Edge"),
        ("EdgA/", "Edge"),
        ("EdgiOS/", "Edge"),
        ("OPR/", "Opera"),
        ("Firefox/", "Firefox"),
        ("FxiOS/", "Firefox"),
        ("CriOS/", "Chrome"),
        ("Chrome/", "Chrome"),
        ("Safari/", "Safari"),
    ];

    /// <summary>
    /// "Chrome on Windows", or whichever half the agent names, or the agent itself cut to fit when
    /// it names neither. Null for no agent.
    /// </summary>
    public static string? FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;

        var platform = Find(Platforms, userAgent);
        var browser = Find(Browsers, userAgent);

        return (browser, platform) switch
        {
            (not null, not null) => $"{browser} on {platform}",
            (not null, null) => browser,
            (null, not null) => platform,
            _ => userAgent.Length > MaxLength ? userAgent[..MaxLength] : userAgent,
        };
    }

    private static string? Find((string Marker, string Name)[] table, string userAgent)
    {
        foreach (var (marker, name) in table)
        {
            if (userAgent.Contains(marker, StringComparison.Ordinal))
                return name;
        }

        return null;
    }
}
