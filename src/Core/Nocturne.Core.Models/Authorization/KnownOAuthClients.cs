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
            // iOS bundle id is org.nightscout.$(DEVELOPMENT_TEAM).trio; the team segment
            // varies per build, so the stable software_id is the team-independent base.
            SoftwareId = "org.nightscout.trio",
            DisplayName = "Trio",
            Homepage = "https://triodocs.org",
            LogoUri = "/logos/trio.svg",
            RedirectUris = ["org.nightscout.trio://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.GlucoseReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DevicesReadWrite,
                OAuthScopes.TherapyRead,
            ],
        },
        new()
        {
            // Real Android applicationId (Play Store / APK identity).
            SoftwareId = "com.eveningoutpost.dexdrip",
            DisplayName = "xDrip+",
            Homepage = "https://github.com/NightscoutFoundation/xDrip",
            LogoUri = "/logos/xdrip.svg",
            RedirectUris = ["com.eveningoutpost.dexdrip://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.GlucoseReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DevicesReadWrite,
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
                OAuthScopes.GlucoseReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DevicesReadWrite,
            ],
        },
        new()
        {
            // Real Android applicationId. (app.aaps is only the new internal source
            // namespace; the installed package id remains info.nightscout.androidaps.)
            SoftwareId = "info.nightscout.androidaps",
            DisplayName = "AAPS",
            Homepage = "https://wiki.aaps.app",
            LogoUri = "/logos/aaps.svg",
            RedirectUris = ["info.nightscout.androidaps://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.GlucoseReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.TherapyRead,
                OAuthScopes.DevicesReadWrite,
            ],
        },
        new()
        {
            // The classic self-hosted Nightscout server (cgm-remote-monitor) acting as a
            // read-only follower of a Nocturne tenant. "Nightscout" is the server, not a
            // distinct client app, so the id is rooted on the cgm-remote-monitor repo.
            SoftwareId = "org.nightscout.cgm-remote-monitor",
            DisplayName = "Nightscout (server)",
            Homepage = "https://nightscout.github.io/",
            LogoUri = "/logos/nightscout.svg",
            RedirectUris = [],
            TypicalScopes =
            [
                OAuthScopes.GlucoseRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DevicesRead,
                OAuthScopes.TherapyRead,
            ],
        },
        new()
        {
            // Real Android applicationId (Sugarmate is now a Tandem Diabetes Care app).
            SoftwareId = "com.tandemdiabetes.sugarmate",
            DisplayName = "Sugarmate",
            Homepage = "https://sugarmate.io/",
            LogoUri = "/logos/sugarmate.svg",
            RedirectUris = [],
            TypicalScopes = [OAuthScopes.GlucoseRead],
        },
        new()
        {
            // The maintained "Nightwatch" Android app (Markus Kallander); real package id.
            SoftwareId = "se.cornixit.nightwatch",
            DisplayName = "Nightwatch",
            Homepage = "https://play.google.com/store/apps/details?id=se.cornixit.nightwatch",
            LogoUri = "/logos/nightwatch.svg",
            RedirectUris = [],
            TypicalScopes = [OAuthScopes.GlucoseRead, OAuthScopes.TreatmentsRead],
        },
        new()
        {
            SoftwareId = "com.nocturne.follower",
            DisplayName = "Nocturne Follower",
            LogoUri = "/logos/nocturne.svg",
            RedirectUris = [],
            TypicalScopes =
            [
                OAuthScopes.GlucoseRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DevicesRead,
                OAuthScopes.TherapyRead,
            ],
        },
        new()
        {
            SoftwareId = "dev.nocturne.prelude",
            DisplayName = "Prelude",
            Homepage = "https://github.com/nightscout/prelude",
            LogoUri = "/logos/prelude.svg",
            RedirectUris = ["dev.nocturne.prelude://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.GlucoseRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DevicesRead,
            ],
        },
        new()
        {
            SoftwareId = "com.nocturne.widget.windows",
            DisplayName = "Nocturne Windows Widget",
            Homepage = "https://github.com/nightscout/nocturne",
            LogoUri = "/logos/nocturne.svg",
            RedirectUris = ["com.nocturne.widget.windows://oauth/callback"],
            TypicalScopes =
            [
                OAuthScopes.GlucoseRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DevicesRead,
                OAuthScopes.TherapyRead,
            ],
        },
        new()
        {
            SoftwareId = "com.nocturne.tray",
            DisplayName = "Nocturne Tray",
            Homepage = "https://github.com/nightscout/nocturne",
            LogoUri = "/logos/nocturne.svg",
            // Auth-code + PKCE via a loopback listener (RFC 8252). The port varies per
            // login; loopback redirect matching is port-agnostic at authorize time.
            RedirectUris = ["http://127.0.0.1/callback"],
            TypicalScopes =
            [
                OAuthScopes.GlucoseRead,
                OAuthScopes.TreatmentsRead,
                OAuthScopes.DevicesRead,
                OAuthScopes.TherapyRead,
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
                OAuthScopes.GlucoseReadWrite,
                OAuthScopes.TreatmentsReadWrite,
                OAuthScopes.DevicesRead,
                OAuthScopes.TherapyRead,
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
