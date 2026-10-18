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
    /// Nightscout API levels Nocturne reports for client compatibility.
    /// </summary>
    public static class NightscoutApi
    {
        /// <summary>
        /// The v3 API level reported as <c>apiVersion</c> by <c>/api/v3/status</c> and <c>/api/v3/version</c>,
        /// matching upstream Nightscout.
        /// </summary>
        public const string V3Version = "3.0.5";
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
