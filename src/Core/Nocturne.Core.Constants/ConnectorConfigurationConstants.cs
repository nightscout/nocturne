namespace Nocturne.Core.Constants;

/// <summary>
/// Environment variable names used by connectors
/// </summary>
public static class ConnectorEnvironmentVariables
{
    // Core Configuration
    public const string MongoConnectionString = "CUSTOMCONNSTR_mongo";
    public const string MongoCollection = "MONGO_COLLECTION";
    public const string MongoProfileCollection = "MONGO_PROFILE_COLLECTION";
    public const string ApiSecret = "API_SECRET";
    public const string Hostname = "HOSTNAME";
    public const string Enable = "ENABLE";
    public const string ShowPlugins = "SHOW_PLUGINS";
    public const string DisplayUnits = "DISPLAY_UNITS";
    public const string TimeFormat = "TIME_FORMAT";
    public const string AlarmTypes = "ALARM_TYPES";
    public const string Language = "LANGUAGE";
    public const string InsecureUseHttp = "INSECURE_USE_HTTP";
    public const string Port = "PORT";
    public const string NodeEnvironment = "NODE_ENV";
    public const string AuthDefaultRoles = "AUTH_DEFAULT_ROLES";
    public const string AuthFailDelay = "AUTH_FAIL_DELAY";

    // Connect Source Configuration
    public const string ConnectSource = "CONNECT_SOURCE";

    // Nightscout Target Configuration
    public const string NightscoutUrl = "NIGHTSCOUT_URL";
    public const string NightscoutApiSecret = "NIGHTSCOUT_API_SECRET";
    public const string ApiToken = "API_TOKEN";
    public const string Url = "url";

    // Glooko Configuration
    public const string GlookoEmail = "CONNECT_GLOOKO_EMAIL";
    public const string GlookoPassword = "CONNECT_GLOOKO_PASSWORD";
    public const string GlookoTimezoneOffset = "CONNECT_GLOOKO_TIMEZONE_OFFSET";
    public const string GlookoServer = "CONNECT_GLOOKO_SERVER";

    // MiniMed CareLink Configuration
    public const string CarelinkUsername = "CONNECT_CARELINK_USERNAME";
    public const string CarelinkPassword = "CONNECT_CARELINK_PASSWORD";
    public const string CarelinkRegion = "CONNECT_CARELINK_REGION";
    public const string CarelinkCountryCode = "CONNECT_CARELINK_COUNTRY";
    public const string CarelinkPatientUsername = "CONNECT_CARELINK_PATIENT_USERNAME";

    // Dexcom Share Configuration
    public const string DexcomUsername = "CONNECT_SHARE_ACCOUNT_NAME";
    public const string DexcomPassword = "CONNECT_SHARE_PASSWORD";
    public const string DexcomRegion = "CONNECT_SHARE_REGION";
    public const string DexcomServer = "CONNECT_SHARE_SERVER";

    // LibreLinkUp Configuration
    public const string LibreUsername = "CONNECT_LINK_UP_USERNAME";
    public const string LibrePassword = "CONNECT_LINK_UP_PASSWORD";
    public const string LibreRegion = "CONNECT_LINK_UP_REGION";
    public const string LibreServer = "CONNECT_LINK_UP_SERVER";
    public const string LibrePatientId = "CONNECT_LINK_UP_PATIENT_ID";

    public const string MyLifeUsername = "CONNECT_MYLIFE_USERNAME";
    public const string MyLifePassword = "CONNECT_MYLIFE_PASSWORD";
    public const string MyLifePatientId = "CONNECT_MYLIFE_PATIENT_ID";
    public const string MyLifeServiceUrl = "CONNECT_MYLIFE_SERVICE_URL";
    public const string MyLifeEnableGlucoseSync = "CONNECT_MYLIFE_ENABLE_GLUCOSE_SYNC";
    public const string MyLifeEnableManualBgSync = "CONNECT_MYLIFE_ENABLE_MANUAL_BG_SYNC";
    public const string MyLifeEnableMealCarbConsolidation = "CONNECT_MYLIFE_ENABLE_MEAL_CARB_CONSOLIDATION";
    public const string MyLifeEnableTempBasalConsolidation = "CONNECT_MYLIFE_ENABLE_TEMP_BASAL_CONSOLIDATION";
    public const string MyLifeTempBasalConsolidationWindowMinutes = "CONNECT_MYLIFE_TEMP_BASAL_CONSOLIDATION_WINDOW_MINUTES";
    public const string MyLifeAppPlatform = "CONNECT_MYLIFE_APP_PLATFORM";
    public const string MyLifeAppVersion = "CONNECT_MYLIFE_APP_VERSION";
    public const string MyLifeSyncMonths = "CONNECT_MYLIFE_SYNC_MONTHS";

    // Nightscout Source Configuration
    public const string SourceEndpoint = "CONNECT_SOURCE_ENDPOINT";
    public const string SourceApiSecret = "CONNECT_SOURCE_API_SECRET";

    // Proxy Configuration
    public const string NightscoutTargetUrl = "NIGHTSCOUT_TARGET_URL";

    // Loop Configuration
    public const string LoopApnsKey = "LOOP_APNS_KEY";
    public const string LoopApnsKeyId = "LOOP_APNS_KEY_ID";
    public const string LoopDeveloperTeamId = "LOOP_DEVELOPER_TEAM_ID";
    public const string LoopPushServerEnvironment = "LOOP_PUSH_SERVER_ENVIRONMENT";
}

/// <summary>
/// Default values used by connectors
/// </summary>
public static class ConnectorDefaults
{
    // Core defaults
    public const string MongoCollection = "entries";
    public const string MongoProfileCollection = "profile";
    public const string Hostname = "0.0.0.0";
    public const string DisplayUnits = "mmol";
    public const int TimeFormat = 24;
    public const string Language = "en";
    public const string NodeEnvironment = "development";
    public const int AuthFailDelay = 50;

    // Glooko defaults
    public const string GlookoServer = "eu.api.glooko.com";
    public const int GlookoTimezoneOffset = 0;

    // CareLink defaults
    public const string CarelinkRegion = "us";

    // Dexcom defaults
    public const string DexcomRegion = "us";

    // LibreLinkUp defaults
    public const string LibreRegion = "EU";

    // Loop defaults
    public const string LoopPushServerEnvironment = "development";
}

/// <summary>
/// Timeout and delay constants for connector services
/// </summary>
public static class ConnectorTimeouts
{
    /// <summary>
    /// HTTP client timeouts
    /// </summary>
    public static class Http
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Retry delays for exponential backoff
    /// </summary>
    public static class Retry
    {
        public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan SecondRetry = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan ThirdRetry = TimeSpan.FromSeconds(30);

        // For rate limiting delays
        public static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan AuthenticationDelay = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Session and token expiration
    /// </summary>
    public static class Session
    {
        public static readonly TimeSpan DefaultExpiration = TimeSpan.FromHours(8);
        public static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan DexcomSessionDuration = TimeSpan.FromHours(3);
        public static readonly TimeSpan LibreTokenDuration = TimeSpan.FromHours(2);
        public static readonly TimeSpan CarelinkSessionDuration = TimeSpan.FromHours(6);
        public static readonly TimeSpan GlookoTokenDuration = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Health check and monitoring intervals
    /// </summary>
    public static class Health
    {
        public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan ResetInterval = TimeSpan.FromHours(1);
        public static readonly TimeSpan AlertThreshold = TimeSpan.FromMinutes(15);
    }

    /// <summary>
    /// WebSocket specific timeouts
    /// </summary>
    public static class WebSocket
    {
        public const int ReconnectAttempts = 10;
        public const int ReconnectDelay = 5000; // milliseconds
        public const int MaxReconnectDelay = 30000; // milliseconds
        public const int GracefulShutdownTimeout = 10000; // milliseconds
    }
}

/// <summary>
/// HTTP status code handling constants
/// </summary>
public static class ConnectorHttpStatus
{
    /// <summary>
    /// HTTP status codes that should trigger a retry
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
    /// HTTP status codes that indicate authentication issues
    /// </summary>
    public static readonly int[] AuthenticationStatusCodes =
    {
        401, // Unauthorized
        403, // Forbidden
    };

    /// <summary>
    /// HTTP status codes that should not be retried
    /// </summary>
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
