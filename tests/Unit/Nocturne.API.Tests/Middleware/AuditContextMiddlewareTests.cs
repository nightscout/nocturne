using Microsoft.AspNetCore.Http;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.TestDoubles;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// The audit context's endpoint is persisted on every mutation audit row, so a credential carried
/// as a path segment must not reach it.
/// </summary>
[Trait("Category", "Unit")]
public class AuditContextMiddlewareTests
{
    [Fact]
    public async Task CredentialRoute_EndpointCarriesTheRedactedPath()
    {
        var auditContext = new AuditContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/v2/authorization/request/noc_credentialthatmustnotbestored";

        await new AuditContextMiddleware(_ => Task.CompletedTask)
            .InvokeAsync(httpContext, auditContext, CredentialRouteRedactor.Create());

        auditContext.Endpoint.Should().Be($"GET {CredentialRouteRedactor.TokenExchangePath}");
    }

    [Fact]
    public async Task OtherRoute_EndpointCarriesThePath()
    {
        var auditContext = new AuditContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/v4/alert-invites";

        await new AuditContextMiddleware(_ => Task.CompletedTask)
            .InvokeAsync(httpContext, auditContext, CredentialRouteRedactor.Create());

        auditContext.Endpoint.Should().Be("POST /api/v4/alert-invites");
    }
}
