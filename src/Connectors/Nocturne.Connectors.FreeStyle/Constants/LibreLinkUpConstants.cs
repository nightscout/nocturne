namespace Nocturne.Connectors.FreeStyle.Constants;

/// <summary>
/// Constants specific to LibreLinkUp/FreeStyle connector
/// </summary>
public static class LibreLinkUpConstants
{
    /// <summary>
    /// Known LibreLinkUp regional endpoints
    /// </summary>
    public static class Endpoints
    {
        public const string AE = "api-ae.libreview.io";
        public const string AP = "api-ap.libreview.io";
        public const string AU = "api-au.libreview.io";
        public const string CA = "api-ca.libreview.io";
        public const string DE = "api-de.libreview.io";
        public const string EU = "api-eu.libreview.io";
        public const string EU2 = "api-eu2.libreview.io";
        public const string FR = "api-fr.libreview.io";
        public const string JP = "api-jp.libreview.io";
        public const string US = "api-us.libreview.io";
    }

    /// <summary>
    /// API endpoints for LibreLinkUp
    /// </summary>
    public static class ApiPaths
    {
        public const string Login = "/llu/auth/login";
        public const string Connections = "/llu/connections";
        public const string GraphData = "/llu/connections/{0}/graph";
    }

    /// <summary>
    /// HTTP headers for LibreLinkUp API
    /// </summary>
    public static class Headers
    {
        public const string UserAgent = "Nocturne-Connect/1.0";
        public const string Version = "4.7.0";
        public const string Product = "llu.ios";
        public const string Accept = "application/json";
        public const string ContentType = "application/json";
    }

    /// <summary>
    /// Configuration specific to LibreLinkUp
    /// </summary>
    public static class Configuration
    {
        public const string DefaultRegion = "EU";
        public const string DeviceIdentifier = "libre-connector";
        public const int MaxRetries = 3;
        public const int MaxHealthFailures = 5;
        public const string EntryType = "sgv";
    }
}
