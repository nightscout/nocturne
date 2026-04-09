namespace Nocturne.Core.Constants;

/// <summary>
/// Shared service / parameter / config-key names used across the Nocturne
/// solution. Only constants that are actually referenced live here — when
/// adding a new entry, make sure something consumes it.
/// </summary>
public static class ServiceNames
{
    // Core Aspire resource names
    public const string NocturneApi = "nocturne-api";
    public const string NocturneWeb = "nocturne-web";

    // Database
    public const string PostgreSql = "nocturne-postgres";

    // SignalR Hubs
    public const string DataHub = "data";
    public const string NotificationHub = "notification";

    // Connector resource names (referenced from connector configurations)
    public const string DexcomConnector = "dexcom-connector";
    public const string LibreConnector = "freestyle-connector";
    public const string GlookoConnector = "glooko-connector";
    public const string NightscoutConnector = "nightscout-connector";
    public const string MyFitnessPalConnector = "myfitnesspal-connector";
    public const string TidepoolConnector = "tidepool-connector";
    public const string HomeAssistantConnector = "home-assistant-connector";

    /// <summary>
    /// Aspire parameter names. Resolved by the AppHost via AddParameter and
    /// by services reading "Parameters:&lt;name&gt;" from configuration.
    /// </summary>
    public static class Parameters
    {
        public const string PostgresUsername = "postgres-username";
        public const string PostgresPassword = "postgres-password";
        public const string PostgresMigratorPassword = "postgres-migrator-password";
        public const string PostgresAppPassword = "postgres-app-password";
        public const string PostgresWebPassword = "postgres-web-password";
        public const string InstanceKey = "instance-key";
    }

    /// <summary>
    /// Docker volume names.
    /// </summary>
    public static class Volumes
    {
        public const string PostgresData = "nocturne-postgres-data";
    }

    /// <summary>
    /// Configuration keys consumed by services. Add only when the value is
    /// referenced from at least one place.
    /// </summary>
    public static class ConfigKeys
    {
        // Shared between API and web for instance authentication
        public const string InstanceKey = "INSTANCE_KEY";

        // Public base URL of the deployment (used for OIDC redirects, invite
        // links, Pushover callbacks, etc.)
        public const string BaseUrl = "BaseUrl";

        // Nightscout legacy /status endpoint compatibility
        public const string NightscoutSiteName = "Nightscout:SiteName";
        public const string DisplayUnits = "Display:Units";
        public const string DisplayShowRawBG = "Display:ShowRawBG";
        public const string DisplayCustomTitle = "Display:CustomTitle";
        public const string DisplayTheme = "Display:Theme";
        public const string DisplayShowPlugins = "Display:ShowPlugins";
        public const string DisplayShowForecast = "Display:ShowForecast";
        public const string DisplayScaleY = "Display:ScaleY";
        public const string LocalizationLanguage = "Localization:Language";
        public const string FeaturesEnable = "Features:Enable";

        // Pushover (config-file values + env-var fallbacks)
        public const string PushoverApiToken = "Pushover:ApiToken";
        public const string PushoverUserKey = "Pushover:UserKey";
        public const string PushoverApiTokenEnv = "PUSHOVER_API_TOKEN";
        public const string PushoverUserKeyEnv = "PUSHOVER_USER_KEY";
    }

    /// <summary>
    /// Default values for non-secret parameters when the user has not
    /// supplied one in configuration.
    /// </summary>
    public static class Defaults
    {
        public const string PostgresDatabase = "nocturne";
    }
}
