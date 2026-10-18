namespace Nocturne.Connectors.Eversense.Configurations;

public static class EversenseConstants
{
    public const string ClientId = "eversenseMMAiOS";
    public const string ClientSecret = "vYL4yrvM_E\"K";

    public static class Servers
    {
        public const string UsAuth = "https://usiamapi.eversensedms.com";
        public const string UsData = "https://usapialpha.eversensedms.com";
        public const string OusAuth = "https://ousiamapialpha.eversensedms.com";
        public const string OusData = "https://ousalphaapiservices.eversensedms.com";
    }

    public static class Endpoints
    {
        public const string Token = "/connect/token";
        public const string GetFollowingPatientList = "/api/care/GetFollowingPatientList";
        public const string GetFollowingUserSensorGlucose = "/api/care/GetFollowingUserSensorGlucose";
    }

    /// <summary>
    ///     <c>EventTypeID</c> of a sensor glucose row in the care API's user-event feeds. The same
    ///     feeds carry blood glucose, calibrations, insulin, meals, exercise, health events and alerts
    ///     under other ids.
    /// </summary>
    public const int SensorGlucoseEventType = 1;

    /// <summary>
    ///     <c>error_code</c> of a token refusal. The Eversense apps read a refused password as
    ///     400 <c>invalid_grant</c> with code 6008 and
    ///     <c>error_description</c> "invalid credentials.RemainingAttempt:N"; after three refusals
    ///     the account is locked for 30 minutes and the code is 5005. A lockout is not worth
    ///     retrying whatever its status: no attempt can succeed until it expires.
    /// </summary>
    public const int LockedOutErrorCode = 5005;

    /// <summary>
    ///     The care API's history window as the Eversense NOW app asks for it: GMT, no zone suffix.
    /// </summary>
    public const string HistoryDateFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public static string AuthBaseUrl(string server) => server.ToUpperInvariant() switch
    {
        "US" => Servers.UsAuth,
        "OUS" => Servers.OusAuth,
        _ => throw new ArgumentOutOfRangeException(nameof(server), server, "Unsupported Eversense server region")
    };

    public static string DataBaseUrl(string server) => server.ToUpperInvariant() switch
    {
        "US" => Servers.UsData,
        "OUS" => Servers.OusData,
        _ => throw new ArgumentOutOfRangeException(nameof(server), server, "Unsupported Eversense server region")
    };
}
