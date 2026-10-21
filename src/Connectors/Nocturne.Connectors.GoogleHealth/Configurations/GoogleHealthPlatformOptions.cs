namespace Nocturne.Connectors.GoogleHealth.Configurations;

/// <summary>
/// The instance operator's Google OAuth client. Every tenant connects through it; tenants never
/// supply a client of their own.
/// </summary>
public sealed class GoogleHealthPlatformOptions
{
    public const string ClientIdKey = "GOOGLE_HEALTH_CLIENT_ID";
    public const string ClientSecretKey = "GOOGLE_HEALTH_CLIENT_SECRET";
    public const string RedirectUriKey = "GOOGLE_HEALTH_REDIRECT_URI";
    public const string CallbackPath = "/api/v4/google-health/oauth/callback";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Defaults to <see cref="CallbackPath"/> on the deployment apex when <c>BASE_DOMAIN</c> is
    /// set; without one there is no trustworthy public origin, so it must be given explicitly.
    /// </summary>
    public string RedirectUri { get; set; } = "";

    public bool IsConfigured =>
        IsValidClientId(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        IsValidRedirectUri(RedirectUri);

    public static bool IsValidClientId(string clientId) =>
        !string.IsNullOrWhiteSpace(clientId) &&
        clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal);

    public static bool IsValidRedirectUri(string redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps ||
         uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) &&
        uri.HostNameType == UriHostNameType.Dns &&
        uri.UserInfo == string.Empty &&
        uri.Query == string.Empty &&
        uri.Fragment == string.Empty &&
        uri.AbsolutePath == CallbackPath;
}
