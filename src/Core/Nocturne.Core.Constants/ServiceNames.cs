namespace Nocturne.Core.Constants;

/// <summary>
/// Defines all constants used throughout the Nocturne application.
/// This centralizes all string constants to ensure consistency across the codebase.
/// </summary>
public static class ServiceNames
{
    // Core Services
    public const string NocturneApi = "nocturne-api";
    public const string NocturneWeb = "nocturne-web";
    public const string WebSocketBridge = "websocket-bridge";
    public const string CompatibilityProxy = "compatibility-proxy";
    public const string DemoService = "demo-service";

    // Data Services
    public const string MongoDb = "nightscout-mongodb";
    public const string PostgreSql = "nocturne-postgres";

    // Aspire Services
    public const string AspireHost = "aspire-host";
    public const string AspireDashboard = "aspire-dashboard";

    // SignalR Hubs
    public const string DataHub = "data";
    public const string NotificationHub = "notification";

    // Connector Services
    public const string DexcomConnector = "dexcom-connector";
    public const string LibreConnector = "freestyle-connector";
    public const string GlookoConnector = "glooko-connector";
    public const string MiniMedConnector = "carelink-connector";
    public const string NightscoutConnector = "nightscout-connector";
    public const string MyFitnessPalConnector = "myfitnesspal-connector";
    public const string TidepoolConnector = "tidepool-connector";
    public const string TConnectSyncConnector = "tconnectsync-connector";
    public const string MyLifeConnector = "mylife-connector";

    // Docker Container Names
    public static class Docker
    {
        public const string MongoDb = "nocturne-mongodb";
        public const string Api = "nocturne-api";
        public const string Web = "nocturne-web";
        public const string WebSocketBridge = "nocturne-websocket-bridge";
    }

    // Kubernetes Service Names
    public static class Kubernetes
    {
        public const string MongoDb = "nocturne-mongodb-service";
        public const string Api = "nocturne-api-service";
        public const string Web = "nocturne-web-service";
        public const string WebSocketBridge = "nocturne-websocket-bridge-service";
    }

    /// <summary>
    /// Aspire parameter names used in configuration
    /// </summary>
    public static class Parameters
    {
        // Database parameters
        public const string PostgresUsername = "postgres-username";
        public const string PostgresPassword = "postgres-password";
        public const string PostgresDbName = "postgres-db-name";

        // Compatibility proxy parameters
        public const string CompatibilityProxyTargetUrl = "compatibility-proxy-target-url";

        // OpenTelemetry parameters
        public const string OtlpEndpoint = "otlp-endpoint";

        // Notification parameters
        public const string PushoverApiToken = "pushover-api-token";
        public const string PushoverUserKey = "pushover-user-key";

        // Authentication parameters
        public const string ApiSecret = "api-secret";
    }

    /// <summary>
    /// Docker volume names
    /// </summary>
    public static class Volumes
    {
        public const string PostgresData = "nocturne-postgres-data";
    }

    /// <summary>
    /// Environment variable prefixes for connectors
    /// </summary>
    public static class ConnectorEnvironment
    {
        public const string DexcomPrefix = "Parameters__Connectors__Dexcom__";
        public const string GlookoPrefix = "Parameters__Connectors__Glooko__";
        public const string FreeStylePrefix = "Parameters__Connectors__FreeStyle__";
        public const string MiniMedPrefix = "Parameters__Connectors__MiniMed__";
        public const string NightscoutPrefix = "Parameters__Connectors__Nightscout__";
        public const string MyFitnessPalPrefix = "Parameters__Connectors__MyFitnessPal__";
        public const string TidepoolPrefix = "Parameters__Connectors__Tidepool__";
        public const string TConnectSyncPrefix = "Parameters__Connectors__TConnectSync__";
        public const string MyLifePrefix = "Parameters__Connectors__MyLife__";
    }

    /// <summary>
    /// Configuration keys for various settings
    /// </summary>
    public static class ConfigKeys
    {
        // Environment variables
        public const string NocturneInteractive = "NOCTURNE_INTERACTIVE";
        public const string ApiSecret = "API_SECRET";
        public const string JwtSecret = "JWT_SECRET";
        public const string NightscoutTargetUrl = "NIGHTSCOUT_TARGET_URL";
        public const string PushoverApiTokenEnv = "PUSHOVER_API_TOKEN";
        public const string PushoverUserKeyEnv = "PUSHOVER_USER_KEY";
        public const string CustomConnStrMongo = "CUSTOMCONNSTR_mongo";

        // Command line arguments
        public const string InteractiveArg = "--interactive";
        public const string InteractiveShort = "-i";

        // Configuration check keys
        public const string NotificationsConfigured = "notifications-configured";
        public const string ConnectorsConfigured = "connectors-configured";
        public const string TelemetryConfigured = "telemetry-configured";

        // Configuration sections
        public const string PostgreSqlSection = "PostgreSql";
        public const string CompatibilityProxySection = "CompatibilityProxy";
        public const string ConnectorSettingsSection = "ConnectorSettings";
        public const string NotificationSettingsSection = "NotificationSettings";
        public const string NightscoutSettingsSection = "NightscoutSettings";
        public const string OpenTelemetrySection = "OpenTelemetry";

        // Nightscout configuration keys
        public const string NightscoutSiteName = "Nightscout:SiteName";
        public const string NightscoutUrl = "Nightscout:Url";

        // Display configuration keys
        public const string DisplayUnits = "Display:Units";
        public const string DisplayShowRawBG = "Display:ShowRawBG";
        public const string DisplayCustomTitle = "Display:CustomTitle";
        public const string DisplayTheme = "Display:Theme";
        public const string DisplayShowPlugins = "Display:ShowPlugins";
        public const string DisplayShowForecast = "Display:ShowForecast";
        public const string DisplayScaleY = "Display:ScaleY";

        // Localization configuration keys
        public const string LocalizationLanguage = "Localization:Language";

        // Features configuration keys
        public const string FeaturesEnable = "Features:Enable";

        // Pushover configuration keys
        public const string PushoverApiToken = "Pushover:ApiToken";
        public const string PushoverUserKey = "Pushover:UserKey";

        // Base URL configuration
        public const string BaseUrl = "BaseUrl";

        // PostgreSQL connection string keys
        public const string PostgreSqlConnectionString = "PostgreSql:ConnectionString";

        // MongoDB connection string keys
        public const string MongoConnectionString = "Mongo:ConnectionString";

        // MyFitnessPal configuration keys
        public const string MyFitnessPalUsername = "MyFitnessPal:Username";
        public const string MyFitnessPalNightscoutUrl = "MyFitnessPal:NightscoutUrl";

        // Configuration values
        public const string TrueValue = "true";
        public const string FalseValue = "false";
        public const string EnabledKey = "Enabled";
    }

    /// <summary>
    /// Default configuration values
    /// </summary>
    public static class Defaults
    {
        // Database defaults
        public const string PostgresUser = "nocturne_user";
        public const string PostgresPassword = "nocturne_password";
        public const string PostgresDatabase = "nocturne";

        // OpenTelemetry defaults
        public const string DefaultOtlpEndpoint = "http://localhost:4317";

        // Authentication defaults
        public const string DefaultApiSecret = "nocturne_default_api_secret";
    }
}
