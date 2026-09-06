namespace Nocturne.Core.Constants;

/// <summary>
/// Central application configuration constants shared across the Nocturne solution.
/// </summary>
/// <seealso cref="ServiceNames"/>
/// <seealso cref="ConnectorTimeouts"/>
public static class ApplicationConstants
{
    /// <summary>
    /// Redis/distributed cache configuration.
    /// </summary>
    public static class Cache
    {
        /// <summary>
        /// Instance name prefix used to namespace cache keys, preventing collisions
        /// when multiple applications share the same cache server.
        /// </summary>
        public const string InstanceName = "nocturne";
    }

    /// <summary>
    /// YARP reverse proxy configuration defaults.
    /// </summary>
    /// <remarks>
    /// These values govern the proxy layer that sits in front of legacy Nightscout
    /// routes defined in <see cref="ApiRoutes.Patterns"/>.
    /// </remarks>
    public static class Proxy
    {
        /// <summary>
        /// Configuration section name in appsettings for proxy settings.
        /// </summary>
        public const string SectionName = "Proxy";

        /// <summary>
        /// Default timeout in seconds for proxied requests before they are aborted.
        /// </summary>
        public const int DefaultTimeoutSeconds = 30;

        /// <summary>
        /// Default number of retry attempts for failed proxied requests.
        /// </summary>
        public const int DefaultRetryAttempts = 3;

        /// <summary>
        /// Whether to forward authentication headers (e.g., Authorization) to upstream targets by default.
        /// </summary>
        public const bool DefaultForwardAuthHeaders = true;
    }

    /// <summary>
    /// Health check and liveness probe endpoint paths.
    /// </summary>
    public static class HealthCheck
    {
        /// <summary>
        /// Full readiness health check endpoint that validates all dependencies.
        /// </summary>
        public const string HealthEndpoint = "/health";

        /// <summary>
        /// Lightweight liveness probe endpoint indicating the process is running.
        /// </summary>
        public const string AliveEndpoint = "/alive";

        /// <summary>
        /// Statistics endpoint exposing runtime metrics.
        /// </summary>
        public const string StatsEndpoint = "/stats";

        /// <summary>
        /// Tag applied to health checks that participate in liveness probes.
        /// </summary>
        public const string LiveTag = "live";
    }

    /// <summary>
    /// OpenTelemetry and monitoring configuration.
    /// </summary>
    /// <seealso cref="ServiceNames"/>
    public static class Monitoring
    {
        /// <summary>
        /// OpenTelemetry service name used for traces, metrics, and logs.
        /// </summary>
        public const string ServiceName = "Nocturne";

        /// <summary>
        /// Semantic version reported in telemetry data.
        /// </summary>
        public const string ServiceVersion = "1.0.0";
    }

    /// <summary>
    /// CORS policy configuration for the API.
    /// </summary>
    public static class Cors
    {
        /// <summary>
        /// Name of the default CORS policy registered in the middleware pipeline.
        /// </summary>
        public const string DefaultPolicy = "DefaultCorsPolicy";

        /// <summary>
        /// HTTP methods permitted by the default CORS policy.
        /// </summary>
        public static readonly string[] DefaultMethods =
        {
            "GET",
            "POST",
            "PUT",
            "DELETE",
            "OPTIONS",
        };

        /// <summary>
        /// HTTP headers permitted by the default CORS policy.
        /// </summary>
        public static readonly string[] DefaultHeaders =
        {
            "Content-Type",
            "Authorization",
            "X-Requested-With",
        };
    }

    /// <summary>
    /// Relative file system paths for application data directories.
    /// </summary>
    public static class Paths
    {
        /// <summary>
        /// Directory for persistent application data files.
        /// </summary>
        public const string DataDirectory = "./data";

        /// <summary>
        /// Directory for log file output.
        /// </summary>
        public const string LogDirectory = "./logs";

        /// <summary>
        /// Directory for configuration files.
        /// </summary>
        public const string ConfigDirectory = "./config";

        /// <summary>
        /// Directory for temporary files that can be safely purged.
        /// </summary>
        public const string TempDirectory = "./temp";
    }

    /// <summary>
    /// Default database connection parameters for local development.
    /// </summary>
    /// <remarks>
    /// Production deployments use secrets managed via Aspire parameters.
    /// See <see cref="ServiceNames.Parameters"/> for parameter names.
    /// </remarks>
    public static class Database
    {
        /// <summary>
        /// Default Aspire resource name for the PostgreSQL database.
        /// </summary>
        public const string DefaultDatabaseName = "nocturne-database";

        /// <summary>
        /// Default database username for local development.
        /// </summary>
        public const string DefaultUsername = "nocturne-user";

        /// <summary>
        /// Default database password for local development.
        /// </summary>
        public const string DefaultPassword = "nocturne_password";

        /// <summary>
        /// Default root/superuser password for local development.
        /// </summary>
        public const string RootPassword = "nocturne_root_password";
    }

    /// <summary>
    /// Web application client-facing settings and defaults.
    /// </summary>
    public static class Web
    {
        /// <summary>
        /// Default values for client-side display and behavior settings.
        /// These are applied when a user has not customized their preferences.
        /// </summary>
        public static class ClientDefaults
        {
            /// <summary>
            /// Default glucose display unit. Either "mg/dl" or "mmol".
            /// </summary>
            public const string Units = "mg/dl";

            /// <summary>
            /// Default time format: 12-hour or 24-hour clock.
            /// </summary>
            public const int TimeFormat = 12;

            /// <summary>
            /// Whether night mode (dark theme) is enabled by default.
            /// </summary>
            public const bool NightMode = false;

            /// <summary>
            /// Whether to show the blood glucose on-screen by default.
            /// </summary>
            public const bool ShowBGON = true;

            /// <summary>
            /// Whether to show insulin on board (IOB) by default.
            /// </summary>
            public const bool ShowIOB = true;

            /// <summary>
            /// Whether to show carbs on board (COB) by default.
            /// </summary>
            public const bool ShowCOB = true;

            /// <summary>
            /// Whether to show basal rate by default.
            /// </summary>
            public const bool ShowBasal = true;

            /// <summary>
            /// Default UI language code (ISO 639-1).
            /// </summary>
            public const string Language = "en";

            /// <summary>
            /// Default visual theme name.
            /// </summary>
            public const string Theme = "default";

            /// <summary>
            /// Whether the urgent high glucose alarm is enabled by default.
            /// </summary>
            public const bool AlarmUrgentHigh = true;

            /// <summary>
            /// Whether the high glucose alarm is enabled by default.
            /// </summary>
            public const bool AlarmHigh = true;

            /// <summary>
            /// Whether the low glucose alarm is enabled by default.
            /// </summary>
            public const bool AlarmLow = true;

            /// <summary>
            /// Whether the urgent low glucose alarm is enabled by default.
            /// </summary>
            public const bool AlarmUrgentLow = true;

            /// <summary>
            /// Whether the stale data warning alarm is enabled by default.
            /// </summary>
            public const bool AlarmTimeagoWarn = true;

            /// <summary>
            /// Minutes of data staleness before triggering a warning alarm.
            /// </summary>
            public const int AlarmTimeagoWarnMins = 15;

            /// <summary>
            /// Whether the urgent stale data alarm is enabled by default.
            /// </summary>
            public const bool AlarmTimeagoUrgent = true;

            /// <summary>
            /// Minutes of data staleness before triggering an urgent alarm.
            /// </summary>
            public const int AlarmTimeagoUrgentMins = 30;

            /// <summary>
            /// Whether to display glucose forecast/prediction lines by default.
            /// </summary>
            public const bool ShowForecast = true;

            /// <summary>
            /// Default number of hours to display in the main chart view.
            /// </summary>
            public const int FocusHours = 3;

            /// <summary>
            /// Interval in seconds between heartbeat pings from the client.
            /// </summary>
            public const int Heartbeat = 60;

            /// <summary>
            /// Default roles assigned to unauthenticated users.
            /// </summary>
            public const string AuthDefaultRoles = "readable";
        }

        /// <summary>
        /// Defaults for the four legacy Nightscout <c>bg*</c> threshold settings, in mg/dL. They are
        /// what ships when nothing else supplies a value: the status and tenant-overview services
        /// fall back to them when the matching <c>Thresholds:Bg*</c> configuration key is absent, and
        /// the properties and AR2 services use them for the <c>bgTarget*</c> entries of the settings
        /// map they build and read, which never consults configuration at all. They are client alarm
        /// settings, not a clinical range — <see cref="GlucoseConstants.TargetBottomMgdl"/> and
        /// <see cref="GlucoseConstants.TargetTopMgdl"/> are the consensus in-range boundaries, and
        /// <see cref="BgTargetBottom"/> deliberately differs from the former. Collapsing the two
        /// families onto each other moves either every tenant's default alarm threshold or every
        /// time-in-range denominator.
        /// </summary>
        public static class Thresholds
        {
            /// <summary>
            /// Glucose level (mg/dL) at or above which readings are categorized as urgent-high.
            /// </summary>
            public const int BgHigh = 260;

            /// <summary>
            /// Upper bound (mg/dL) of the legacy target band.
            /// </summary>
            public const int BgTargetTop = 180;

            /// <summary>
            /// Lower bound (mg/dL) of the legacy target band. Nightscout has always shipped 80 here,
            /// which is not the 70 that time-in-range is measured against.
            /// </summary>
            public const int BgTargetBottom = 80;

            /// <summary>
            /// Glucose level (mg/dL) at or below which readings are categorized as urgent-low.
            /// </summary>
            public const int BgLow = 55;
        }

        /// <summary>
        /// Configuration defaults for the dedicated clock/kiosk display mode.
        /// </summary>
        public static class Clock
        {
            /// <summary>
            /// Maximum minutes since the last reading before the clock display shows a stale-data indicator.
            /// </summary>
            public const int MaxStaleMinutes = 60;

            /// <summary>
            /// Element identifiers that can appear in the clock view:
            /// sg (sensor glucose), dt (delta), ar (arrow/direction), ag (age/staleness), time.
            /// </summary>
            public static readonly string[] AllowedElements = { "sg", "dt", "ar", "ag", "time" };

            /// <summary>
            /// Minimum and maximum font size percentages for each clock element,
            /// governing how large each element can be rendered.
            /// </summary>
            public static class SizeConstraints
            {
                /// <summary>Minimum font size percentage for the sensor glucose element.</summary>
                public const int SgMin = 20;

                /// <summary>Maximum font size percentage for the sensor glucose element.</summary>
                public const int SgMax = 80;

                /// <summary>Minimum font size percentage for the delta element.</summary>
                public const int DtMin = 10;

                /// <summary>Maximum font size percentage for the delta element.</summary>
                public const int DtMax = 40;

                /// <summary>Minimum font size percentage for the arrow/direction element.</summary>
                public const int ArMin = 15;

                /// <summary>Maximum font size percentage for the arrow/direction element.</summary>
                public const int ArMax = 50;

                /// <summary>Minimum font size percentage for the age/staleness element.</summary>
                public const int AgMin = 8;

                /// <summary>Maximum font size percentage for the age/staleness element.</summary>
                public const int AgMax = 24;

                /// <summary>Minimum font size percentage for the time element.</summary>
                public const int TimeMin = 16;

                /// <summary>Maximum font size percentage for the time element.</summary>
                public const int TimeMax = 48;
            }
        }
    }

    /// <summary>
    /// Nightscout-compatible plugin names and identifiers used to enable
    /// or disable feature modules in the web client.
    /// </summary>
    public static class Plugins
    {
        /// <summary>Plugin that computes the glucose rate of change.</summary>
        public const string Delta = "delta";

        /// <summary>Plugin that displays the glucose trend direction arrow.</summary>
        public const string Direction = "direction";

        /// <summary>Plugin that shows how long ago the last reading was received.</summary>
        public const string TimeAgo = "timeago";

        /// <summary>Plugin that displays device status information (uploader battery, etc.).</summary>
        public const string DeviceStatus = "devicestatus";

        /// <summary>Plugin that renders the current basal rate.</summary>
        public const string Basal = "basal";

        /// <summary>Plugin for Dexcom Share bridge integration.</summary>
        public const string Bridge = "bridge";

        /// <summary>Plugin that tracks cannula/infusion set age (CAGE).</summary>
        public const string CannulaAge = "cage";

        /// <summary>Plugin that tracks sensor age (SAGE).</summary>
        public const string SensorAge = "sage";

        /// <summary>Plugin that tracks insulin reservoir age (IAGE).</summary>
        public const string InsulinAge = "iage";

        /// <summary>Plugin that tracks pump battery age (BAGE).</summary>
        public const string BatteryAge = "bage";

        /// <summary>Plugin that renders the programmed basal profile. Shares the identifier with <see cref="Basal"/>.</summary>
        public const string BasalProfile = "basal";

        /// <summary>Plugin for Dexcom Share-to-Nightscout bridging. Shares the identifier with <see cref="Bridge"/>.</summary>
        public const string Share2Nightscout = "bridge";

        /// <summary>Plugin for Medtronic MiniMed Connect integration.</summary>
        public const string MiniMedConnect = "mmconnect";

        /// <summary>Plugin that displays insulin pump status and reservoir level.</summary>
        public const string Pump = "pump";

        /// <summary>Plugin for OpenAPS closed-loop system status display.</summary>
        public const string OpenAPS = "openaps";

        /// <summary>Plugin for Loop iOS closed-loop system status display.</summary>
        public const string Loop = "loop";

        /// <summary>Plugin for displaying active temporary overrides from Loop or similar systems.</summary>
        public const string Override = "override";

        /// <summary>
        /// Plugins shown by default when a user has not customized their plugin list.
        /// </summary>
        public static readonly string[] DefaultShowPlugins =
        {
            Delta,
            Direction,
            TimeAgo,
            DeviceStatus,
        };
    }

    /// <summary>
    /// API route segment constants and YARP proxy patterns, organized by API version.
    /// </summary>
    /// <remarks>
    /// <see cref="Native"/> and <see cref="V2Native"/> list routes handled natively by Nocturne controllers.
    /// <see cref="Patterns"/> contains regex patterns for YARP to proxy unrecognized routes to a legacy Nightscout instance.
    /// </remarks>
    public static class ApiRoutes
    {
        /// <summary>
        /// V1 API route segments handled natively by Nocturne controllers.
        /// </summary>
        /// <remarks>
        /// These correspond to controllers such as <c>StatusController</c>, <c>EntriesController</c>,
        /// <c>TreatmentsController</c>, <c>DeviceStatusController</c>, and others in the V1 namespace.
        /// </remarks>
        public static class Native
        {
            /// <summary>Route segment for the status endpoint.</summary>
            public const string Status = "status";

            /// <summary>Route segment for the CGM entries endpoint.</summary>
            public const string Entries = "entries";

            /// <summary>Route segment for the treatments endpoint.</summary>
            public const string Treatments = "treatments";

            /// <summary>Route segment for the device status endpoint.</summary>
            public const string DeviceStatus = "devicestatus";

            /// <summary>Route segment for the profile endpoint.</summary>
            public const string Profile = "profile";

            /// <summary>Route segment for the food database endpoint.</summary>
            public const string Food = "food";

            /// <summary>Route segment for the activity endpoint.</summary>
            public const string Activity = "activity";

            /// <summary>Route segment for the count endpoint.</summary>
            public const string Count = "count";

            /// <summary>Route segment for the echo/ping endpoint.</summary>
            public const string Echo = "echo";

            /// <summary>Route segment for time-based query endpoint.</summary>
            public const string Times = "times";

            /// <summary>Route segment for time-slice query endpoint.</summary>
            public const string Slice = "slice";

            /// <summary>Route segment for the authentication verification endpoint.</summary>
            public const string VerifyAuth = "verifyauth";

            /// <summary>Route segment for the notifications endpoint.</summary>
            public const string Notifications = "notifications";

            /// <summary>Route segment for the admin notifications endpoint.</summary>
            public const string AdminNotifies = "adminnotifies";
        }

        /// <summary>
        /// V2 API route segments handled natively by Nocturne controllers.
        /// </summary>
        /// <remarks>
        /// These correspond to controllers such as <c>DDataController</c>, <c>PropertiesController</c>,
        /// <c>SummaryController</c>, and others in the V2 namespace.
        /// </remarks>
        public static class V2Native
        {
            /// <summary>Route segment for the aggregated data download endpoint.</summary>
            public const string DData = "ddata";

            /// <summary>Route segment for the server properties endpoint.</summary>
            public const string Properties = "properties";

            /// <summary>Route segment for the data summary endpoint.</summary>
            public const string Summary = "summary";

            /// <summary>Route segment for the V2 notifications endpoint.</summary>
            public const string Notifications = "notifications";

            /// <summary>Route segment for the V2 authorization endpoint.</summary>
            public const string Authorization = "authorization";
        }

        /// <summary>
        /// Regex patterns used by YARP to proxy requests that do not match any
        /// <see cref="Native"/> or <see cref="V2Native"/> route to a legacy Nightscout instance.
        /// </summary>
        public static class Patterns
        {
            /// <summary>
            /// Negative-lookahead regex matching V1 routes NOT handled natively,
            /// causing them to be proxied to the legacy Nightscout backend.
            /// </summary>
            public const string LegacyApiV1 =
                "^(?!status|entries|treatments|devicestatus|profile|food|activity|count|echo|times|slice|verifyauth|notifications|adminnotifies).*$";

            /// <summary>
            /// Negative-lookahead regex matching V2 routes NOT handled natively,
            /// causing them to be proxied to the legacy Nightscout backend.
            /// </summary>
            public const string LegacyApiV2 =
                "^(?!ddata|properties|summary|notifications|authorization).*$";

            /// <summary>
            /// Catch-all pattern that proxies all V3 API requests to the legacy Nightscout backend.
            /// </summary>
            public const string LegacyApiV3 = "{**catch-all}";
        }
    }
}
