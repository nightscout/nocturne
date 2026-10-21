using Nocturne.API.Extensions;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;

namespace Nocturne.API.Middleware;

/// <summary>
/// Populates the scoped <see cref="IAuditContext"/> from the current HTTP request.
/// Must run after <see cref="AuthenticationMiddleware"/>, which sets the <c>AuthContext</c> it reads.
/// </summary>
public class AuditContextMiddleware
{
    private readonly RequestDelegate _next;

    public AuditContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext httpContext,
        IAuditContext auditContext,
        IRequestPathRedactor pathRedactor)
    {
        if (auditContext is AuditContext mutableContext)
        {
            var authContext = httpContext.GetAuthContext();

            mutableContext.SubjectId = authContext?.SubjectId;
            mutableContext.SubjectName = authContext?.SubjectName;
            mutableContext.AuthType = authContext?.AuthType.ToString();
            mutableContext.TokenId = authContext?.TokenId;
            mutableContext.IpAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            mutableContext.TraceId = httpContext.TraceIdentifier;
            mutableContext.Endpoint =
                $"{httpContext.Request.Method} {pathRedactor.Redact(httpContext.Request.Path.ToString())}";
            httpContext.Items[AuditContext.HttpItemKey] = mutableContext;
        }

        await _next(httpContext);
    }
}
