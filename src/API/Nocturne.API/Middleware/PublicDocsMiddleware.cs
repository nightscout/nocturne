using Nocturne.API.Services.Docs;

namespace Nocturne.API.Middleware;

/// <summary>
/// Serves the documentation paths — the OpenAPI specs and the Scalar reference — ahead of tenant
/// resolution and authentication, and gates them on the resolved tenant's opt-in.
/// </summary>
/// <remarks>
/// Running before <see cref="Multitenancy.TenantResolutionMiddleware"/> is what lets the reference
/// render on a fresh install: the apex of an instance with no tenants answers 503 setup_required,
/// and an instance with several answers 404. Neither is a useful first page for an operator who is
/// standing the instance up, so the request jumps straight to the endpoint instead — which also
/// skips everything downstream, leaving what a tenant's host exposes to
/// <see cref="ScalarAuthProvider"/> alone.
/// </remarks>
public sealed class PublicDocsMiddleware
{
    private readonly RequestDelegate _next;

    public PublicDocsMiddleware(RequestDelegate next) => _next = next;

    /// <summary>
    /// The OpenAPI specs and the Scalar UI plus its wwwroot assets. Also picks out the branch
    /// that gets the any-origin CORS policy in <c>Program</c>.
    /// </summary>
    public static bool IsPublicDocsPath(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        return path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context, ScalarAuthProvider docs)
    {
        // A docs path with no endpoint is not one of ours (MapOpenApi / MapScalarApiReference).
        if (!IsPublicDocsPath(context) || context.GetEndpoint()?.RequestDelegate is not { } handle)
        {
            await _next(context);
            return;
        }

        // Scalar's options delegate is synchronous, so the per-tenant auth context
        // (OAuth client, demo bearer token) is resolved here and stashed on Items.
        if (!await docs.TryPrepareAsync(context))
        {
            // 404 rather than 403: a tenant that has not opted in has no documentation surface,
            // as an unknown slug has none.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await handle(context);
    }
}
