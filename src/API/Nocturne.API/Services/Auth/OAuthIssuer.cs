using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// The OAuth authorization server's issuer identifier (RFC 8414): the deployment's public origin
/// when a base domain is configured, else the requesting origin. The authorization server metadata
/// reports it and every authorization response carries it as <c>iss</c> (RFC 9207), and a client
/// compares the two. See <see cref="Controllers.WellKnownController"/> for why the OpenID document
/// reports a different issuer.
/// </summary>
internal static class OAuthIssuer
{
    public static string Of(HttpRequest request) =>
        request.HttpContext.RequestServices?.GetService<IOptions<BaseDomainOptions>>()?.Value.PublicOrigin
        ?? $"{request.PublicScheme()}://{request.Host}";
}
