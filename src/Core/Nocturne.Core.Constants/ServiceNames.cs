namespace Nocturne.Core.Constants;

/// <summary>
/// Shared service, parameter, and configuration key names used across the Nocturne
/// solution. Only constants that are actually referenced live here -- when
/// adding a new entry, make sure something consumes it.
/// </summary>
/// <seealso cref="ApplicationConstants"/>
/// <seealso cref="DataSources"/>
public static class ServiceNames
{
    // ========================================================================
    // Core Aspire resource names
    // ========================================================================

    /// <summary>
    /// Aspire resource name for the Nocturne ASP.NET Core API project.
    /// </summary>
    public const string NocturneApi = "nocturne-api";

    /// <summary>
    /// Aspire resource name for the Nocturne SvelteKit web frontend.
    /// </summary>
    public const string NocturneWeb = "nocturne-web";

    // ========================================================================
    // Database
    // ========================================================================

    /// <summary>
    /// Aspire resource name for the PostgreSQL database server.
    /// </summary>
    /// <seealso cref="ApplicationConstants.Database"/>
    public const string PostgreSql = "nocturne-postgres";

    // ========================================================================
    // SignalR Hubs
    // ========================================================================

    /// <summary>
    /// Hub name for real-time CGM data and treatment updates via SignalR.
    /// </summary>
    /// <seealso cref="WebSocketEvents"/>
    public const string DataHub = "data";

    /// <summary>
    /// Hub name for alarm and notification delivery via SignalR.
    /// </summary>
    /// <seealso cref="WebSocketEvents"/>
    public const string NotificationHub = "notification";

    // ========================================================================
    // Connector resource names
    // ========================================================================

    /// <summary>
    /// Aspire resource name for the Dexcom Share connector service.
    /// </summary>
    /// <seealso cref="DataSources.DexcomConnector"/>
    public const string DexcomConnector = "dexcom-connector";

    /// <summary>
    /// Aspire resource name for the FreeStyle Libre (LibreLinkUp) connector service.
    /// </summary>
    /// <seealso cref="DataSources.LibreConnector"/>
    public const string LibreConnector = "freestyle-connector";

    /// <summary>
    /// Aspire resource name for the Glooko connector service.
    /// </summary>
    /// <seealso cref="DataSources.GlookoConnector"/>
    public const string GlookoConnector = "glooko-connector";

    /// <summary>
    /// Aspire resource name for the upstream Nightscout bridging connector service.
    /// </summary>
    /// <seealso cref="DataSources.NightscoutConnector"/>
    public const string NightscoutConnector = "nightscout-connector";

    /// <summary>
    /// Aspire resource name for the MyFitnessPal connector service.
    /// </summary>
    /// <seealso cref="DataSources.MyFitnessPalConnector"/>
    public const string MyFitnessPalConnector = "myfitnesspal-connector";

    /// <summary>
    /// Aspire resource name for the Tidepool connector service.
    /// </summary>
    /// <seealso cref="DataSources.TidepoolConnector"/>
    public const string TidepoolConnector = "tidepool-connector";

    /// <summary>
    /// Aspire resource name for the Home Assistant connector service.
    /// </summary>
    /// <seealso cref="DataSources.HomeAssistantConnector"/>
    public const string HomeAssistantConnector = "home-assistant-connector";

    /// <summary>
    /// Aspire resource name for the Eversense Now connector service.
    /// </summary>
    /// <seealso cref="DataSources.EversenseConnector"/>
    public const string EversenseConnector = "eversense-connector";

    /// <summary>
    /// Aspire resource name for the NocturneRemote connector service.
    /// </summary>
    public const string NocturneRemoteConnector = "nocturne-remote-connector";

    /// <summary>
    /// Aspire parameter names resolved by the AppHost via <c>AddParameter</c> and
    /// by services reading <c>Parameters:&lt;name&gt;</c> from configuration.
    /// </summary>
    public static class Parameters
    {
        /// <summary>
        /// Parameter name for the PostgreSQL superuser username.
        /// </summary>
        public const string PostgresUsername = "postgres-username";

        /// <summary>
        /// Parameter name for the PostgreSQL superuser password.
        /// </summary>
        public const string PostgresPassword = "postgres-password";

        /// <summary>
        /// Parameter name for the <c>nocturne_migrator</c> role password used by EF Core migrations.
        /// </summary>
        public const string PostgresMigratorPassword = "postgres-migrator-password";

        /// <summary>
        /// Parameter name for the <c>nocturne_app</c> role password used by the runtime DbContext pool.
        /// </summary>
        public const string PostgresAppPassword = "postgres-app-password";

        /// <summary>
        /// Parameter name for the <c>nocturne_web</c> role password used by the SvelteKit bot framework.
        /// </summary>
        public const string PostgresWebPassword = "postgres-web-password";

        /// <summary>
        /// Parameter name for the shared instance key used to authenticate between API and web services.
        /// </summary>
        /// <seealso cref="ConfigKeys.InstanceKey"/>
        public const string InstanceKey = "instance-key";
    }

    /// <summary>
    /// Docker volume names used by the Aspire AppHost for persistent storage.
    /// </summary>
    public static class Volumes
    {
        /// <summary>
        /// Named volume for PostgreSQL data persistence across container restarts.
        /// </summary>
        public const string PostgresData = "nocturne-postgres-data";
    }

    /// <summary>
    /// Configuration keys consumed by services at runtime. Add only when the value is
    /// referenced from at least one place.
    /// </summary>
    public static class ConfigKeys
    {
        /// <summary>
        /// Shared secret between the API and web service for instance-level authentication.
        /// </summary>
        /// <seealso cref="Parameters.InstanceKey"/>
        public const string InstanceKey = "INSTANCE_KEY";

        /// <summary>
        /// Public base URL of the deployment, used for OIDC redirects, invite links,
        /// Pushover callbacks, and other external-facing URLs.
        /// </summary>
        public const string BaseUrl = "BaseUrl";

        /// <summary>
        /// Site name reported by the legacy Nightscout <c>/status</c> endpoint.
        /// </summary>
        public const string NightscoutSiteName = "Nightscout:SiteName";

        /// <summary>
        /// Glucose display units configuration key ("mg/dl" or "mmol").
        /// </summary>
        public const string DisplayUnits = "Display:Units";

        /// <summary>
        /// Whether to show raw (unfiltered) blood glucose values.
        /// </summary>
        public const string DisplayShowRawBG = "Display:ShowRawBG";

        /// <summary>
        /// Custom title displayed in the web client header.
        /// </summary>
        public const string DisplayCustomTitle = "Display:CustomTitle";

        /// <summary>
        /// Visual theme name for the web client.
        /// </summary>
        public const string DisplayTheme = "Display:Theme";

        /// <summary>
        /// Comma-separated list of plugins to display in the web client.
        /// </summary>
        /// <seealso cref="ApplicationConstants.Plugins"/>
        public const string DisplayShowPlugins = "Display:ShowPlugins";

        /// <summary>
        /// Whether to show glucose forecast/prediction lines.
        /// </summary>
        public const string DisplayShowForecast = "Display:ShowForecast";

        /// <summary>
        /// Y-axis scaling mode for the glucose chart (e.g., "linear", "log").
        /// </summary>
        public const string DisplayScaleY = "Display:ScaleY";

        /// <summary>
        /// UI language code (ISO 639-1) for localization.
        /// </summary>
        public const string LocalizationLanguage = "Localization:Language";

        /// <summary>
        /// Comma-separated list of feature flags to enable.
        /// </summary>
        public const string FeaturesEnable = "Features:Enable";

        /// <summary>
        /// Pushover API token from configuration file (appsettings section).
        /// </summary>
        public const string PushoverApiToken = "Pushover:ApiToken";

        /// <summary>
        /// Pushover user/group key from configuration file (appsettings section).
        /// </summary>
        public const string PushoverUserKey = "Pushover:UserKey";

        /// <summary>
        /// Environment variable fallback for the Pushover API token.
        /// </summary>
        public const string PushoverApiTokenEnv = "PUSHOVER_API_TOKEN";

        /// <summary>
        /// Environment variable fallback for the Pushover user/group key.
        /// </summary>
        public const string PushoverUserKeyEnv = "PUSHOVER_USER_KEY";
    }

    /// <summary>
    /// Default values for non-secret parameters when the user has not
    /// supplied one in configuration.
    /// </summary>
    public static class Defaults
    {
        /// <summary>
        /// Default PostgreSQL database name.
        /// </summary>
        public const string PostgresDatabase = "nocturne";
    }
}
