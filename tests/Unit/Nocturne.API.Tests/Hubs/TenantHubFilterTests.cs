using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
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
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = handshakeServices };
        if (withTenant)
        {
            httpContext.Items[TenantAwareHub.TenantContextKey] =
                new TenantContext(Tenant, "default", "Default", IsActive: true);
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
}
