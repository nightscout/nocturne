using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// The OAuth authorization server's issuer identifier (RFC 8414), which is also the origin of its
/// endpoints: the requesting host's origin (after forwarded headers), or the configured
/// <see cref="BaseDomainOptions.PublicOrigin"/> only when the request is to the apex itself. A
/// client registers with, is authorized by, and calls MCP on one tenant host, so a tenant host is
/// its own authorization server. The metadata reports it and every authorization response carries
/// it as <c>iss</c> (RFC 9207). See <see cref="Controllers.WellKnownController"/> for why the OpenID
/// document reports a different issuer.
/// </summary>
internal static class OAuthIssuer
{
    public static string Of(HttpRequest request)
    {
        var requestOrigin = $"{request.PublicScheme()}://{request.Host}";
        var baseDomain = request.HttpContext.RequestServices?.GetService<IOptions<BaseDomainOptions>>()?.Value;

        return baseDomain?.PublicOrigin is { } publicOrigin
            && string.Equals(request.Host.Value, baseDomain.BaseDomain.Trim(), StringComparison.OrdinalIgnoreCase)
            ? publicOrigin
            : requestOrigin;
    }
}
