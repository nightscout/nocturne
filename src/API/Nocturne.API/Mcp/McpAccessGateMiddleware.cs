using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp;

/// <summary>Marks the endpoints <see cref="McpAccessGateMiddleware"/> guards.</summary>
internal sealed class McpEndpointMetadata;

/// <summary>
/// Admits a request to <c>/mcp</c> only when it carries an OAuth access token issued for this
/// host's MCP endpoint (its audience, see <c>OAuthGrantEntity.Resource</c>) that holds at least one
/// of <see cref="McpScopes.Offered"/>, on a tenant host that is not a public share.
/// </summary>
/// <remarks>
/// Middleware rather than an authorization policy: the SDK maps <c>/mcp</c> as plain request
/// delegates, which take no endpoint filters, and a policy's challenge goes through the JwtBearer
/// scheme, which cannot name the protected-resource metadata. It runs after
/// <c>AuthenticationMiddleware</c> and <c>MemberScopeMiddleware</c>, so the scopes it reads are
/// already narrowed to the caller's membership of the resolved tenant.
/// </remarks>
internal sealed class McpAccessGateMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<McpEndpointMetadata>() is null)
            return next(context);

        var metadataUrl = ProtectedResourceMetadataUrl(context.Request);

        if (context.IsShareAccess())
            return RefuseAsync(context, StatusCodes.Status403Forbidden, null,
                "A share link cannot be used as an MCP server.");

        if (!context.IsAuthenticated())
            return RefuseAsync(context, StatusCodes.Status401Unauthorized,
                $"Bearer resource_metadata=\"{metadataUrl}\"",
                "An OAuth access token is required.");

        if (context.GetAuthContext() is not { AuthType: AuthType.OAuthAccessToken } auth
            || auth.Audience != ResourceUrl(context.Request))
            return RefuseAsync(context, StatusCodes.Status403Forbidden,
                $"Bearer error=\"invalid_token\", resource_metadata=\"{metadataUrl}\"",
                $"This credential was not issued for {ResourceUrl(context.Request)}. Connect the "
                + "assistant to that address, so that it signs in and gets a token for it.");

        if (!McpScopes.Offered.Any(context.HasScope))
            return RefuseAsync(context, StatusCodes.Status403Forbidden,
                $"Bearer error=\"insufficient_scope\", scope=\"{string.Join(' ', McpScopes.Offered)}\", "
                + $"resource_metadata=\"{metadataUrl}\"",
                "The token holds none of the read scopes this server needs.");

        return next(context);
    }

    internal static string ResourceOrigin(HttpRequest request) =>
        $"{request.PublicScheme()}://{request.Host}";

    internal static string ResourceUrl(HttpRequest request) => $"{ResourceOrigin(request)}/mcp";

    internal static string ProtectedResourceMetadataUrl(HttpRequest request) =>
        $"{ResourceOrigin(request)}/.well-known/oauth-protected-resource";

    private static Task RefuseAsync(HttpContext context, int status, string? challenge, string message)
    {
        context.Response.StatusCode = status;
        if (challenge is not null)
            context.Response.Headers.WWWAuthenticate = challenge;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
