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
    public const string V3SignInPath = "/api/v3/users/sign_in";
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
    public const string V3HistoriesPath = "/api/v3/users/summary/histories";

    // -- SSV2 sync (mobile-app granular sync) ---------------------------------

    /// <summary>
    ///     Granular per-reading CGM stream consumed by the Glooko mobile app, paginated by cursor.
    ///     One resource in the broader SSV2 sync protocol (<c>com.glooko.serversync</c>).
    /// </summary>
    public const string Ssv2EgvsPath = "/api/v2/cgm/egvs";

    /// <summary>
    ///     Granular pump device-event stream (reservoir/site/cannula changes, prime, suspend/resume, etc.).
    /// </summary>
    public const string Ssv2PumpEventsPath = "/api/v2/pumps/events";

    /// <summary>
    ///     Manual pen-injected boluses (rapid/short-acting). SSV2 counterpart to the v3 graph's
    ///     <c>gkInsulinBolus</c> series — the only insulin source for MDI (pen) users.
    /// </summary>
    public const string Ssv2InjectionBolusesPath = "/api/v2/pumps/injection_boluses";

    /// <summary>
    ///     Manual pen-injected basal (long/ultra-long-acting). SSV2 counterpart to the v3 graph's
    ///     <c>gkInsulinBasal</c> series.
    /// </summary>
    public const string Ssv2InjectionBasalsPath = "/api/v2/pumps/injection_basals";

    /// <summary>
    ///     Pump alarms (occlusion, empty reservoir, etc.). SSV2 counterpart to the v3 graph's
    ///     <c>pumpAlarm</c> series. Note: records are snake_case Mongo documents, unlike the other
    ///     camelCase pump feeds.
    /// </summary>
    public const string Ssv2AlarmsPath = "/api/v2/pumps/alarms";

    /// <summary>
    ///     Standalone carb entries logged in the Glooko/CGM app (not tied to a bolus). SSV2 counterpart
    ///     to the v3 graph's <c>carbAll</c> series.
    /// </summary>
    public const string Ssv2CarbsEventsPath = "/api/v2/cgm/carbs_events";

    /// <summary>
    ///     Extended/square-wave and dual-wave boluses (an initial portion plus a portion delivered over a
    ///     duration). Net-new vs the windowed path and the v3 graph (which has no extended-bolus series).
    /// </summary>
    public const string Ssv2ExtendedBolusesPath = "/api/v2/pumps/extended_boluses";

    /// <summary>
    ///     App-logged insulin doses for CGM-only/MDI users (logged in the app, not pump-delivered).
    ///     "fast_acting" → rapid Bolus; "long_acting"/"intermediate" → BasalInjection. Records are
    ///     snake_case Mongo documents.
    /// </summary>
    public const string Ssv2InsulinEventsPath = "/api/v2/cgm/insulin_events";

    /// <summary>
    ///     App-logged free-text notes (camelCase) → Note.
    /// </summary>
    public const string Ssv2NotesPath = "/api/v2/notes";

    /// <summary>
    ///     App-logged exercises (camelCase) → Activity. Duration is in seconds.
    /// </summary>
    public const string Ssv2ExercisesPath = "/api/v2/exercises";

    /// <summary>
    ///     A second app-logged exercise source (snake_case Mongo) → Activity. Duration is in minutes and
    ///     intensity is a string, unlike <see cref="Ssv2ExercisesPath"/>.
    /// </summary>
    public const string Ssv2ExerciseEventsPath = "/api/v2/cgm/exercise_events";

    /// <summary>
    ///     Pump basal/bolus program snapshots → Nocturne Profiles. The SSV2-native profile source that lets
    ///     the SSV2 path stop calling the v3 <c>devices_and_settings</c> endpoint. Records are snake_case
    ///     Mongo documents (like <c>pumps/alarms</c>); segment times are seconds-of-day and ISF/target glucose
    ///     values are mg/dL × 100.
    /// </summary>
    public const string Ssv2PumpSettingsPath = "/api/v2/pumps/settings";

    /// <summary>
    ///     The patient's pump hardware inventory (brand/model/serial per pump ever used). Maps to
    ///     PatientDevice (InsulinPump), distinct from the per-event pumps/* feeds.
    /// </summary>
    public const string Ssv2PumpsPath = "/api/v2/pumps";

    /// <summary>
    ///     The patient's CGM hardware inventory (brand/model/serial per sensor system). Maps to
    ///     PatientDevice (CGM).
    /// </summary>
    public const string Ssv2CgmDevicesPath = "/api/v2/cgm_devices";

    /// <summary>
    ///     Manual + HealthKit body-weight entries → BodyWeight. <c>value</c> is in grams. The third-party
    ///     counterpart is <see cref="Ssv2ValidicWeightsPath"/> (kilograms).
    /// </summary>
    public const string Ssv2WeightsPath = "/api/v2/weights";

    /// <summary>
    ///     Third-party (Validic) body-weight entries → BodyWeight. <c>weight</c> is already in kilograms and
    ///     <c>bmi</c> may be present, unlike the manual/HealthKit <see cref="Ssv2WeightsPath"/> feed (grams).
    /// </summary>
    public const string Ssv2ValidicWeightsPath = "/api/v2/validic/weights";

    /// <summary>
    ///     Daily third-party (Validic) activity summary → StepCount. <c>steps</c> is the day's total step
    ///     count. (Per-workout steps also appear in <see cref="Ssv2ValidicFitnessesPath"/> but are not
    ///     ingested, to avoid double-counting the daily total.)
    /// </summary>
    public const string Ssv2RoutinesPath = "/api/v2/validic/routines";

    /// <summary>
    ///     Third-party (Validic) workouts. Carries per-workout steps but <b>no</b> heart rate; not currently
    ///     ingested (steps come from <see cref="Ssv2RoutinesPath"/>). Kept as a documented path for reference.
    /// </summary>
    public const string Ssv2ValidicFitnessesPath = "/api/v2/validic/fitnesses";

    /// <summary>
    ///     Third-party (Validic) biometric panel (cholesterol, blood pressure, SpO2, resting heart rate, …).
    ///     The only heart-rate-bearing SSV2 source: the <c>restingHeartrate</c> field → HeartRate. Glooko has
    ///     no continuous/time-series HR stream.
    /// </summary>
    public const string Ssv2BiometricMeasurementsPath = "/api/v2/validic/biometric_measurements";

    /// <summary>
    ///     Sentinel <c>lastGuid</c> that starts an SSV2 cursor scan from the beginning.
    /// </summary>
    public const string Ssv2InitialLastGuid = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    ///     Sentinel <c>lastUpdatedAt</c> (Unix epoch) that starts an SSV2 cursor scan from the beginning.
    /// </summary>
    public const string Ssv2InitialLastUpdatedAt = "1970-01-01T00:00:00.000Z";

    /// <summary>
    ///     Records requested per SSV2 page. The app uses a few hundred; this bounds memory per call.
    /// </summary>
    public const int Ssv2PageSize = 500;

    /// <summary>
    ///     Hard cap on SSV2 pages per resource per sync, so a non-advancing cursor can never loop forever.
    /// </summary>
    public const int Ssv2MaxPages = 1000;

    // -- V3 graph series ------------------------------------------------------

    /// <summary>
    ///     Base series requested from the v3 graph/data endpoint.
    /// </summary>
    public static readonly string[] V3GraphSeries =
    [
        "automaticBolus", "deliveredBolus", "injectionBolus",
        "gkInsulinBasal", "gkInsulinBolus",
        "pumpAlarm", "reservoirChange", "setSiteChange",
        "carbAll", "scheduledBasal", "temporaryBasal",
        "suspendBasal", "lgsPlgs", "profileChange",
        "bgHigh", "bgNormal", "bgLow",
    ];

    /// <summary>
    ///     Additional CGM series appended when V3 CGM backfill is enabled.
    /// </summary>
    public static readonly string[] V3CgmBackfillSeries = ["cgmHigh", "cgmNormal", "cgmLow"];

    /// <summary>
    ///     Pump mode series requested from the v3 graph/data endpoint.
    ///     Device-family-prefixed series covering CamAPS FX, Control-IQ, Omnipod 5, Basal-iQ, and generic pumps.
    /// </summary>
    public static readonly string[] V3PumpModeSeries =
    [
        // CamAPS FX
        "pumpCamapsAutomaticMode", "pumpCamapsManualMode", "pumpCamapsBoostMode",
        "pumpCamapsEaseOffMode", "pumpCamapsLibertyMode",
        "pumpCamapsPumpDeliverySuspendedMode",
        "pumpCamapsNoPumpConnectivityMode", "pumpCamapsBluetoothTurnedOffMode", "pumpCamapsNoCgmMode",
        // Control-IQ
        "pumpControliqAutomaticMode", "pumpControliqManualMode",
        "pumpControliqSleepMode", "pumpControliqExerciseMode",
        // Omnipod 5
        "pumpOp5AutomaticMode", "pumpOp5ManualMode",
        "pumpOp5LimitedMode", "pumpOp5HypoprotectMode",
        // Basal-iQ
        "pumpBasaliqAutomaticMode", "pumpBasaliqManualMode",
        // Generic
        "pumpGenericAutomaticMode", "pumpGenericManualMode",
    ];

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

    /// <summary>
    ///     Width of one fetch window. A sync request's range is sliced into chunks this wide, each
    ///     fetched and published on its own.
    /// </summary>
    public static readonly TimeSpan SyncChunkSize = TimeSpan.FromDays(14);

    /// <summary>
    ///     How far back a full walk reads. Glooko posts pump data in batches, often days after the
    ///     fact, so a background sync cannot resume from the newest stored record; it re-reads a
    ///     short lookback every run and this whole span once per <see cref="FullWalkInterval"/>.
    /// </summary>
    public const int FullWalkMonths = 6;

    /// <summary>
    ///     How often a background sync sets its lookback aside and walks <see cref="FullWalkMonths"/>
    ///     of history. Late arrivals older than the lookback land within this long of reaching Glooko.
    /// </summary>
    public static readonly TimeSpan FullWalkInterval = TimeSpan.FromDays(1);

    /// <summary>
    ///     The sync-cursor resource under which the last completed full walk is recorded.
    /// </summary>
    public const string FullWalkCursorResource = "full-walk";

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
