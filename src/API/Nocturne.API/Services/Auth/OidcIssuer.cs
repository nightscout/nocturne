namespace Nocturne.API.Services.Auth;

/// <summary>
/// Whether a token's <c>iss</c> names a configured provider.
/// </summary>
/// <remarks>
/// Exact equality with the configured issuer URL is too strict for real providers:
/// <list type="bullet">
/// <item>A trailing slash is stored trimmed but may be present on the token.</item>
/// <item>A multi-tenant Microsoft Entra endpoint (<c>/common</c>, <c>/organizations</c>) publishes
/// <c>https://login.microsoftonline.com/{tenantid}/v2.0</c> as its issuer, and each token carries
/// its own tenant there, named again by the token's <c>tid</c> claim.</item>
/// <item>Google issues <c>accounts.google.com</c> with or without the scheme.</item>
/// </list>
/// The audience check is what binds a token to this client; this only decides the issuer.
/// </remarks>
public static class OidcIssuer
{
    private const string TenantIdPlaceholder = "{tenantid}";

    /// <param name="iss">The token's <c>iss</c> claim.</param>
    /// <param name="tid">The token's <c>tid</c> claim, when it has one.</param>
    /// <param name="configuredIssuer">The provider's configured issuer URL.</param>
    /// <param name="discoveryIssuer">The <c>issuer</c> the provider's discovery document publishes.</param>
    public static bool Matches(string? iss, string? tid, string configuredIssuer, string? discoveryIssuer)
    {
        if (string.IsNullOrWhiteSpace(iss))
            return false;

        var actual = Normalize(iss);
        return Candidates(configuredIssuer, discoveryIssuer, tid)
            .Any(candidate => string.Equals(Normalize(candidate), actual, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Candidates(string configuredIssuer, string? discoveryIssuer, string? tid)
    {
        yield return configuredIssuer;

        if (string.IsNullOrWhiteSpace(discoveryIssuer))
            yield break;

        if (!discoveryIssuer.Contains(TenantIdPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            yield return discoveryIssuer;
        }
        else if (!string.IsNullOrWhiteSpace(tid))
        {
            yield return discoveryIssuer.Replace(TenantIdPlaceholder, tid, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Drops a trailing slash and supplies the https scheme to a bare host.</summary>
    private static string Normalize(string issuer)
    {
        var trimmed = issuer.Trim().TrimEnd('/');
        return trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
    }
}
