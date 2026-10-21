using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Covers <see cref="TenantHubFilter"/>, which sets the reader the upgrade request recorded on each
/// hub invocation and lifetime scope. SignalR builds a fresh DI scope per invocation, and for long
/// polling serves the connection from a copy of the upgrade request whose scope never ran the
/// middleware, so neither scope knows the share, the categories or the clamp until this runs.
/// </summary>
[Trait("Category", "Unit")]
public class TenantHubFilterTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly TenantContext TenantContext =
        new(Tenant, "default", "Default", IsActive: true, IsDemo: false);

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

    private static ServiceProvider FreshScope() => new ServiceCollection()
        .AddSingleton<ITenantAccessor>(new HttpContextTenantAccessor())
        .AddSingleton<ICategoryReadContext>(new CategoryReadContext())
        .AddSingleton<IAuditContext>(new AuditContext())
        .BuildServiceProvider();

    /// <summary>
    /// A connection whose upgrade request resolved <see cref="Tenant"/> (unless
    /// <paramref name="withTenant"/> is false), was narrowed by <paramref name="arrange"/> as the
    /// middleware would narrow it, and recorded its reader. With <paramref name="longPolling"/> the
    /// connection is then served from a copy that keeps the items but has a fresh scope.
    /// </summary>
    private static HubCallerContext CreateConnection(
        bool withTenant = true, Action<ICategoryReadContext>? arrange = null, bool longPolling = false)
    {
        var upgrade = new DefaultHttpContext { RequestServices = FreshScope() };
        if (withTenant)
        {
            upgrade.SetTenantContext(TenantContext);
            upgrade.RequestServices.GetRequiredService<ITenantAccessor>().SetTenant(TenantContext);
        }

        arrange?.Invoke(upgrade.RequestServices.GetRequiredService<ICategoryReadContext>());
        upgrade.RecordReader();

        var served = upgrade;
        if (longPolling)
        {
            served = new DefaultHttpContext { RequestServices = FreshScope() };
            foreach (var item in upgrade.Items)
                served.Items[item.Key] = item.Value;
        }

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = served });

        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(c => c.ConnectionId).Returns("conn-1");
        caller.SetupGet(c => c.Features).Returns(features);
        caller.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());
        return caller.Object;
    }

    private static HubInvocationContext Invocation(HubCallerContext caller, IServiceProvider services) => new(
        caller,
        services,
        hub: new StubHub(),
        hubMethod: typeof(StubHub).GetMethod(nameof(StubHub.Method), BindingFlags.Public | BindingFlags.Instance)!,
        hubMethodArguments: []);

    private static async Task<IServiceProvider> InvokeAsync(HubCallerContext caller)
    {
        var scope = FreshScope();
        await new TenantHubFilter().InvokeMethodAsync(
            Invocation(caller, scope), _ => ValueTask.FromResult<object?>(null));
        return scope;
    }

    private static void Share(ICategoryReadContext reader, bool fullHistory)
    {
        reader.MarkShare();
        reader.SetVisibleCategories("glucose.read");
        reader.SetFullHistory(fullHistory);
    }

    [Fact]
    public async Task The_invocation_scope_gets_the_tenant_before_the_method_runs()
    {
        var caller = CreateConnection();
        var scope = FreshScope();

        var reached = false;
        await new TenantHubFilter().InvokeMethodAsync(Invocation(caller, scope), _ =>
        {
            reached = true;
            scope.GetRequiredService<ITenantAccessor>().TenantId.Should().Be(Tenant);
            return ValueTask.FromResult<object?>(null);
        });

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Lifetime_events_populate_their_own_scope()
    {
        var caller = CreateConnection();
        var scope = FreshScope();

        await new TenantHubFilter().OnConnectedAsync(
            new HubLifetimeContext(caller, scope, new StubHub()), _ => Task.CompletedTask);

        scope.GetRequiredService<ITenantAccessor>().TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task A_connection_with_no_resolved_tenant_leaves_the_accessor_unset()
    {
        var scope = await InvokeAsync(CreateConnection(withTenant: false));

        scope.GetRequiredService<ITenantAccessor>().IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task A_connection_whose_request_recorded_no_reader_reads_as_no_one()
    {
        var upgrade = new DefaultHttpContext { RequestServices = FreshScope() };
        upgrade.SetTenantContext(TenantContext);
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = upgrade });
        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(c => c.Features).Returns(features);
        caller.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());

        var scope = await InvokeAsync(caller.Object);

        scope.GetRequiredService<ITenantAccessor>().IsResolved.Should().BeFalse(
            "a tenant without the reader it was narrowed by would read as an unclamped non-share");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_share_connection_invokes_as_the_share(bool longPolling)
    {
        var scope = await InvokeAsync(CreateConnection(arrange: r => Share(r, fullHistory: true), longPolling: longPolling));

        var invocation = scope.GetRequiredService<ICategoryReadContext>();
        invocation.IsShare.Should().BeTrue("a share connection's method must not read as a non-share");
        invocation.VisibleCategoriesCsv.Should().Be("glucose.read");
        invocation.FullHistory.Should().BeTrue();
        invocation.IsHistoryClamped.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_share_without_full_history_invokes_clamped(bool longPolling)
    {
        var scope = await InvokeAsync(CreateConnection(arrange: r => Share(r, fullHistory: false), longPolling: longPolling));

        scope.GetRequiredService<ICategoryReadContext>().IsHistoryClamped.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clamped_connection_invokes_clamped(bool longPolling)
    {
        var scope = await InvokeAsync(CreateConnection(arrange: r => r.ClampMemberHistory(), longPolling: longPolling));

        var invocation = scope.GetRequiredService<ICategoryReadContext>();
        invocation.IsHistoryClamped.Should().BeTrue("the 24-hour clamp fails open in a scope that never hears of it");
        invocation.IsShare.Should().BeFalse();
    }

    [Fact]
    public async Task A_long_polling_copy_of_the_upgrade_request_reads_as_the_connection_once_connected()
    {
        var caller = CreateConnection(arrange: r => Share(r, fullHistory: false), longPolling: true);
        var copy = caller.GetHttpContext()!.RequestServices;

        await new TenantHubFilter().OnConnectedAsync(
            new HubLifetimeContext(caller, FreshScope(), new StubHub()), _ => Task.CompletedTask);

        copy.GetRequiredService<ITenantAccessor>().TenantId.Should().Be(Tenant);
        copy.GetRequiredService<ICategoryReadContext>().IsShare.Should().BeTrue(
            "hub methods read tenant data through the upgrade request's scope");
        copy.GetRequiredService<ICategoryReadContext>().IsHistoryClamped.Should().BeTrue();
    }

    [Fact]
    public async Task An_in_band_clamp_reaches_the_next_invocation()
    {
        var caller = CreateConnection();
        var filter = new TenantHubFilter();

        await filter.InvokeMethodAsync(Invocation(caller, FreshScope()), _ =>
        {
            HubAuthorizationState.Grant(caller, new HubAuthorization(
                Tenant, Scope.Normalize([Scope.GlucoseRead]),
                HubCredentialKind.Subject, Guid.NewGuid(), HistoryClamped: true));
            return ValueTask.FromResult<object?>(null);
        });
        var second = await InvokeAsync(caller);

        second.GetRequiredService<ICategoryReadContext>().IsHistoryClamped.Should().BeTrue();
    }

    [Fact]
    public async Task The_invocation_scope_carries_the_upgrade_requests_audit_actor()
    {
        var caller = CreateConnection();
        var subject = Guid.NewGuid();
        caller.GetHttpContext()!.Items[AuditContext.HttpItemKey] =
            new AuditContext { SubjectId = subject, AuthType = "Session", Endpoint = "GET /hubs/data" };

        var scope = await InvokeAsync(caller);

        var audit = scope.GetRequiredService<IAuditContext>();
        audit.SubjectId.Should().Be(subject);
        audit.Endpoint.Should().Be("GET /hubs/data");
    }

    [Fact]
    public async Task A_long_polling_copy_of_the_upgrade_request_writes_as_its_actor_once_connected()
    {
        var caller = CreateConnection(longPolling: true);
        var subject = Guid.NewGuid();
        caller.GetHttpContext()!.Items[AuditContext.HttpItemKey] =
            new AuditContext { SubjectId = subject, AuthType = "SessionCookie", IpAddress = "203.0.113.7" };
        var copy = caller.GetHttpContext()!.RequestServices;

        await new TenantHubFilter().OnConnectedAsync(
            new HubLifetimeContext(caller, FreshScope(), new StubHub()), _ => Task.CompletedTask);

        var audit = copy.GetRequiredService<IAuditContext>();
        audit.SubjectId.Should().Be(subject, "hub methods write through the upgrade request's scope");
        audit.AuthType.Should().Be("SessionCookie");
        audit.IpAddress.Should().Be("203.0.113.7");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_in_band_credential_is_the_actor_of_later_writes(bool longPolling)
    {
        var caller = CreateConnection(longPolling: longPolling);
        var upgrade = caller.GetHttpContext()!;
        upgrade.Items[AuditContext.HttpItemKey] = new AuditContext { IpAddress = "203.0.113.7", Endpoint = "GET /hubs/alarms" };
        var filter = new TenantHubFilter();
        await filter.OnConnectedAsync(new HubLifetimeContext(caller, FreshScope(), new StubHub()), _ => Task.CompletedTask);
        var subject = Guid.NewGuid();
        var grant = Guid.NewGuid();

        await filter.InvokeMethodAsync(Invocation(caller, FreshScope()), _ =>
        {
            HubAuthorizationState.Grant(caller, new HubAuthorization(
                Tenant, Scope.Normalize([Scope.GlucoseRead]), HubCredentialKind.Subject, subject, HistoryClamped: false)
            {
                Source = new HubCredentialSource(
                    Nocturne.Core.Models.Authorization.AuthType.LegacyJwt, subject, grant,
                    Scope.Normalize([Scope.GlucoseRead]), LimitTo24Hours: false, ExpiresAt: null),
            });
            return ValueTask.FromResult<object?>(null);
        });
        var next = await InvokeAsync(caller);

        foreach (var audit in new[]
                 {
                     next.GetRequiredService<IAuditContext>(),
                     upgrade.RequestServices.GetRequiredService<IAuditContext>(),
                 })
        {
            audit.SubjectId.Should().Be(subject, "the in-band credential, not the anonymous upgrade request, wrote");
            audit.AuthType.Should().Be("LegacyJwt");
            audit.TokenId.Should().Be(grant);
            audit.IpAddress.Should().Be("203.0.113.7");
        }
    }

    [Fact]
    public void One_invocation_runs_per_connection_at_a_time()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealTimeAndNotifications(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<HubOptions>>().Value.MaximumParallelInvocationsPerClient
            .Should().Be(1, "an in-band grant's reader and actor must reach every later call before it runs");
    }
}
