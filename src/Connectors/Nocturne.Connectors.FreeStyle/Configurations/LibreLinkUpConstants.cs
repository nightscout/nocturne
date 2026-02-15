namespace Nocturne.Connectors.FreeStyle.Configurations;

/// <summary>
///     Constants specific to LibreLinkUp/FreeStyle connector
/// </summary>
public static class LibreLinkUpConstants
{
    /// <summary>
    ///     Known LibreLinkUp regional endpoints
    /// </summary>
    public static class Endpoints
    {
        public const string Ae = "api-ae.libreview.io";
        public const string Ap = "api-ap.libreview.io";
        public const string Au = "api-au.libreview.io";
        public const string Ca = "api-ca.libreview.io";
        public const string De = "api-de.libreview.io";
        public const string Eu = "api-eu.libreview.io";
        public const string Eu2 = "api-eu2.libreview.io";
        public const string Fr = "api-fr.libreview.io";
        public const string Jp = "api-jp.libreview.io";
        public const string Us = "api-us.libreview.io";
    }

    /// <summary>
    ///     API endpoints for LibreLinkUp
    /// </summary>
    public static class ApiPaths
    {
        public const string Login = "/llu/auth/login";
        public const string Connections = "/llu/connections";
        public const string GraphData = "/llu/connections/{0}/graph";
    }

    /// <summary>
    ///     Configuration specific to LibreLinkUp
    /// </summary>
    public static class Configuration
    {
        public const string DefaultRegion = "EU";
        public const string DeviceIdentifier = "libre-connector";
        public const int MaxRetries = 3;
        public const string EntryType = "sgv";
    }
}