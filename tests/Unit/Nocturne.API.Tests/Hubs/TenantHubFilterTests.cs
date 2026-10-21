using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Covers <see cref="TenantHubFilter"/>, which carries the tenant resolved on the upgrade handshake
/// into each hub invocation. SignalR builds a fresh DI scope per invocation, so the scoped
/// <see cref="ITenantAccessor"/> a service resolved from
/// <see cref="HubInvocationContext.ServiceProvider"/> inside a method body sees is the invocation's,
/// not the handshake request's.
/// </summary>
[Trait("Category", "Unit")]
public class TenantHubFilterTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class StubHub : Hub
    {
        public void Method()
        {
        }
    }

    /// <summary>
    /// A connection whose handshake resolved <see cref="Tenant"/>, with a distinct
    /// <see cref="ITenantAccessor"/> on the handshake scope and on the invocation scope.
    /// </summary>
    private static (HubCallerContext Caller, ITenantAccessor Handshake, IServiceProvider Invocation, ITenantAccessor InvocationAccessor)
        CreateConnection(bool withTenant = true)
    {
        var handshakeAccessor = new HttpContextTenantAccessor();
        var handshakeServices = new ServiceCollection()
            .AddSingleton<ITenantAccessor>(handshakeAccessor)
            .AddSingleton<ICategoryReadContext>(new CategoryReadContext())
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = handshakeServices };
        if (withTenant)
        {
            httpContext.SetTenantContext(
                new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false));
        }

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(c => c.ConnectionId).Returns("conn-1");
        caller.SetupGet(c => c.Features).Returns(features);
        caller.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());

        var invocationAccessor = new HttpContextTenantAccessor();
        var invocationServices = new ServiceCollection()
            .AddSingleton<ITenantAccessor>(invocationAccessor)
            .AddSingleton<ICategoryReadContext>(new CategoryReadContext())
            .BuildServiceProvider();

        return (caller.Object, handshakeAccessor, invocationServices, invocationAccessor);
    }

    [Fact]
    public async Task The_invocation_scopes_accessor_is_the_one_that_gets_the_tenant()
    {
        // A service a method body resolves from HubInvocationContext.ServiceProvider comes from the
        // invocation scope. The hub's own constructor dependencies do not: the dispatcher activates
        // the hub before the filter pipeline runs, so those latch Guid.Empty whatever this sets.
        var (caller, handshake, invocationServices, invocationAccessor) = CreateConnection();

        var invocation = new HubInvocationContext(
            caller,
            invocationServices,
            hub: new StubHub(),
            hubMethod: typeof(StubHub).GetMethod(nameof(StubHub.Method), BindingFlags.Public | BindingFlags.Instance)!,
            hubMethodArguments: []);

        var reached = false;
        await new TenantHubFilter().InvokeMethodAsync(invocation, _ =>
        {
            reached = true;
            invocationAccessor.TenantId.Should().Be(Tenant, "the tenant must be set before the method runs");
            return ValueTask.FromResult<object?>(null);
        });

        reached.Should().BeTrue();
        invocationAccessor.TenantId.Should().Be(Tenant);
        handshake.IsResolved.Should().BeFalse(
            "the handshake request's scope is not the one the invocation resolves services from");
    }

    [Fact]
    public async Task Lifetime_events_populate_their_own_scope()
    {
        var (caller, _, invocationServices, invocationAccessor) = CreateConnection();
        var lifetime = new HubLifetimeContext(caller, invocationServices, new StubHub());

        await new TenantHubFilter().OnConnectedAsync(lifetime, _ => Task.CompletedTask);

        invocationAccessor.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task A_connection_with_no_resolved_tenant_leaves_the_accessor_unset()
    {
        var (caller, _, invocationServices, invocationAccessor) = CreateConnection(withTenant: false);

        var invocation = new HubInvocationContext(
            caller,
            invocationServices,
            hub: new StubHub(),
            hubMethod: typeof(StubHub).GetMethod(nameof(StubHub.Method), BindingFlags.Public | BindingFlags.Instance)!,
            hubMethodArguments: []);

        await new TenantHubFilter().InvokeMethodAsync(
            invocation, _ => ValueTask.FromResult<object?>(null));

        invocationAccessor.IsResolved.Should().BeFalse();
    }

    private static HubInvocationContext Invocation(HubCallerContext caller, IServiceProvider services) => new(
        caller,
        services,
        hub: new StubHub(),
        hubMethod: typeof(StubHub).GetMethod(nameof(StubHub.Method), BindingFlags.Public | BindingFlags.Instance)!,
        hubMethodArguments: []);

    private static ICategoryReadContext HandshakeReader(HubCallerContext caller) =>
        caller.GetHttpContext()!.RequestServices.GetRequiredService<ICategoryReadContext>();

    [Fact]
    public async Task A_share_connection_invokes_as_the_share()
    {
        var (caller, _, invocationServices, _) = CreateConnection();
        var handshake = HandshakeReader(caller);
        handshake.MarkShare();
        handshake.SetVisibleCategories("glucose.read");
        handshake.SetFullHistory(true);

        await new TenantHubFilter().InvokeMethodAsync(
            Invocation(caller, invocationServices), _ => ValueTask.FromResult<object?>(null));

        var invocation = invocationServices.GetRequiredService<ICategoryReadContext>();
        invocation.IsShare.Should().BeTrue("a share connection's method must not read as a non-share");
        invocation.VisibleCategoriesCsv.Should().Be("glucose.read");
        invocation.FullHistory.Should().BeTrue();
        invocation.IsHistoryClamped.Should().BeFalse();
    }

    [Fact]
    public async Task A_share_without_full_history_invokes_clamped()
    {
        var (caller, _, invocationServices, _) = CreateConnection();
        var handshake = HandshakeReader(caller);
        handshake.MarkShare();
        handshake.SetVisibleCategories("glucose.read");

        await new TenantHubFilter().InvokeMethodAsync(
            Invocation(caller, invocationServices), _ => ValueTask.FromResult<object?>(null));

        invocationServices.GetRequiredService<ICategoryReadContext>().IsHistoryClamped.Should().BeTrue();
    }

    [Fact]
    public async Task A_clamped_connection_invokes_clamped()
    {
        var (caller, _, invocationServices, _) = CreateConnection();
        HandshakeReader(caller).ClampMemberHistory();

        await new TenantHubFilter().InvokeMethodAsync(
            Invocation(caller, invocationServices), _ => ValueTask.FromResult<object?>(null));

        var invocation = invocationServices.GetRequiredService<ICategoryReadContext>();
        invocation.IsHistoryClamped.Should().BeTrue("the 24-hour clamp fails open in a scope that never hears of it");
        invocation.IsShare.Should().BeFalse();
    }

    [Fact]
    public async Task An_in_band_clamp_reaches_the_next_invocation()
    {
        var (caller, _, _, _) = CreateConnection();
        var filter = new TenantHubFilter();
        var first = new ServiceCollection()
            .AddSingleton<ITenantAccessor>(new HttpContextTenantAccessor())
            .AddSingleton<ICategoryReadContext>(new CategoryReadContext())
            .BuildServiceProvider();
        var second = new ServiceCollection()
            .AddSingleton<ITenantAccessor>(new HttpContextTenantAccessor())
            .AddSingleton<ICategoryReadContext>(new CategoryReadContext())
            .BuildServiceProvider();

        await filter.InvokeMethodAsync(Invocation(caller, first), _ =>
        {
            HubAuthorizationState.Grant(caller, new HubAuthorization(
                Tenant, Scope.Normalize([Scope.GlucoseRead]),
                HubCredentialKind.Subject, Guid.NewGuid(), HistoryClamped: true));
            return ValueTask.FromResult<object?>(null);
        });
        await filter.InvokeMethodAsync(Invocation(caller, second), _ => ValueTask.FromResult<object?>(null));

        second.GetRequiredService<ICategoryReadContext>().IsHistoryClamped.Should().BeTrue();
    }
}
