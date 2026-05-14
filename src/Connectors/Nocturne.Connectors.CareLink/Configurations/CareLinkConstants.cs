namespace Nocturne.Connectors.CareLink.Configurations;

public static class CareLinkConstants
{
    public static class Servers
    {
        public const string Eu = "carelink.minimed.eu";
        public const string Us = "carelink.minimed.com";
    }

    public static class Discovery
    {
        public const string EuBaseUrl = "https://clcloud.minimed.eu";
        public const string UsBaseUrl = "https://clcloud.minimed.com";
        public const string DiscoveryPath = "/connect/carepartner/v13/discover/android/3.6";
        public const string DefaultSSOConfigKey = "Auth0SSOConfiguration";
    }

    public static class Endpoints
    {
        public const string UsersMe = "/patient/users/me";
        public const string MonitorData = "/patient/monitor/data";
        public const string ConnectData = "/patient/connect/data";
        public const string CountrySettings = "/patient/countries/settings";
        public const string LinkedPatients = "/patient/m2m/links/patients";
    }

    public static class UserAgents
    {
        public const string MobileApp = "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";
        public const string Desktop = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    }

    public static class CarePartnerRoles
    {
        public const string CarePartner = "CARE_PARTNER";
        public const string CarePartnerOus = "CARE_PARTNER_OUS";
    }

    public const int StaleDataThresholdMinutes = 20;
    public const double MmolToMgdlFactor = 18.0182;

    /// <summary>
    /// BLE endpoint versions to try in order. Cached after first success.
    /// </summary>
    public static readonly string[] BleEndpointVersions = ["/v6/", "/v5/", "/v11/"];
}
