namespace Nocturne.Connectors.Tandem.Configurations;

/// <summary>
/// Constants for the Tandem Source (t:connect) cloud API and its OpenID Connect login flow.
/// Endpoints, client IDs and the pump-event epoch are ported from the open-source
/// <c>tconnectsync</c> project (https://github.com/jwoglom/tconnectsync), which reverse-engineered
/// the Tandem Source web and mobile APIs for the t:slim X2 and Mobi insulin pumps.
/// </summary>
public static class TandemConstants
{
    /// <summary>SSO landing page, shared between regions; used as the login Referer.</summary>
    public const string LoginPageUrl = "https://sso.tandemdiabetes.com/";

    /// <summary>
    /// The pump-event epoch: seconds between the Unix epoch and 2008-01-01T00:00:00Z.
    /// Raw event timestamps are seconds since this epoch (and carry no timezone).
    /// </summary>
    public const long TandemEpochUnixSeconds = 1199145600L;

    /// <summary>Fixed on-the-wire length of a single encoded pump event record, in bytes.</summary>
    public const int EventLength = 26;

    /// <summary>Number of header bytes (source+id, timestamp, sequence) before the event payload.</summary>
    public const int EventHeaderSize = 10;

    /// <summary>User-Agent presented to the Tandem Source API.</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    /// <summary>
    /// The event IDs the Tandem Source backend itself requests by default. When the connector is not
    /// fetching the full history-log (i.e. device status / "fetch all" is off) it filters to these,
    /// matching <c>tconnectsync</c>'s <c>DEFAULT_EVENT_IDS</c>.
    /// </summary>
    public static readonly int[] DefaultEventIds =
    [
        229, 5, 28, 4, 26, 99, 279, 3, 16, 59, 21, 55, 20, 280, 64, 65, 66, 61, 33, 371, 171, 369,
        460, 172, 370, 461, 372, 399, 256, 213, 406, 394, 212, 404, 214, 405, 447, 313, 60, 14, 6,
        90, 230, 140, 12, 11, 53, 13, 63, 203, 307, 191
    ];

    /// <summary>Per-region Tandem Source endpoints.</summary>
    public sealed record RegionUrls(
        string LoginApiUrl,
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string JwksUrl,
        string Issuer,
        string ClientId,
        string RedirectUri,
        string SourceUrl);

    public static readonly RegionUrls Us = new(
        LoginApiUrl: "https://tdcservices.tandemdiabetes.com/accounts/api/login",
        AuthorizationEndpoint: "https://tdcservices.tandemdiabetes.com/accounts/api/connect/authorize",
        TokenEndpoint: "https://tdcservices.tandemdiabetes.com/accounts/api/connect/token",
        JwksUrl: "https://tdcservices.tandemdiabetes.com/accounts/api/.well-known/openid-configuration/jwks",
        Issuer: "https://tdcservices.tandemdiabetes.com/accounts/api",
        ClientId: "0oa27ho9tpZE9Arjy4h7",
        RedirectUri: "https://sso.tandemdiabetes.com/auth/callback",
        SourceUrl: "https://source.tandemdiabetes.com/");

    public static readonly RegionUrls Eu = new(
        LoginApiUrl: "https://tdcservices.eu.tandemdiabetes.com/accounts/api/login",
        AuthorizationEndpoint: "https://tdcservices.eu.tandemdiabetes.com/accounts/api/connect/authorize",
        TokenEndpoint: "https://tdcservices.eu.tandemdiabetes.com/accounts/api/connect/token",
        JwksUrl: "https://tdcservices.eu.tandemdiabetes.com/accounts/api/.well-known/openid-configuration/jwks",
        Issuer: "https://tdcservices.eu.tandemdiabetes.com/accounts/api",
        ClientId: "1519e414-eeec-492e-8c5e-97bea4815a10",
        RedirectUri: "https://source.eu.tandemdiabetes.com/authorize/callback",
        SourceUrl: "https://source.eu.tandemdiabetes.com/");

    /// <summary>Resolves the region endpoints for a region code ("US" or "EU"); defaults to US.</summary>
    public static RegionUrls ForRegion(string? region) =>
        string.Equals(region, "EU", StringComparison.OrdinalIgnoreCase) ? Eu : Us;
}
