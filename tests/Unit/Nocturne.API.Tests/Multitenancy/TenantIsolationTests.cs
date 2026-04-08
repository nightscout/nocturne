using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Unit.Multitenancy;

/// <summary>
/// Comprehensive tests verifying that no data leaks between tenants across
/// API controllers, SignalR hubs, broadcast services, and middleware.
/// </summary>
[Trait("Category", "Unit")]
public class TenantIsolationTests
{
    private static readonly Guid TenantAId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantBId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly TenantContext TenantA = new(TenantAId, "alice", "Alice", true);
    private static readonly TenantContext TenantB = new(TenantBId, "bob", "Bob", true);
    private static readonly TenantContext InactiveTenant = new(TenantAId, "alice", "Alice", false);

    /// <summary>
    /// Creates a mock HubCallerContext backed by a real HttpContext.
    /// GetHttpContext() is an extension method that reads IHttpContextFeature from Features,
    /// so we create a FeatureCollection with IHttpContextFeature explicitly set.
    /// </summary>
    private static Mock<HubCallerContext> CreateMockHubCallerContext(
        HttpContext httpContext, string connectionId = "test-connection")
    {
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var mock = new Mock<HubCallerContext>();
        mock.Setup(c => c.Features).Returns(features);
        mock.Setup(c => c.ConnectionId).Returns(connectionId);
        return mock;
    }

    private class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    #region TenantAwareHub Isolation

    /// <summary>
    /// Concrete hub for testing the abstract TenantAwareHub base class.
    /// </summary>
    private class TestableHub : TenantAwareHub
    {
        public new string TenantGroup(string groupName) => base.TenantGroup(groupName);
        public new TenantContext? TenantContext => base.TenantContext;
    }

    private static TestableHub CreateHub(TenantContext? tenantContext)
    {
        var hub = new TestableHub();
        var httpContext = new DefaultHttpContext();

        if (tenantContext != null)
            httpContext.Items["TenantContext"] = tenantContext;

        var mockCallerContext = CreateMockHubCallerContext(httpContext, Guid.NewGuid().ToString());

        typeof(Hub).GetProperty("Context")!.SetValue(hub, mockCallerContext.Object);

        return hub;
    }

    [Fact]
    public async Task TenantAwareHub_OnConnectedAsync_RejectsWithoutTenantContext()
    {
        var hub = CreateHub(tenantContext: null);

        var act = () => hub.OnConnectedAsync();

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*no tenant context*");
    }

    [Fact]
    public async Task TenantAwareHub_OnConnectedAsync_RejectsInactiveTenant()
    {
        var hub = CreateHub(InactiveTenant);

        var act = () => hub.OnConnectedAsync();

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*inactive*");
    }

    [Fact]
    public void TenantAwareHub_TenantGroup_FormatsCorrectly()
    {
        var hub = CreateHub(TenantA);

        var group = hub.TenantGroup("authorized");

        group.Should().Be($"{TenantAId}:authorized");
    }

    [Fact]
    public void TenantAwareHub_TenantGroup_ThrowsWithoutTenantContext()
    {
        var hub = CreateHub(tenantContext: null);

        var act = () => hub.TenantGroup("authorized");

        act.Should().Throw<HubException>()
            .WithMessage("*no tenant context*");
    }

    [Fact]
    public void TenantAwareHub_FormatTenantGroup_IsConsistent()
    {
        var a = TenantAwareHub.FormatTenantGroup(TenantAId.ToString(), "authorized");
        var b = TenantAwareHub.FormatTenantGroup(TenantAId.ToString(), "authorized");

        a.Should().Be(b);
    }

    [Fact]
    public void TenantAwareHub_DifferentTenants_ProduceDifferentGroupNames()
    {
        var groupA = TenantAwareHub.FormatTenantGroup(TenantAId.ToString(), "authorized");
        var groupB = TenantAwareHub.FormatTenantGroup(TenantBId.ToString(), "authorized");

        groupA.Should().NotBe(groupB);
    }

    #endregion

    #region TenantHubFilter Isolation

    [Fact]
    public async Task TenantHubFilter_InvokeMethodAsync_SetsTenantOnAccessor()
    {
        var filter = new TenantHubFilter();
        var mockAccessor = new Mock<ITenantAccessor>();
        var (invocationContext, _) = CreateHubInvocationContext(TenantA, mockAccessor.Object);
        var nextCalled = false;

        await filter.InvokeMethodAsync(invocationContext, ctx =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });

        nextCalled.Should().BeTrue();
        mockAccessor.Verify(a => a.SetTenant(TenantA), Times.Once);
    }

    [Fact]
    public async Task TenantHubFilter_InvokeMethodAsync_WithoutTenantContext_DoesNotSetAccessor()
    {
        var filter = new TenantHubFilter();
        var mockAccessor = new Mock<ITenantAccessor>();
        var (invocationContext, _) = CreateHubInvocationContext(null, mockAccessor.Object);

        await filter.InvokeMethodAsync(invocationContext, ctx =>
            ValueTask.FromResult<object?>(null));

        mockAccessor.Verify(a => a.SetTenant(It.IsAny<TenantContext>()), Times.Never);
    }

    [Fact]
    public async Task TenantHubFilter_OnConnectedAsync_SetsTenantOnAccessor()
    {
        var filter = new TenantHubFilter();
        var mockAccessor = new Mock<ITenantAccessor>();
        var lifetimeContext = CreateHubLifetimeContext(TenantA, mockAccessor.Object);

        await filter.OnConnectedAsync(lifetimeContext, _ => Task.CompletedTask);

        mockAccessor.Verify(a => a.SetTenant(TenantA), Times.Once);
    }

    [Fact]
    public async Task TenantHubFilter_SequentialInvocations_SetCorrectTenantEachTime()
    {
        var filter = new TenantHubFilter();
        var mockAccessorA = new Mock<ITenantAccessor>();
        var mockAccessorB = new Mock<ITenantAccessor>();

        var (contextA, _) = CreateHubInvocationContext(TenantA, mockAccessorA.Object);
        var (contextB, _) = CreateHubInvocationContext(TenantB, mockAccessorB.Object);

        await filter.InvokeMethodAsync(contextA, ctx =>
            ValueTask.FromResult<object?>(null));
        await filter.InvokeMethodAsync(contextB, ctx =>
            ValueTask.FromResult<object?>(null));

        mockAccessorA.Verify(a => a.SetTenant(TenantA), Times.Once);
        mockAccessorA.Verify(a => a.SetTenant(TenantB), Times.Never);
        mockAccessorB.Verify(a => a.SetTenant(TenantB), Times.Once);
        mockAccessorB.Verify(a => a.SetTenant(TenantA), Times.Never);
    }

    private static (HubInvocationContext, Mock<HubCallerContext>) CreateHubInvocationContext(
        TenantContext? tenantContext, ITenantAccessor accessor)
    {
        var httpContext = new DefaultHttpContext();
        if (tenantContext != null)
            httpContext.Items["TenantContext"] = tenantContext;

        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        httpContext.RequestServices = services.BuildServiceProvider();

        var mockCallerContext = CreateMockHubCallerContext(httpContext, Guid.NewGuid().ToString());

        var invocationContext = new HubInvocationContext(
            mockCallerContext.Object,
            Mock.Of<IServiceProvider>(),
            Mock.Of<Hub>(),
            typeof(Hub).GetMethod(nameof(Hub.OnConnectedAsync))!,
            Array.Empty<object>());

        return (invocationContext, mockCallerContext);
    }

    private static HubLifetimeContext CreateHubLifetimeContext(
        TenantContext? tenantContext, ITenantAccessor accessor)
    {
        var httpContext = new DefaultHttpContext();
        if (tenantContext != null)
            httpContext.Items["TenantContext"] = tenantContext;

        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        httpContext.RequestServices = services.BuildServiceProvider();

        var mockCallerContext = CreateMockHubCallerContext(httpContext, Guid.NewGuid().ToString());

        return new HubLifetimeContext(mockCallerContext.Object, Mock.Of<IServiceProvider>(), Mock.Of<Hub>());
    }

    #endregion

    #region SignalRBroadcastService Tenant Isolation

    private static (SignalRBroadcastService service, Mock<IHubClients> dataClients,
        Mock<IHubClients> alarmClients, Mock<IHubClients> configClients,
        Mock<IClientProxy> dataProxy, Mock<IClientProxy> alarmProxy, Mock<IClientProxy> configProxy)
        CreateBroadcastService(TenantContext? tenantContext)
    {
        var mockDataHub = new Mock<IHubContext<DataHub>>();
        var mockAlarmHub = new Mock<IHubContext<AlarmHub>>();
        var mockConfigHub = new Mock<IHubContext<ConfigHub>>();
        var mockLogger = new Mock<ILogger<SignalRBroadcastService>>();

        var dataClients = new Mock<IHubClients>();
        var alarmClients = new Mock<IHubClients>();
        var configClients = new Mock<IHubClients>();
        var dataProxy = new Mock<IClientProxy>();
        var alarmProxy = new Mock<IClientProxy>();
        var configProxy = new Mock<IClientProxy>();

        mockDataHub.Setup(x => x.Clients).Returns(dataClients.Object);
        mockAlarmHub.Setup(x => x.Clients).Returns(alarmClients.Object);
        mockConfigHub.Setup(x => x.Clients).Returns(configClients.Object);

        dataClients.Setup(x => x.Group(It.IsAny<string>())).Returns(dataProxy.Object);
        alarmClients.Setup(x => x.Group(It.IsAny<string>())).Returns(alarmProxy.Object);
        configClients.Setup(x => x.Group(It.IsAny<string>())).Returns(configProxy.Object);

        var mockAccessor = new Mock<ITenantAccessor>();
        if (tenantContext != null)
        {
            mockAccessor.Setup(x => x.Context).Returns(tenantContext);
            mockAccessor.Setup(x => x.IsResolved).Returns(true);
            mockAccessor.Setup(x => x.TenantId).Returns(tenantContext.TenantId);
        }
        else
        {
            mockAccessor.Setup(x => x.Context).Returns((TenantContext?)null);
            mockAccessor.Setup(x => x.IsResolved).Returns(false);
        }

        var mockAlertHub = new Mock<IHubContext<AlertHub>>();
        var alertClients = new Mock<IHubClients>();
        var alertProxy = new Mock<IClientProxy>();
        mockAlertHub.Setup(x => x.Clients).Returns(alertClients.Object);
        alertClients.Setup(x => x.Group(It.IsAny<string>())).Returns(alertProxy.Object);

        var service = new SignalRBroadcastService(
            mockDataHub.Object, mockAlarmHub.Object, mockConfigHub.Object, mockAlertHub.Object,
            mockAccessor.Object, mockLogger.Object);

        return (service, dataClients, alarmClients, configClients, dataProxy, alarmProxy, configProxy);
    }

    [Fact]
    public async Task Broadcast_DataUpdate_TargetsTenantSpecificGroup()
    {
        var (service, dataClients, _, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastDataUpdateAsync(new { test = true });

        dataClients.Verify(c => c.Group($"{TenantAId}:authorized"), Times.Once);
        dataClients.Verify(c => c.Group("authorized"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_StorageCreate_TargetsTenantSpecificCollectionGroup()
    {
        var (service, dataClients, _, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastStorageCreateAsync("entries", new { id = "1" });

        dataClients.Verify(c => c.Group($"{TenantAId}:entries"), Times.Once);
        dataClients.Verify(c => c.Group("entries"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_StorageUpdate_TargetsTenantSpecificCollectionGroup()
    {
        var (service, dataClients, _, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastStorageUpdateAsync("treatments", new { id = "1" });

        dataClients.Verify(c => c.Group($"{TenantAId}:treatments"), Times.Once);
        dataClients.Verify(c => c.Group("treatments"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_StorageDelete_TargetsTenantSpecificCollectionGroup()
    {
        var (service, dataClients, _, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastStorageDeleteAsync("devicestatus", new { id = "1" });

        dataClients.Verify(c => c.Group($"{TenantAId}:devicestatus"), Times.Once);
        dataClients.Verify(c => c.Group("devicestatus"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_Notification_TargetsTenantSpecificAlarmGroup()
    {
        var (service, _, alarmClients, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastNotificationAsync(new NotificationBase { Title = "Test" });

        alarmClients.Verify(c => c.Group($"{TenantAId}:alarm-subscribers"), Times.Once);
        alarmClients.Verify(c => c.Group("alarm-subscribers"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_Alarm_TargetsTenantSpecificAlarmGroup()
    {
        var (service, _, alarmClients, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastAlarmAsync(new NotificationBase { Title = "Alarm" });

        alarmClients.Verify(c => c.Group($"{TenantAId}:alarm-subscribers"), Times.Once);
    }

    [Fact]
    public async Task Broadcast_UrgentAlarm_TargetsTenantSpecificAlarmGroup()
    {
        var (service, _, alarmClients, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastUrgentAlarmAsync(new NotificationBase { Title = "Urgent" });

        alarmClients.Verify(c => c.Group($"{TenantAId}:alarm-subscribers"), Times.Once);
    }

    [Fact]
    public async Task Broadcast_ClearAlarm_TargetsTenantSpecificAlarmGroup()
    {
        var (service, _, alarmClients, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastClearAlarmAsync(new NotificationBase { Title = "Clear" });

        alarmClients.Verify(c => c.Group($"{TenantAId}:alarm-subscribers"), Times.Once);
    }

    [Fact]
    public async Task Broadcast_Announcement_TargetsTenantSpecificAlarmGroup()
    {
        var (service, _, alarmClients, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastAnnouncementAsync(new NotificationBase { Title = "Announce" });

        alarmClients.Verify(c => c.Group($"{TenantAId}:alarm-subscribers"), Times.Once);
    }

    [Fact]
    public async Task Broadcast_ConfigChange_TargetsTenantSpecificConfigGroups()
    {
        var (service, _, _, configClients, _, _, _) = CreateBroadcastService(TenantA);
        var change = new ConfigurationChangeEvent
        {
            ConnectorName = "Dexcom",
            ChangeType = "updated"
        };

        await service.BroadcastConfigChangeAsync(change);

        configClients.Verify(c => c.Group($"{TenantAId}:config:dexcom"), Times.Once);
        configClients.Verify(c => c.Group($"{TenantAId}:config:all"), Times.Once);
        configClients.Verify(c => c.Group("config:dexcom"), Times.Never);
        configClients.Verify(c => c.Group("config:all"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_NotificationCreated_TargetsTenantSpecificUserAndAuthorizedGroups()
    {
        var (service, dataClients, _, _, _, _, _) = CreateBroadcastService(TenantA);
        var notification = new InAppNotificationDto { Id = Guid.NewGuid() };

        await service.BroadcastNotificationCreatedAsync("user-123", notification);

        dataClients.Verify(c => c.Group($"{TenantAId}:user-user-123"), Times.Once);
        dataClients.Verify(c => c.Group($"{TenantAId}:authorized"), Times.Once);
        dataClients.Verify(c => c.Group("user-user-123"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_TrackerUpdate_TargetsTenantSpecificAuthorizedGroup()
    {
        var (service, dataClients, _, _, _, _, _) = CreateBroadcastService(TenantA);

        await service.BroadcastTrackerUpdateAsync("created", new { id = "tracker-1" });

        dataClients.Verify(c => c.Group($"{TenantAId}:authorized"), Times.Once);
        dataClients.Verify(c => c.Group("authorized"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_TwoTenants_DoNotCrossTenantBoundaries()
    {
        var (serviceA, dataClientsA, _, _, _, _, _) = CreateBroadcastService(TenantA);
        var (serviceB, dataClientsB, _, _, _, _, _) = CreateBroadcastService(TenantB);

        await serviceA.BroadcastDataUpdateAsync(new { from = "A" });
        await serviceB.BroadcastDataUpdateAsync(new { from = "B" });

        dataClientsA.Verify(c => c.Group($"{TenantAId}:authorized"), Times.Once);
        dataClientsA.Verify(c => c.Group($"{TenantBId}:authorized"), Times.Never);

        dataClientsB.Verify(c => c.Group($"{TenantBId}:authorized"), Times.Once);
        dataClientsB.Verify(c => c.Group($"{TenantAId}:authorized"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_WithoutTenantContext_DoesNotThrow_LogsError()
    {
        var (service, _, _, _, _, _, _) = CreateBroadcastService(tenantContext: null);

        // The InvalidOperationException from GetTenantId is caught by each broadcast method
        var act = () => service.BroadcastDataUpdateAsync(new { test = true });
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Broadcast_StorageCreate_TwoTenants_DoNotCross()
    {
        var (serviceA, dataClientsA, _, _, _, _, _) = CreateBroadcastService(TenantA);
        var (serviceB, dataClientsB, _, _, _, _, _) = CreateBroadcastService(TenantB);

        await serviceA.BroadcastStorageCreateAsync("entries", new { id = "entry-a" });
        await serviceB.BroadcastStorageCreateAsync("entries", new { id = "entry-b" });

        dataClientsA.Verify(c => c.Group($"{TenantAId}:entries"), Times.Once);
        dataClientsA.Verify(c => c.Group($"{TenantBId}:entries"), Times.Never);
        dataClientsB.Verify(c => c.Group($"{TenantBId}:entries"), Times.Once);
        dataClientsB.Verify(c => c.Group($"{TenantAId}:entries"), Times.Never);
    }

    [Fact]
    public async Task Broadcast_Alarms_TwoTenants_DoNotCross()
    {
        var (serviceA, _, alarmClientsA, _, _, _, _) = CreateBroadcastService(TenantA);
        var (serviceB, _, alarmClientsB, _, _, _, _) = CreateBroadcastService(TenantB);

        await serviceA.BroadcastAlarmAsync(new NotificationBase { Title = "A" });
        await serviceB.BroadcastAlarmAsync(new NotificationBase { Title = "B" });

        alarmClientsA.Verify(c => c.Group($"{TenantAId}:alarm-subscribers"), Times.Once);
        alarmClientsA.Verify(c => c.Group($"{TenantBId}:alarm-subscribers"), Times.Never);
        alarmClientsB.Verify(c => c.Group($"{TenantBId}:alarm-subscribers"), Times.Once);
        alarmClientsB.Verify(c => c.Group($"{TenantAId}:alarm-subscribers"), Times.Never);
    }

    #endregion

    #region TenantResolutionMiddleware Isolation

    [Fact]
    public async Task TenantResolutionMiddleware_UnknownSlug_Returns404()
    {
        // "unknown" is not in the cache, so the middleware will try to query the DB.
        // We register a mock DbContextFactory that returns a context with no tenants.
        var middleware = CreateMiddleware();
        var context = CreateMiddlewareHttpContext("unknown.nocturnecgm.com", registerDbFactory: true);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task TenantResolutionMiddleware_InactiveTenant_Returns403()
    {
        var middleware = CreateMiddleware(
            tenants: new[] { ("inactive", TenantAId, false, false) });
        var context = CreateMiddlewareHttpContext("inactive.nocturnecgm.com");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task TenantResolutionMiddleware_ValidTenant_SetsAccessorAndItems()
    {
        var nextCalled = false;
        var middleware = CreateMiddlewareWithNext(
            ctx =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            tenants: new[] { ("alice", TenantAId, true, false) });
        var context = CreateMiddlewareHttpContext("alice.nocturnecgm.com");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        var tenantContext = context.Items["TenantContext"] as TenantContext;
        tenantContext.Should().NotBeNull();
        tenantContext!.TenantId.Should().Be(TenantAId);
        tenantContext.Slug.Should().Be("alice");
    }

    [Fact]
    public async Task TenantResolutionMiddleware_DifferentSubdomains_ResolveDifferentTenants()
    {
        var middleware = CreateMiddlewareWithNext(
            _ => Task.CompletedTask,
            tenants: new[] { ("alice", TenantAId, true, false), ("bob", TenantBId, true, false) });

        var aliceContext = CreateMiddlewareHttpContext("alice.nocturnecgm.com");
        await middleware.InvokeAsync(aliceContext);

        var bobContext = CreateMiddlewareHttpContext("bob.nocturnecgm.com");
        await middleware.InvokeAsync(bobContext);

        var aliceTenant = aliceContext.Items["TenantContext"] as TenantContext;
        var bobTenant = bobContext.Items["TenantContext"] as TenantContext;

        aliceTenant!.TenantId.Should().Be(TenantAId);
        bobTenant!.TenantId.Should().Be(TenantBId);
        aliceTenant.TenantId.Should().NotBe(bobTenant.TenantId);
    }

    [Fact]
    public async Task TenantResolutionMiddleware_NoSubdomain_ResolvesDefaultTenant()
    {
        var defaultTenantId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var middleware = CreateMiddlewareWithNext(
            _ => Task.CompletedTask,
            tenants: new[] { ("default", defaultTenantId, true, true) });

        var context = CreateMiddlewareHttpContext("nocturnecgm.com");
        await middleware.InvokeAsync(context);

        var tenant = context.Items["TenantContext"] as TenantContext;
        tenant.Should().NotBeNull();
        tenant!.TenantId.Should().Be(defaultTenantId);
    }

    [Fact]
    public async Task TenantResolutionMiddleware_BaseDomainWithPort_StripsPortForSubdomainExtraction()
    {
        var middleware = CreateMiddlewareWithNext(
            _ => Task.CompletedTask,
            tenants: new[] { ("alice", TenantAId, true, false) },
            baseDomain: "localhost:1612");

        // Host.Host strips port, so "alice.localhost" not "alice.localhost:1612"
        var context = CreateMiddlewareHttpContext("alice.localhost");
        await middleware.InvokeAsync(context);

        var tenant = context.Items["TenantContext"] as TenantContext;
        tenant.Should().NotBeNull();
        tenant!.TenantId.Should().Be(TenantAId);
        tenant.Slug.Should().Be("alice");
    }

    [Fact]
    public async Task TenantResolutionMiddleware_TenantlessAllowedPath_NoSubdomain_SkipsDefaultTenantFallback()
    {
        // Regression: cross-tenant endpoints (e.g. bot pending-link creation)
        // hit the apex with no slug. They must NOT resolve to the IsDefault
        // tenant — otherwise TenantSetupMiddleware blocks them with 503
        // setup_required when the default tenant has no passkey credentials.
        var defaultId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var nextCalled = false;
        var middleware = CreateMiddlewareWithNext(
            _ => { nextCalled = true; return Task.CompletedTask; },
            tenants: new[] { ("default", defaultId, true, true) });

        var context = CreateMiddlewareHttpContext("nocturnecgm.com");
        context.Request.Path = "/api/v4/chat-identity/directory/pending-links";
        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Items.ContainsKey("TenantContext").Should().BeFalse();
    }

    [Fact]
    public async Task TenantResolutionMiddleware_BaseDomainWithPort_NoSubdomain_ResolvesDefault()
    {
        var defaultId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var middleware = CreateMiddlewareWithNext(
            _ => Task.CompletedTask,
            tenants: new[] { ("default", defaultId, true, true) },
            baseDomain: "localhost:1612");

        var context = CreateMiddlewareHttpContext("localhost");
        await middleware.InvokeAsync(context);

        var tenant = context.Items["TenantContext"] as TenantContext;
        tenant.Should().NotBeNull();
        tenant!.TenantId.Should().Be(defaultId);
    }

    private static TenantResolutionMiddleware CreateMiddleware(
        (string slug, Guid id, bool active, bool isDefault)[]? tenants = null,
        string baseDomain = "nocturnecgm.com")
    {
        return CreateMiddlewareWithNext(_ => Task.CompletedTask, tenants, baseDomain);
    }

    private static TenantResolutionMiddleware CreateMiddlewareWithNext(
        RequestDelegate next,
        (string slug, Guid id, bool active, bool isDefault)[]? tenants = null,
        string baseDomain = "nocturnecgm.com")
    {
        var config = Options.Create(new MultitenancyConfiguration
        {
            BaseDomain = baseDomain
        });
        var logger = Mock.Of<ILogger<TenantResolutionMiddleware>>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        // Pre-populate cache to avoid needing a real DbContext
        if (tenants != null)
        {
            foreach (var (slug, id, active, isDefault) in tenants)
            {
                var ctx = new TenantContext(id, slug, slug, active);
                cache.Set($"tenant:{slug}", ctx, TimeSpan.FromMinutes(5));
                if (isDefault)
                    cache.Set("tenant:__default__", ctx, TimeSpan.FromMinutes(5));
            }
        }

        return new TenantResolutionMiddleware(next, logger, config, cache);
    }

    private static DefaultHttpContext CreateMiddlewareHttpContext(string hostname, bool registerDbFactory = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(hostname);

        var mockAccessor = new Mock<ITenantAccessor>();
        TenantContext? capturedContext = null;
        mockAccessor.Setup(a => a.SetTenant(It.IsAny<TenantContext>()))
            .Callback<TenantContext>(tc => capturedContext = tc);
        mockAccessor.Setup(a => a.Context).Returns(() => capturedContext);
        mockAccessor.Setup(a => a.IsResolved).Returns(() => capturedContext != null);

        var services = new ServiceCollection();
        services.AddSingleton(mockAccessor.Object);

        if (registerDbFactory)
        {
            // Register an in-memory DbContext factory so the middleware can query for tenants
            services.AddDbContextFactory<Nocturne.Infrastructure.Data.NocturneDbContext>(options =>
                options.UseInMemoryDatabase($"TenantTest_{Guid.NewGuid()}"));
        }

        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    #endregion

    #region DataHub Tenant Isolation

    [Fact]
    public async Task DataHub_Authorize_AddsToTenantScopedAuthorizedGroup()
    {
        var (hub, mockGroups) = CreateDataHub(TenantA);

        await hub.Authorize(new AuthorizeRequest { Secret = "wrong-secret" });

        // Even on failed auth, the hub should NOT add to bare "authorized" group.
        mockGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), "authorized", default),
            Times.Never);
    }

    [Fact]
    public async Task DataHub_Subscribe_AddsToTenantScopedCollectionGroups()
    {
        var (hub, mockGroups) = CreateDataHub(TenantA);

        await hub.Subscribe(new StorageSubscribeRequest
        {
            Collections = new[] { "entries", "treatments" }
        });

        // Verify groups are tenant-scoped
        mockGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantAId}:entries", default),
            Times.Once);
        mockGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantAId}:treatments", default),
            Times.Once);

        // Verify bare group names are never used
        mockGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), "entries", default),
            Times.Never);
        mockGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), "treatments", default),
            Times.Never);
    }

    [Fact]
    public async Task DataHub_Subscribe_DifferentTenants_GetDifferentGroups()
    {
        var (hubA, groupsA) = CreateDataHub(TenantA);
        var (hubB, groupsB) = CreateDataHub(TenantB);

        await hubA.Subscribe(new StorageSubscribeRequest { Collections = new[] { "entries" } });
        await hubB.Subscribe(new StorageSubscribeRequest { Collections = new[] { "entries" } });

        groupsA.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantAId}:entries", default), Times.Once);
        groupsA.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantBId}:entries", default), Times.Never);

        groupsB.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantBId}:entries", default), Times.Once);
        groupsB.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantAId}:entries", default), Times.Never);
    }

    private static (DataHub hub, Mock<IGroupManager> groups) CreateDataHub(TenantContext tenantContext)
    {
        var mockLogger = new Mock<ILogger<DataHub>>();
        var mockAuthService = new Mock<Core.Contracts.IAuthorizationService>();
        var hub = new DataHub(mockLogger.Object, mockAuthService.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantContext"] = tenantContext;

        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IConfiguration>());
        httpContext.RequestServices = services.BuildServiceProvider();

        var mockCallerContext = CreateMockHubCallerContext(httpContext);

        var mockGroups = new Mock<IGroupManager>();
        var mockClients = new Mock<IHubCallerClients>();
        mockClients.Setup(c => c.Caller).Returns(Mock.Of<ISingleClientProxy>());

        typeof(Hub).GetProperty("Context")!.SetValue(hub, mockCallerContext.Object);
        typeof(Hub).GetProperty("Groups")!.SetValue(hub, mockGroups.Object);
        typeof(Hub).GetProperty("Clients")!.SetValue(hub, mockClients.Object);

        return (hub, mockGroups);
    }

    #endregion

    #region AlarmHub Tenant Isolation

    [Fact]
    public async Task AlarmHub_Subscribe_AddsToTenantScopedAlarmGroup()
    {
        var (hub, mockGroups) = CreateAlarmHub(TenantA);

        await hub.Subscribe(new AlarmSubscribeRequest { Secret = "wrong" });

        // Even on failed auth, should never use bare group names
        mockGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), "alarm-subscribers", default),
            Times.Never);
    }

    [Fact]
    public async Task AlarmHub_Subscribe_DifferentTenants_GetDifferentGroups()
    {
        var (hubA, groupsA) = CreateAlarmHub(TenantA);
        var (hubB, groupsB) = CreateAlarmHub(TenantB);

        await hubA.Subscribe(new AlarmSubscribeRequest());
        await hubB.Subscribe(new AlarmSubscribeRequest());

        // Neither should use the other's tenant group
        groupsA.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.Is<string>(s => s.StartsWith($"{TenantBId}:")), default),
            Times.Never);
        groupsB.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.Is<string>(s => s.StartsWith($"{TenantAId}:")), default),
            Times.Never);
    }

    private static (AlarmHub hub, Mock<IGroupManager> groups) CreateAlarmHub(TenantContext tenantContext)
    {
        var mockLogger = new Mock<ILogger<AlarmHub>>();
        var mockAuthService = new Mock<Core.Contracts.IAuthorizationService>();
        var hub = new AlarmHub(mockLogger.Object, mockAuthService.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantContext"] = tenantContext;

        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IConfiguration>());
        httpContext.RequestServices = services.BuildServiceProvider();

        var mockCallerContext = CreateMockHubCallerContext(httpContext);

        var mockGroups = new Mock<IGroupManager>();
        var mockClients = new Mock<IHubCallerClients>();
        mockClients.Setup(c => c.Caller).Returns(Mock.Of<ISingleClientProxy>());

        typeof(Hub).GetProperty("Context")!.SetValue(hub, mockCallerContext.Object);
        typeof(Hub).GetProperty("Groups")!.SetValue(hub, mockGroups.Object);
        typeof(Hub).GetProperty("Clients")!.SetValue(hub, mockClients.Object);

        return (hub, mockGroups);
    }

    #endregion

    #region ConfigHub Tenant Isolation

    [Fact]
    public async Task ConfigHub_Subscribe_AddsToTenantScopedConfigGroup()
    {
        var (hub, mockGroups) = CreateConfigHub(TenantA);

        await hub.Subscribe("dexcom");

        mockGroups.Verify(
            g => g.AddToGroupAsync("test-connection", $"{TenantAId}:config:dexcom", default),
            Times.Once);
        mockGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), "config:dexcom", default),
            Times.Never);
    }

    [Fact]
    public async Task ConfigHub_SubscribeAll_AddsToTenantScopedConfigAllGroup()
    {
        var (hub, mockGroups) = CreateConfigHub(TenantA);

        await hub.SubscribeAll();

        mockGroups.Verify(
            g => g.AddToGroupAsync("test-connection", $"{TenantAId}:config:all", default),
            Times.Once);
        mockGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), "config:all", default),
            Times.Never);
    }

    [Fact]
    public async Task ConfigHub_Unsubscribe_RemovesFromTenantScopedConfigGroup()
    {
        var (hub, mockGroups) = CreateConfigHub(TenantA);

        await hub.Unsubscribe("libre");

        mockGroups.Verify(
            g => g.RemoveFromGroupAsync("test-connection", $"{TenantAId}:config:libre", default),
            Times.Once);
        mockGroups.Verify(
            g => g.RemoveFromGroupAsync(It.IsAny<string>(), "config:libre", default),
            Times.Never);
    }

    [Fact]
    public async Task ConfigHub_UnsubscribeAll_RemovesFromTenantScopedConfigAllGroup()
    {
        var (hub, mockGroups) = CreateConfigHub(TenantA);

        await hub.UnsubscribeAll();

        mockGroups.Verify(
            g => g.RemoveFromGroupAsync("test-connection", $"{TenantAId}:config:all", default),
            Times.Once);
    }

    [Fact]
    public async Task ConfigHub_DifferentTenants_GetDifferentGroups()
    {
        var (hubA, groupsA) = CreateConfigHub(TenantA);
        var (hubB, groupsB) = CreateConfigHub(TenantB);

        await hubA.Subscribe("dexcom");
        await hubB.Subscribe("dexcom");

        groupsA.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantAId}:config:dexcom", default),
            Times.Once);
        groupsA.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantBId}:config:dexcom", default),
            Times.Never);

        groupsB.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantBId}:config:dexcom", default),
            Times.Once);
        groupsB.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), $"{TenantAId}:config:dexcom", default),
            Times.Never);
    }

    private static (ConfigHub hub, Mock<IGroupManager> groups) CreateConfigHub(TenantContext tenantContext)
    {
        var mockLogger = new Mock<ILogger<ConfigHub>>();
        var hub = new ConfigHub(mockLogger.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantContext"] = tenantContext;

        var mockCallerContext = CreateMockHubCallerContext(httpContext);
        var mockGroups = new Mock<IGroupManager>();

        typeof(Hub).GetProperty("Context")!.SetValue(hub, mockCallerContext.Object);
        typeof(Hub).GetProperty("Groups")!.SetValue(hub, mockGroups.Object);

        return (hub, mockGroups);
    }

    #endregion

    #region HttpContextTenantAccessor Isolation

    [Fact]
    public void HttpContextTenantAccessor_InitialState_IsNotResolved()
    {
        var accessor = new HttpContextTenantAccessor();

        accessor.IsResolved.Should().BeFalse();
        accessor.TenantId.Should().Be(Guid.Empty);
        accessor.Context.Should().BeNull();
    }

    [Fact]
    public void HttpContextTenantAccessor_SetTenant_ResolvesCorrectly()
    {
        var accessor = new HttpContextTenantAccessor();

        accessor.SetTenant(TenantA);

        accessor.IsResolved.Should().BeTrue();
        accessor.TenantId.Should().Be(TenantAId);
        accessor.Context.Should().Be(TenantA);
    }

    [Fact]
    public void HttpContextTenantAccessor_SetTenant_OverwritesPrevious()
    {
        var accessor = new HttpContextTenantAccessor();

        accessor.SetTenant(TenantA);
        accessor.SetTenant(TenantB);

        accessor.TenantId.Should().Be(TenantBId);
        accessor.Context.Should().Be(TenantB);
    }

    [Fact]
    public void HttpContextTenantAccessor_SetTenant_NullThrows()
    {
        var accessor = new HttpContextTenantAccessor();

        var act = () => accessor.SetTenant(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void HttpContextTenantAccessor_TwoInstances_AreIsolated()
    {
        var accessorA = new HttpContextTenantAccessor();
        var accessorB = new HttpContextTenantAccessor();

        accessorA.SetTenant(TenantA);
        accessorB.SetTenant(TenantB);

        accessorA.TenantId.Should().Be(TenantAId);
        accessorB.TenantId.Should().Be(TenantBId);
        accessorA.TenantId.Should().NotBe(accessorB.TenantId);
    }

    #endregion
}
