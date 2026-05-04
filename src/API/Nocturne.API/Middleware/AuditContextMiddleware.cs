using Nocturne.API.Extensions;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;

namespace Nocturne.API.Middleware;

/// <summary>
/// Populates the scoped IAuditContext from the current HTTP request.
/// Must run after AuthenticationMiddleware.
/// </summary>
public class AuditContextMiddleware
{
    private readonly RequestDelegate _next;

    public AuditContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext httpContext, IAuditContext auditContext)
    {
        if (auditContext is AuditContext mutableContext)
        {
            var authContext = httpContext.GetAuthContext();

            mutableContext.SubjectId = authContext?.SubjectId;
            mutableContext.SubjectName = authContext?.SubjectName;
            mutableContext.AuthType = authContext?.AuthType.ToString();
            mutableContext.TokenId = authContext?.TokenId;
            mutableContext.IpAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            mutableContext.CorrelationId = httpContext.TraceIdentifier;
            mutableContext.Endpoint = $"{httpContext.Request.Method} {httpContext.Request.Path}";
        }

        await _next(httpContext);
    }
}
