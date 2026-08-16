namespace Nocturne.Core.Constants;

/// <summary>
/// Environment variable names read by connector services to configure their upstream API connections.
/// </summary>
/// <remarks>
/// These mirror the environment variables used by legacy Nightscout cgm-remote-monitor and its
/// connect plugins. Connectors read these at startup to authenticate with upstream data sources.
/// Default values when a variable is unset are defined in <see cref="ConnectorDefaults"/>.
/// </remarks>
/// <seealso cref="ConnectorDefaults"/>
/// <seealso cref="DataSources"/>
public static class ConnectorEnvironmentVariables
{
    // ========================================================================
    // Core Configuration
    // ========================================================================

    /// <summary>
    /// MongoDB connection string for legacy Nightscout data migration.
    /// Uses the Azure-style CUSTOMCONNSTR_ prefix for compatibility.
    /// </summary>
    public const string MongoConnectionString = "CUSTOMCONNSTR_mongo";

    /// <summary>
    /// Name of the MongoDB collection containing CGM entries.
    /// </summary>
    /// <seealso cref="ConnectorDefaults.MongoCollection"/>
    public const string MongoCollection = "MONGO_COLLECTION";

    /// <summary>
    /// Name of the MongoDB collection containing user profiles.
    /// </summary>
    /// <seealso cref="ConnectorDefaults.MongoProfileCollection"/>
    public const string MongoProfileCollection = "MONGO_PROFILE_COLLECTION";

    /// <summary>
    /// Nightscout API secret (SHA-1 hash or plain text) for authenticating with the local instance.
    /// </summary>
    public const string ApiSecret = "API_SECRET";

    /// <summary>
    /// Hostname the server binds to.
    /// </summary>
    /// <seealso cref="ConnectorDefaults.Hostname"/>
    public const string Hostname = "HOSTNAME";

    /// <summary>
    /// Comma-separated list of enabled feature plugins.
    /// </summary>
    public const string Enable = "ENABLE";

    /// <summary>
    /// Comma-separated list of plugins to display in the web client header.
    /// </summary>
    public const string ShowPlugins = "SHOW_PLUGINS";

    /// <summary>
    /// Glucose display unit: "mg/dl" or "mmol".
    /// </summary>
    /// <seealso cref="ConnectorDefaults.DisplayUnits"/>
    public const string DisplayUnits = "DISPLAY_UNITS";

    /// <summary>
    /// Time display format: 12 or 24.
    /// </summary>
    /// <seealso cref="ConnectorDefaults.TimeFormat"/>
    public const string TimeFormat = "TIME_FORMAT";

    /// <summary>
    /// Alarm type configuration (e.g., "simple" or "predict").
    /// </summary>
    public const string AlarmTypes = "ALARM_TYPES";

    /// <summary>
    /// UI language code (ISO 639-1).
    /// </summary>
    /// <seealso cref="ConnectorDefaults.Language"/>
    public const string Language = "LANGUAGE";

    /// <summary>
    /// When set to "true", allows insecure HTTP connections instead of requiring HTTPS.
    /// </summary>
    public const string InsecureUseHttp = "INSECURE_USE_HTTP";

    /// <summary>
    /// TCP port the server listens on.
    /// </summary>
    public const string Port = "PORT";

    /// <summary>
    /// Node.js environment mode (e.g., "production", "development").
    /// Retained for legacy Nightscout compatibility.
    /// </summary>
    /// <seealso cref="ConnectorDefaults.NodeEnvironment"/>
    public const string NodeEnvironment = "NODE_ENV";

    /// <summary>
    /// Default roles assigned to unauthenticated API consumers.
    /// </summary>
    public const string AuthDefaultRoles = "AUTH_DEFAULT_ROLES";

    /// <summary>
    /// Delay in milliseconds applied after a failed authentication attempt to mitigate brute-force attacks.
    /// </summary>
    /// <seealso cref="ConnectorDefaults.AuthFailDelay"/>
    public const string AuthFailDelay = "AUTH_FAIL_DELAY";

    // ========================================================================
    // Connect Source Configuration
    // ========================================================================

    /// <summary>
    /// Identifies the upstream data source type (e.g., "dexcom", "libre", "glooko").
    /// </summary>
    /// <seealso cref="DataSources"/>
    public const string ConnectSource = "CONNECT_SOURCE";

    // ========================================================================
    // Nightscout Target Configuration
    // ========================================================================

    /// <summary>
    /// Base URL of the downstream Nightscout instance that receives pushed data.
    /// </summary>
    public const string NightscoutUrl = "NIGHTSCOUT_URL";

    /// <summary>
    /// API secret for authenticating with the downstream Nightscout instance.
    /// </summary>
    public const string NightscoutApiSecret = "NIGHTSCOUT_API_SECRET";

    /// <summary>
    /// JWT or token-based API credential for the downstream Nightscout instance.
    /// </summary>
    public const string ApiToken = "API_TOKEN";

    /// <summary>
    /// Generic URL parameter used by some connector configurations.
    /// </summary>
    public const string Url = "url";

    // ========================================================================
    // Glooko Configuration
    // ========================================================================

    /// <summary>
    /// Email address for Glooko API authentication.
    /// </summary>
    /// <seealso cref="DataSources.GlookoConnector"/>
    public const string GlookoEmail = "CONNECT_GLOOKO_EMAIL";

    /// <summary>
    /// Password for Glooko API authentication.
    /// </summary>
    public const string GlookoPassword = "CONNECT_GLOOKO_PASSWORD";

    /// <summary>
    /// Timezone offset in hours for Glooko data timestamps.
    /// </summary>
    /// <seealso cref="ConnectorDefaults.GlookoTimezoneOffset"/>
    public const string GlookoTimezoneOffset = "CONNECT_GLOOKO_TIMEZONE_OFFSET";

    /// <summary>
    /// Glooko API server hostname.
    /// </summary>
    /// <seealso cref="ConnectorDefaults.GlookoServer"/>
    public const string GlookoServer = "CONNECT_GLOOKO_SERVER";

    // ========================================================================
    // MiniMed CareLink Configuration
    // ========================================================================

    /// <summary>
    /// Username for Medtronic CareLink API authentication.
    /// </summary>
    /// <seealso cref="DataSources.MiniMedConnector"/>
    public const string CarelinkUsername = "CONNECT_CARELINK_USERNAME";

    /// <summary>
    /// Password for Medtronic CareLink API authentication.
    /// </summary>
    public const string CarelinkPassword = "CONNECT_CARELINK_PASSWORD";

    /// <summary>
    /// CareLink regional server identifier (e.g., "us", "eu").
    /// </summary>
    /// <seealso cref="ConnectorDefaults.CarelinkRegion"/>
    public const string CarelinkRegion = "CONNECT_CARELINK_REGION";

    /// <summary>
    /// ISO country code for the CareLink account.
    /// </summary>
    public const string CarelinkCountryCode = "CONNECT_CARELINK_COUNTRY";

    /// <summary>
    /// Username of the specific patient when the CareLink account manages multiple patients.
    /// </summary>
    public const string CarelinkPatientUsername = "CONNECT_CARELINK_PATIENT_USERNAME";

    // ========================================================================
    // Dexcom Share Configuration
    // ========================================================================

    /// <summary>
    /// Dexcom Share account name (email or username).
    /// </summary>
    /// <seealso cref="DataSources.DexcomConnector"/>
    public const string DexcomUsername = "CONNECT_SHARE_ACCOUNT_NAME";

    /// <summary>
    /// Password for Dexcom Share authentication.
    /// </summary>
    public const string DexcomPassword = "CONNECT_SHARE_PASSWORD";

    /// <summary>
    /// Dexcom Share regional server identifier (e.g., "us", "ous" for outside-US).
    /// </summary>
    /// <seealso cref="ConnectorDefaults.DexcomRegion"/>
    public const string DexcomRegion = "CONNECT_SHARE_REGION";

    /// <summary>
    /// Explicit Dexcom Share server URL, overriding region-based resolution.
    /// </summary>
    public const string DexcomServer = "CONNECT_SHARE_SERVER";

    // ========================================================================
    // LibreLinkUp Configuration
    // ========================================================================

    /// <summary>
    /// Username (email) for FreeStyle LibreLinkUp authentication.
    /// </summary>
    /// <seealso cref="DataSources.LibreConnector"/>
    public const string LibreUsername = "CONNECT_LINK_UP_USERNAME";

    /// <summary>
    /// Password for FreeStyle LibreLinkUp authentication.
    /// </summary>
    public const string LibrePassword = "CONNECT_LINK_UP_PASSWORD";

    /// <summary>
    /// LibreLinkUp regional server identifier (e.g., "EU", "US", "AP").
    /// </summary>
    /// <seealso cref="ConnectorDefaults.LibreRegion"/>
    public const string LibreRegion = "CONNECT_LINK_UP_REGION";

    /// <summary>
    /// Explicit LibreLinkUp server URL, overriding region-based resolution.
    /// </summary>
    public const string LibreServer = "CONNECT_LINK_UP_SERVER";

    /// <summary>
    /// Patient identifier when the LibreLinkUp account follows multiple patients.
    /// </summary>
    public const string LibrePatientId = "CONNECT_LINK_UP_PATIENT_ID";

    // ========================================================================
    // MyLife (CamAPS / YpsoPump) Configuration
    // ========================================================================

    /// <summary>
    /// Username for MyLife (CamAPS / YpsoPump) API authentication.
    /// </summary>
    /// <seealso cref="DataSources.MyLifeConnector"/>
    public const string MyLifeUsername = "CONNECT_MYLIFE_USERNAME";

    /// <summary>
    /// Password for MyLife API authentication.
    /// </summary>
    public const string MyLifePassword = "CONNECT_MYLIFE_PASSWORD";

    /// <summary>
    /// Patient identifier when the MyLife account manages multiple patients.
    /// </summary>
    public const string MyLifePatientId = "CONNECT_MYLIFE_PATIENT_ID";

    /// <summary>
    /// Base URL for the MyLife API service.
    /// </summary>
    public const string MyLifeServiceUrl = "CONNECT_MYLIFE_SERVICE_URL";

    /// <summary>
    /// When "true", enables syncing CGM glucose readings from MyLife.
    /// </summary>
    public const string MyLifeEnableGlucoseSync = "CONNECT_MYLIFE_ENABLE_GLUCOSE_SYNC";

    /// <summary>
    /// When "true", enables syncing manual blood glucose readings from MyLife.
    /// </summary>
    public const string MyLifeEnableManualBgSync = "CONNECT_MYLIFE_ENABLE_MANUAL_BG_SYNC";

    /// <summary>
    /// When "true", consolidates individual carb entries into meal-level treatments.
    /// </summary>
    public const string MyLifeEnableMealCarbConsolidation = "CONNECT_MYLIFE_ENABLE_MEAL_CARB_CONSOLIDATION";

    /// <summary>
    /// When "true", consolidates rapid temp basal changes into single treatment records.
    /// </summary>
    public const string MyLifeEnableTempBasalConsolidation = "CONNECT_MYLIFE_ENABLE_TEMP_BASAL_CONSOLIDATION";

    /// <summary>
    /// Time window in minutes within which consecutive temp basal changes are consolidated.
    /// </summary>
    public const string MyLifeTempBasalConsolidationWindowMinutes = "CONNECT_MYLIFE_TEMP_BASAL_CONSOLIDATION_WINDOW_MINUTES";

    /// <summary>
    /// Numeric platform identifier sent as the appPlatform argument of the MyLife Login
    /// SOAP request. Defaults to 2.
    /// </summary>
    public const string MyLifeAppPlatform = "CONNECT_MYLIFE_APP_PLATFORM";

    /// <summary>
    /// Numeric app version sent as the appVersion argument of the MyLife Login SOAP request
    /// for compatibility negotiation. Defaults to 20403.
    /// </summary>
    public const string MyLifeAppVersion = "CONNECT_MYLIFE_APP_VERSION";

    // ========================================================================
    // Nightscout Source Configuration
    // ========================================================================

    /// <summary>
    /// Base URL of an upstream Nightscout instance used as a data source.
    /// </summary>
    /// <seealso cref="DataSources.NightscoutConnector"/>
    public const string SourceEndpoint = "CONNECT_SOURCE_ENDPOINT";

    /// <summary>
    /// API secret for authenticating with the upstream Nightscout source.
    /// </summary>
    public const string SourceApiSecret = "CONNECT_SOURCE_API_SECRET";

    // ========================================================================
    // Proxy Configuration
    // ========================================================================

    /// <summary>
    /// Target URL for the YARP reverse proxy when proxying to a legacy Nightscout instance.
    /// </summary>
    /// <seealso cref="ApplicationConstants.Proxy"/>
    public const string NightscoutTargetUrl = "NIGHTSCOUT_TARGET_URL";

    // ========================================================================
    // Loop Configuration
    // ========================================================================

    /// <summary>
    /// Apple Push Notification Service (APNs) private key in PEM format for Loop remote commands.
    /// </summary>
    /// <seealso cref="DataSources.Loop"/>
    public const string LoopApnsKey = "LOOP_APNS_KEY";

    /// <summary>
    /// APNs key identifier associated with the <see cref="LoopApnsKey"/>.
    /// </summary>
    public const string LoopApnsKeyId = "LOOP_APNS_KEY_ID";

    /// <summary>
    /// Apple Developer Team ID that owns the Loop APNs certificate.
    /// </summary>
    public const string LoopDeveloperTeamId = "LOOP_DEVELOPER_TEAM_ID";

    /// <summary>
    /// APNs environment: "development" (sandbox) or "production".
    /// </summary>
    /// <seealso cref="ConnectorDefaults.LoopPushServerEnvironment"/>
    public const string LoopPushServerEnvironment = "LOOP_PUSH_SERVER_ENVIRONMENT";
}

/// <summary>
/// Default values applied when the corresponding <see cref="ConnectorEnvironmentVariables"/>
/// environment variable is not set.
/// </summary>
/// <seealso cref="ConnectorEnvironmentVariables"/>
public static class ConnectorDefaults
{
    /// <summary>
    /// Default MongoDB collection name for CGM entries.
    /// </summary>
    /// <seealso cref="ConnectorEnvironmentVariables.MongoCollection"/>
    public const string MongoCollection = "entries";

    /// <summary>
    /// Default MongoDB collection name for user profiles.
    /// </summary>
    /// <seealso cref="ConnectorEnvironmentVariables.MongoProfileCollection"/>
    public const string MongoProfileCollection = "profile";

    /// <summary>
    /// Default bind address (all interfaces).
    /// </summary>
    public const string Hostname = "0.0.0.0";

    /// <summary>
    /// Default glucose display unit.
    /// </summary>
    public const string DisplayUnits = "mmol";

    /// <summary>
    /// Default time format (24-hour clock).
    /// </summary>
    public const int TimeFormat = 24;

    /// <summary>
    /// Default UI language.
    /// </summary>
    public const string Language = "en";

    /// <summary>
    /// Default Node.js environment mode, retained for legacy Nightscout compatibility.
    /// </summary>
    public const string NodeEnvironment = "development";

    /// <summary>
    /// Default authentication failure delay in milliseconds.
    /// </summary>
    public const int AuthFailDelay = 50;

    /// <summary>
    /// Default Glooko API server hostname.
    /// </summary>
    /// <seealso cref="ConnectorEnvironmentVariables.GlookoServer"/>
    public const string GlookoServer = "eu.api.glooko.com";

    /// <summary>
    /// Default timezone offset in hours for Glooko data.
    /// </summary>
    public const int GlookoTimezoneOffset = 0;

    /// <summary>
    /// Default CareLink regional server.
    /// </summary>
    /// <seealso cref="ConnectorEnvironmentVariables.CarelinkRegion"/>
    public const string CarelinkRegion = "us";

    /// <summary>
    /// Default Dexcom Share regional server.
    /// </summary>
    /// <seealso cref="ConnectorEnvironmentVariables.DexcomRegion"/>
    public const string DexcomRegion = "us";

    /// <summary>
    /// Default LibreLinkUp regional server.
    /// </summary>
    /// <seealso cref="ConnectorEnvironmentVariables.LibreRegion"/>
    public const string LibreRegion = "EU";

    /// <summary>
    /// Default APNs environment for Loop push notifications.
    /// </summary>
    /// <seealso cref="ConnectorEnvironmentVariables.LoopPushServerEnvironment"/>
    public const string LoopPushServerEnvironment = "development";
}

/// <summary>
/// Timeout and delay constants governing connector HTTP clients, retry policies,
/// session lifetimes, health checks, and WebSocket reconnection behavior.
/// </summary>
/// <seealso cref="ConnectorHttpStatus"/>
/// <seealso cref="DataSources"/>
public static class ConnectorTimeouts
{
    /// <summary>
    /// HTTP client timeout durations for upstream API calls.
    /// </summary>
    public static class Http
    {
        /// <summary>
        /// Standard timeout for most connector HTTP requests (2 minutes).
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Shorter timeout for lightweight or health-check requests (30 seconds).
        /// </summary>
        public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Extended timeout for bulk data downloads or slow endpoints (5 minutes).
        /// </summary>
        public static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Delay durations between retry attempts, using an exponential backoff strategy.
    /// </summary>
    /// <seealso cref="ConnectorHttpStatus.RetryableStatusCodes"/>
    public static class Retry
    {
        /// <summary>
        /// Delay before the first retry attempt.
        /// </summary>
        public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Delay before the second retry attempt.
        /// </summary>
        public static readonly TimeSpan SecondRetry = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Delay before the third retry attempt.
        /// </summary>
        public static readonly TimeSpan ThirdRetry = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Delay applied when the upstream API returns HTTP 429 (Too Many Requests).
        /// </summary>
        public static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Delay applied after an authentication failure before re-authenticating.
        /// </summary>
        /// <seealso cref="ConnectorHttpStatus.AuthenticationStatusCodes"/>
        public static readonly TimeSpan AuthenticationDelay = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Session and authentication token lifetimes for each upstream data source.
    /// Connectors re-authenticate when the session nears expiration.
    /// </summary>
    public static class Session
    {
        /// <summary>
        /// Default session expiration when no source-specific duration is defined.
        /// </summary>
        public static readonly TimeSpan DefaultExpiration = TimeSpan.FromHours(8);

        /// <summary>
        /// Time before session expiry at which a proactive refresh is triggered.
        /// </summary>
        public static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Dexcom Share session lifetime before re-authentication is required.
        /// </summary>
        /// <seealso cref="DataSources.DexcomConnector"/>
        public static readonly TimeSpan DexcomSessionDuration = TimeSpan.FromHours(3);

        /// <summary>
        /// LibreLinkUp authentication token lifetime.
        /// </summary>
        /// <seealso cref="DataSources.LibreConnector"/>
        public static readonly TimeSpan LibreTokenDuration = TimeSpan.FromHours(2);

        /// <summary>
        /// Medtronic CareLink session lifetime.
        /// </summary>
        /// <seealso cref="DataSources.MiniMedConnector"/>
        public static readonly TimeSpan CarelinkSessionDuration = TimeSpan.FromHours(6);

        /// <summary>
        /// Glooko API token lifetime.
        /// </summary>
        /// <seealso cref="DataSources.GlookoConnector"/>
        public static readonly TimeSpan GlookoTokenDuration = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Intervals for connector health monitoring and stale-state recovery.
    /// </summary>
    public static class Health
    {
        /// <summary>
        /// Interval between periodic health checks of connector upstream connections.
        /// </summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Interval after which accumulated error counters are reset to zero.
        /// </summary>
        public static readonly TimeSpan ResetInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// Duration of failed health checks before an alert is raised.
        /// </summary>
        public static readonly TimeSpan AlertThreshold = TimeSpan.FromMinutes(15);
    }

    /// <summary>
    /// WebSocket reconnection and shutdown timing constants.
    /// </summary>
    /// <seealso cref="WebSocketEvents"/>
    public static class WebSocket
    {
        /// <summary>
        /// Maximum number of reconnection attempts before giving up.
        /// </summary>
        public const int ReconnectAttempts = 10;

        /// <summary>
        /// Initial delay in milliseconds between reconnection attempts.
        /// </summary>
        public const int ReconnectDelay = 5000; // milliseconds

        /// <summary>
        /// Maximum delay in milliseconds between reconnection attempts (caps exponential backoff).
        /// </summary>
        public const int MaxReconnectDelay = 30000; // milliseconds

        /// <summary>
        /// Time in milliseconds to wait for in-flight messages to complete during graceful shutdown.
        /// </summary>
        public const int GracefulShutdownTimeout = 10000; // milliseconds
    }
}

/// <summary>
/// HTTP status code classifications used by connector retry policies to determine
/// whether a failed request should be retried, requires re-authentication, or is terminal.
/// </summary>
/// <seealso cref="ConnectorTimeouts.Retry"/>
public static class ConnectorHttpStatus
{
    /// <summary>
    /// HTTP status codes that indicate a transient failure and should trigger a retry
    /// with exponential backoff per <see cref="ConnectorTimeouts.Retry"/>.
    /// </summary>
    public static readonly int[] RetryableStatusCodes =
    {
        429, // Too Many Requests
        500, // Internal Server Error
        502, // Bad Gateway
        503, // Service Unavailable
        504, // Gateway Timeout
        408, // Request Timeout
    };

    /// <summary>
    /// HTTP status codes indicating the connector's session or token has expired
    /// and re-authentication is needed before retrying.
    /// </summary>
    /// <seealso cref="ConnectorTimeouts.Retry.AuthenticationDelay"/>
    public static readonly int[] AuthenticationStatusCodes =
    {
        401, // Unauthorized
        403, // Forbidden
    };

    /// <summary>
    /// HTTP status codes representing client errors that will not succeed on retry
    /// and should be surfaced immediately.
    /// </summary>
    /// <remarks>
    /// HTTP 422 (Unprocessable Entity) is listed here but may be treated as retryable
    /// for the Glooko connector, which uses 422 for rate limiting.
    /// </remarks>
    public static readonly int[] NonRetryableStatusCodes =
    {
        400, // Bad Request
        404, // Not Found
        405, // Method Not Allowed
        406, // Not Acceptable
        409, // Conflict
        410, // Gone
        422, // Unprocessable Entity (except for Glooko rate limiting)
    };
}
