namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Well-known diabetes app directory. Ships bundled with Nocturne and updates
/// with releases. Provides identity metadata for consent screens, not authorization.
/// Any app can initiate an OAuth flow without being in this directory.
/// </summary>
public static class KnownOAuthClients
{
    /// <summary>
    /// Bundled known client entries. These provide identity information for the
    /// consent screen so users can recognize apps they're authorizing.
    /// </summary>
    public static readonly IReadOnlyList<KnownClientEntry> Entries = new List<KnownClientEntry>
    {
        new()
        {
            ClientIdPattern = "xdrip-*",
            DisplayName = "xDrip+",
            Homepage = "https://github.com/NightscoutFoundation/xDrip",
            TypicalScopes = [
                OAuthScopes.EntriesReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DeviceStatusReadWrite,
            ],
        },
        new()
        {
            ClientIdPattern = "aaps-*",
            DisplayName = "AAPS",
            Homepage = "https://androidaps.readthedocs.io",
            TypicalScopes = [
                OAuthScopes.EntriesReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.ProfileRead,
                OAuthScopes.DeviceStatusReadWrite,
            ],
        },
        new()
        {
            ClientIdPattern = "loop-*",
            DisplayName = "Loop",
            Homepage = "https://loopkit.github.io/loopdocs/",
            TypicalScopes = [
                OAuthScopes.EntriesReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DeviceStatusReadWrite,
            ],
        },
        new()
        {
            ClientIdPattern = "nightscout-*",
            DisplayName = "Nightscout",
            Homepage = "https://nightscout.github.io/",
            TypicalScopes = [
                OAuthScopes.EntriesRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DeviceStatusRead,
                OAuthScopes.ProfileRead,
            ],
        },
        new()
        {
            ClientIdPattern = "sugarmate-*",
            DisplayName = "Sugarmate",
            Homepage = "https://sugarmate.io/",
            TypicalScopes = [
                OAuthScopes.EntriesRead,
            ],
        },
        new()
        {
            ClientIdPattern = "nightwatch-*",
            DisplayName = "Nightwatch",
            Homepage = "https://github.com/nickenilsson/nightwatch",
            TypicalScopes = [
                OAuthScopes.EntriesRead,
                OAuthScopes.TreatmentsRead,
            ],
        },
    };

    /// <summary>
    /// Try to match a client_id against the known app directory.
    /// Uses prefix matching: "xdrip-pixel9" matches pattern "xdrip-*".
    /// </summary>
    /// <param name="clientId">The client_id to look up</param>
    /// <returns>The matching entry, or null if not in the directory</returns>
    public static KnownClientEntry? Match(string clientId)
    {
        if (string.IsNullOrEmpty(clientId))
            return null;

        foreach (var entry in Entries)
        {
            if (MatchesPattern(clientId, entry.ClientIdPattern))
                return entry;
        }

        return null;
    }

    /// <summary>
    /// Simple glob matching: "xdrip-*" matches "xdrip-pixel9", "xdrip-anything".
    /// Only supports trailing wildcard.
    /// </summary>
    private static bool MatchesPattern(string value, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Entry in the known OAuth client directory.
/// </summary>
public class KnownClientEntry
{
    /// <summary>
    /// Glob pattern to match client_id (e.g., "xdrip-*")
    /// </summary>
    public string ClientIdPattern { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable app name for the consent screen
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// App homepage URL
    /// </summary>
    public string? Homepage { get; set; }

    /// <summary>
    /// Typical scopes this app requests (informational, not enforced)
    /// </summary>
    public List<string> TypicalScopes { get; set; } = new();
}
