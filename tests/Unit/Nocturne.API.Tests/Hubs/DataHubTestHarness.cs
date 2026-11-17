using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// A <see cref="DataHub"/> on an in-memory handshake scope for tenant <see cref="Tenant"/>, reading
/// from the services the caller supplies and holding no grant until the test gives it one.
/// </summary>
internal static class DataHubTestHarness
{
    internal static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid Subject = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    internal static (DataHub Hub, HttpContext HttpContext) CreateHub(
        IEntryService entryService,
        ITreatmentService treatmentService,
        IDeviceStatusService deviceStatusService,
        IHubTokenAuthorizer authorizer)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<NocturneDbContext>(
            options => options.UseInMemoryDatabase($"DataHubTestHarness_{Guid.NewGuid()}"));
        services.AddDataServices();
        services.AddSingleton(entryService);
        services.AddSingleton(treatmentService);
        services.AddSingleton(deviceStatusService);
        var requestServices = services.BuildServiceProvider().CreateScope().ServiceProvider;

        var httpContext = new DefaultHttpContext { RequestServices = requestServices };
        httpContext.Items["TenantContext"] =
            new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false);

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.ConnectionId).Returns("conn-1");
        callerContext.SetupGet(c => c.Features).Returns(features);
        callerContext.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());

        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(c => c.Caller).Returns(Mock.Of<ISingleClientProxy>());

        var hub = new DataHub(NullLogger<DataHub>.Instance, authorizer)
        {
            Context = callerContext.Object,
            Groups = Mock.Of<IGroupManager>(),
            Clients = clients.Object,
        };

        return (hub, httpContext);
    }
}
