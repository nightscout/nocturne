namespace Nocturne.Connectors.Glooko.Configurations;

/// <summary>
///     Constants specific to Glooko connector.
///     To add a new region, add an entry to <see cref="ServerMapping"/>,
///     <see cref="WebOriginMapping"/>, and <see cref="AllowedRegions"/>.
/// </summary>
public static class GlookoConstants
{
    // -- Regions --------------------------------------------------------------

    public const string RegionCA = "CA";
    public const string RegionEU = "EU";
    public const string RegionUS = "US";

    /// <summary>
    ///     All supported region codes (runtime list for validation).
    /// </summary>
    public static readonly string[] AllowedRegions = [RegionCA, RegionEU, RegionUS];

    /// <summary>
    ///     Default region when none is configured.
    /// </summary>
    public const string DefaultRegion = RegionUS;

    // -- Server mapping -------------------------------------------------------

    /// <summary>
    ///     Known Glooko API server hostnames keyed by region code.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ServerMapping =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RegionCA] = "ca.api.glooko.com",
            [RegionEU] = "eu.api.glooko.com",
            [RegionUS] = "api.glooko.com",
        };

    /// <summary>
    ///     Known Glooko web origins keyed by region code (used for Referer/Origin headers).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WebOriginMapping =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RegionCA] = "https://ca.my.glooko.com",
            [RegionEU] = "https://eu.my.glooko.com",
            [RegionUS] = "https://my.glooko.com",
        };

    // -- API paths ------------------------------------------------------------

    public const string SignInPath = "/api/v2/users/sign_in";
    public const string FoodsPath = "/api/v2/foods";
    public const string ScheduledBasalsPath = "/api/v2/pumps/scheduled_basals";
    public const string NormalBolusesPath = "/api/v2/pumps/normal_boluses";
    public const string CgmReadingsPath = "/api/v2/cgm/readings";
    public const string MeterReadingsPath = "/api/v2/readings";
    public const string SuspendBasalsPath = "/api/v2/pumps/suspend_basals";
    public const string TemporaryBasalsPath = "/api/v2/pumps/temporary_basals";
    public const string V3UsersPath = "/api/v3/session/users";
    public const string V3GraphDataPath = "/api/v3/graph/data";
    public const string V3DeviceSettingsPath = "/api/v3/devices_and_settings";

    // -- V3 graph series ------------------------------------------------------

    /// <summary>
    ///     Base series requested from the v3 graph/data endpoint.
    /// </summary>
    public static readonly string[] V3GraphSeries =
    [
        "automaticBolus", "deliveredBolus", "injectionBolus",
        "pumpAlarm", "reservoirChange", "setSiteChange",
        "carbAll", "scheduledBasal", "temporaryBasal",
        "suspendBasal", "lgsPlgs", "profileChange",
    ];

    /// <summary>
    ///     Additional CGM series appended when V3 CGM backfill is enabled.
    /// </summary>
    public static readonly string[] V3CgmBackfillSeries = ["cgmHigh", "cgmNormal", "cgmLow"];

    // -- HTTP -----------------------------------------------------------------

    /// <summary>
    ///     User-Agent header sent with all Glooko API requests.
    /// </summary>
    public const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.5 Safari/605.1.15";

    /// <summary>
    ///     Session cookie name returned by the Glooko sign-in endpoint.
    /// </summary>
    public const string SessionCookieName = "_logbook-web_session";

    /// <summary>
    ///     Hardcoded GUID sent as the lastGuid parameter in v2 API requests (legacy requirement).
    /// </summary>
    public const string LegacyLastGuid = "1e0c094e-1e54-4a4f-8e6a-f94484b53789";

    /// <summary>
    ///     Session lifetime used for token expiry calculation.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    // -- Device information (sent during sign-in) -----------------------------

    /// <summary>
    ///     The deviceInformation object sent with the sign-in request.
    ///     Mimics the Glooko Android app to satisfy server-side validation.
    /// </summary>
    public static readonly object DeviceInformation = new
    {
        applicationType = "logbook",
        os = "android",
        osVersion = "33",
        device = "Google Pixel 8 Pro",
        deviceManufacturer = "Google",
        deviceModel = "Pixel 8 Pro",
        serialNumber = "HIDDEN",
        clinicalResearch = false,
        deviceId = "HIDDEN",
        applicationVersion = "6.1.3",
        buildNumber = "0",
        gitHash = "g4fbed2011b"
    };

    // -- Resolution helpers ---------------------------------------------------

    /// <summary>
    ///     Resolves the API base URL from the server region string.
    ///     Called at request time so per-tenant DB overrides are respected.
    /// </summary>
    public static string ResolveBaseUrl(string? server)
    {
        var key = server?.Trim() ?? DefaultRegion;
        var host = ServerMapping.GetValueOrDefault(key, ServerMapping[DefaultRegion]);
        return $"https://{host}";
    }

    /// <summary>
    ///     Resolves the web origin URL for Referer/Origin headers.
    /// </summary>
    public static string ResolveWebOrigin(string? server)
    {
        var key = server?.Trim() ?? DefaultRegion;
        return WebOriginMapping.GetValueOrDefault(key, WebOriginMapping[DefaultRegion]);
    }
}
