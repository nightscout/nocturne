namespace Nocturne.Connectors.Eversense.Configurations;

public static class EversenseConstants
{
    public const string ClientId = "eversenseMMAiOS";
    public const string ClientSecret = "vYL4yrvM_E\"K";

    public static class Servers
    {
        public const string UsAuth = "https://usiamapi.eversensedms.com";
        public const string UsData = "https://usapialpha.eversensedms.com";
    }

    public static class Endpoints
    {
        public const string Token = "/connect/token";
        public const string GetFollowingPatientList = "/api/care/GetFollowingPatientList";
    }
}
