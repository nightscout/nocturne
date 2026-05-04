namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Well-known diabetes app directory. Ships bundled with Nocturne and updates
/// with releases. Provides identity metadata for consent screens and for
/// seeding pre-verified OAuth client rows per tenant via DCR.
/// </summary>
/// <seealso cref="KnownClientEntry"/>
/// <seealso cref="OAuthScopes"/>
public static class KnownOAuthClients
{
    /// <summary>
    /// Bundled known client entries keyed on reverse-DNS software_id.
    /// </summary>
    public static readonly IReadOnlyList<KnownClientEntry> Entries = new List<KnownClientEntry>
    {
        new()
        {
            SoftwareId = "org.trio.diabetes",
            DisplayName = "Trio",
            Homepage = "https://github.com/nightscout/Trio",
            LogoUri = "/logos/trio.svg",
            RedirectUris = ["trio://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.EntriesReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DeviceStatusReadWrite,
                OAuthScopes.ProfileRead,
            ],
        },
        new()
        {
            SoftwareId = "org.nightscoutfoundation.xdrip",
            DisplayName = "xDrip+",
            Homepage = "https://github.com/NightscoutFoundation/xDrip",
            LogoUri = "/logos/xdrip.svg",
            RedirectUris = ["org.nightscoutfoundation.xdrip://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.EntriesReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DeviceStatusReadWrite,
                OAuthScopes.HeartRateReadWrite,
                OAuthScopes.StepCountReadWrite,
            ],
        },
        new()
        {
            SoftwareId = "org.loopkit.loop",
            DisplayName = "Loop",
            Homepage = "https://loopkit.github.io/loopdocs/",
            LogoUri = "/logos/loop.svg",
            RedirectUris = ["org.loopkit.loop://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.EntriesReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DeviceStatusReadWrite,
            ],
        },
        new()
        {
            SoftwareId = "org.androidaps.aaps",
            DisplayName = "AAPS",
            Homepage = "https://androidaps.readthedocs.io",
            LogoUri = "/logos/aaps.svg",
            RedirectUris = ["org.androidaps.aaps://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.EntriesReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.ProfileRead,
                OAuthScopes.DeviceStatusReadWrite,
            ],
        },
        new()
        {
            SoftwareId = "github.nightscout.nightscout",
            DisplayName = "Nightscout",
            Homepage = "https://nightscout.github.io/",
            LogoUri = "/logos/nightscout.svg",
            RedirectUris = [],
            TypicalScopes =
            [
                OAuthScopes.EntriesRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DeviceStatusRead,
                OAuthScopes.ProfileRead,
            ],
        },
        new()
        {
            SoftwareId = "io.sugarmate",
            DisplayName = "Sugarmate",
            Homepage = "https://sugarmate.io/",
            LogoUri = "/logos/sugarmate.svg",
            RedirectUris = [],
            TypicalScopes = [OAuthScopes.EntriesRead],
        },
        new()
        {
            SoftwareId = "com.nickenilsson.nightwatch",
            DisplayName = "Nightwatch",
            Homepage = "https://github.com/nickenilsson/nightwatch",
            LogoUri = "/logos/nightwatch.svg",
            RedirectUris = [],
            TypicalScopes = [OAuthScopes.EntriesRead, OAuthScopes.TreatmentsRead],
        },
        new()
        {
            SoftwareId = "com.nocturne.follower",
            DisplayName = "Nocturne Follower",
            LogoUri = "/logos/nocturne.svg",
            RedirectUris = [],
            TypicalScopes =
            [
                OAuthScopes.EntriesRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DeviceStatusRead,
                OAuthScopes.ProfileRead,
            ],
        },
        new()
        {
            SoftwareId = "com.nocturne.widget.windows",
            DisplayName = "Nocturne Windows Widget",
            Homepage = "https://github.com/nightscout/nocturne",
            LogoUri = "/logos/nocturne.svg",
            RedirectUris = [],
            TypicalScopes =
            [
                OAuthScopes.EntriesRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DeviceStatusRead,
                OAuthScopes.ProfileRead,
            ],
        },
        new()
        {
            SoftwareId = "com.nocturne.tray",
            DisplayName = "Nocturne Tray",
            Homepage = "https://github.com/nightscout/nocturne",
            LogoUri = "/logos/nocturne.svg",
            RedirectUris = [],
            TypicalScopes =
            [
                OAuthScopes.EntriesRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DeviceStatusRead,
                OAuthScopes.ProfileRead,
            ],
        },
        new()
        {
            SoftwareId = "io.home-assistant.nocturne",
            DisplayName = "Home Assistant",
            Homepage = "https://www.home-assistant.io/",
            LogoUri = "/logos/home-assistant.svg",
            RedirectUris = [],
            TypicalScopes =
            [
                OAuthScopes.EntriesReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DeviceStatusRead,
                OAuthScopes.ProfileRead,
                OAuthScopes.HeartRateReadWrite,
                OAuthScopes.StepCountReadWrite,
            ],
        },
    };

    /// <summary>
    /// The well-known software_id used for follower (user-to-user sharing) grants.
    /// </summary>
    public const string FollowerSoftwareId = "com.nocturne.follower";

    /// <summary>
    /// Legacy constant kept for backward compatibility with existing follower grant code.
    /// </summary>
    public const string FollowerClientId = "nocturne-follower-internal";

    /// <summary>
    /// Look up a known app entry by its RFC 7591 software_id (reverse-DNS).
    /// </summary>
    /// <param name="softwareId">The reverse-DNS software_id to look up (e.g., <c>org.trio.diabetes</c>).</param>
    /// <returns>The matching <see cref="KnownClientEntry"/>, or <c>null</c> if not found.</returns>
    public static KnownClientEntry? MatchBySoftwareId(string softwareId) =>
        Entries.FirstOrDefault(e => string.Equals(e.SoftwareId, softwareId, StringComparison.Ordinal));
}

/// <summary>
/// Entry in the known OAuth client directory.
/// </summary>
/// <seealso cref="KnownOAuthClients"/>
/// <seealso cref="OAuthScopes"/>
public class KnownClientEntry
{
    /// <summary>
    /// RFC 7591 software_id — reverse-DNS identifier stable across installs
    /// (e.g., "org.trio.diabetes").
    /// </summary>
    public string SoftwareId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable app name for the consent screen.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// App homepage URL.
    /// </summary>
    public string? Homepage { get; set; }

    /// <summary>
    /// App logo URI for the consent screen.
    /// </summary>
    public string? LogoUri { get; set; }

    /// <summary>
    /// Allowed redirect URIs to seed when the client registers via DCR.
    /// </summary>
    public List<string> RedirectUris { get; set; } = [];

    /// <summary>
    /// Typical scopes this app requests (informational, used for seeding).
    /// </summary>
    public List<string> TypicalScopes { get; set; } = [];
}
