using Nocturne.API.Authorization;
using Nocturne.API.Extensions;

namespace Nocturne.API.Middleware;

/// <summary>
/// Resolves the request's client address once, so every recorder reads the one
/// <see cref="ClientAddressResolver"/> verdict through
/// <see cref="HttpContextExtensions.GetClientAddress"/>. Runs after the forwarded headers are applied.
/// </summary>
public class ClientAddressMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ClientAddressResolver _resolver;

    public ClientAddressMiddleware(RequestDelegate next, ClientAddressResolver resolver)
    {
        _next = next;
        _resolver = resolver;
    }

    public Task InvokeAsync(HttpContext httpContext)
    {
        httpContext.SetClientAddress(_resolver.Resolve(httpContext));
        return _next(httpContext);
    }
}
