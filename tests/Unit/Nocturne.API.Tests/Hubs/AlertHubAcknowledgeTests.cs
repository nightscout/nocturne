using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// An acknowledgement over the alert hub is recorded against the connection's authenticated
/// principal, as it is over HTTP; the name the caller supplies counts only when the principal
/// names no one.
/// </summary>
[Trait("Category", "Unit")]
public class AlertHubAcknowledgeTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static AlertHub Hub(IAlertAcknowledgementService service, ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(service).BuildServiceProvider(),
        };
        httpContext.SetTenantContext(new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false));
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.Features).Returns(features);
        context.SetupGet(c => c.User).Returns(user);
        return new AlertHub { Context = context.Object };
    }

    [Theory]
    [InlineData(ClaimTypes.Name, "Alice", "Alice")]
    [InlineData(ClaimTypes.NameIdentifier, "0198a7c2-0000-7000-8000-000000000001", "0198a7c2-0000-7000-8000-000000000001")]
    [InlineData(null, null, "Bob")]
    public async Task Acknowledge_records_the_principal_and_falls_back_to_the_supplied_name_only_without_one(
        string? claimType, string? claimValue, string recorded)
    {
        var service = new Mock<IAlertAcknowledgementService>();
        var identity = claimType is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(claimType, claimValue!)], "Nocturne");

        await Hub(service.Object, new ClaimsPrincipal(identity)).Acknowledge("Bob");

        service.Verify(s => s.AcknowledgeAllAsync(Tenant, recorded, It.IsAny<CancellationToken>()), Times.Once);
    }
}
