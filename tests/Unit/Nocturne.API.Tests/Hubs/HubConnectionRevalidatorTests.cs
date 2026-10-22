using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// The sweep closes a connection whose credential no longer covers what it connected with, and
/// one connection it cannot check, or cannot close, never stops it checking the rest.
/// </summary>
[Trait("Category", "Unit")]
public class HubConnectionRevalidatorTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private readonly HubConnectionRevalidator _revalidator = new(
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        TimeProvider.System,
        NullLogger<HubConnectionRevalidator>.Instance);

    private static HubAuthorization Member(bool historyClamped = false, params string[] scopes) => new(
        Tenant, Scope.Normalize(scopes), HubCredentialKind.Subject, Subject, historyClamped);

    /// <summary>A credential proven without any server-side read, so it stays covered.</summary>
    private static HubAuthorization InstanceKey() =>
        new(Tenant, new HashSet<string> { Scope.FullAccess }, HubCredentialKind.Infrastructure, null, false)
        {
            Source = new HubCredentialSource(
                AuthType.InstanceKey, null, null, new HashSet<string> { Scope.FullAccess }, false, null),
        };

    private static Mock<HubCallerContext> Connection(string connectionId, HubAuthorization? granted = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        httpContext.SetTenantContext(new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false));
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.Features).Returns(features);
        context.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());
        if (granted is not null)
            HubAuthorizationState.Grant(context.Object, granted);
        return context;
    }

    private Mock<HubCallerContext> Tracked(string connectionId, HubAuthorization granted)
    {
        var connection = Connection(connectionId, granted);
        _revalidator.Track(connection.Object);
        return connection;
    }

    [Fact]
    public async Task A_connection_still_covered_stays_open_and_one_that_cannot_be_proven_is_closed()
    {
        var covered = Tracked("covered", InstanceKey());
        var unprovable = Tracked("unprovable", Member(false, Scope.GlucoseRead));

        await _revalidator.RevalidateAsync(CancellationToken.None);

        covered.Verify(c => c.Abort(), Times.Never);
        unprovable.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task A_closed_connection_is_checked_no_further()
    {
        var connection = Tracked("conn", Member(false, Scope.GlucoseRead));

        await _revalidator.RevalidateAsync(CancellationToken.None);
        await _revalidator.RevalidateAsync(CancellationToken.None);

        connection.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task A_connection_that_throws_does_not_stop_the_sweep()
    {
        var throwing = new Mock<HubCallerContext>();
        throwing.SetupGet(c => c.ConnectionId).Returns("throwing");
        throwing.SetupGet(c => c.Items).Throws(new InvalidOperationException());
        _revalidator.Track(throwing.Object);
        var next = Tracked("next", Member(false, Scope.GlucoseRead));

        await _revalidator.RevalidateAsync(CancellationToken.None);

        next.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task A_connection_whose_abort_throws_does_not_stop_the_sweep()
    {
        var failing = Tracked("failing", Member(false, Scope.GlucoseRead));
        failing.Setup(c => c.Abort()).Throws(new ObjectDisposedException("connection"));
        var next = Tracked("next", Member(false, Scope.GlucoseRead));

        await _revalidator.RevalidateAsync(CancellationToken.None);

        next.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task An_already_closed_connection_is_forgotten_unchecked()
    {
        var closed = Connection("closed", Member(false, Scope.GlucoseRead));
        closed.SetupGet(c => c.ConnectionAborted).Returns(new CancellationToken(canceled: true));
        _revalidator.Track(closed.Object);

        await _revalidator.RevalidateAsync(CancellationToken.None);

        closed.Verify(c => c.Abort(), Times.Never);
    }

    public static TheoryData<string, HubAuthorization?> Withdrawn => new()
    {
        { "refused", null },
        { "narrowed", Member(false, Scope.GlucoseRead) },
        { "newly clamped", Member(true, Scope.GlucoseRead, Scope.TreatmentsRead) },
        { "another subject", Member(false, Scope.GlucoseRead, Scope.TreatmentsRead) with { SubjectId = Guid.NewGuid() } },
        { "another kind", Member(false, Scope.GlucoseRead, Scope.TreatmentsRead) with { Kind = HubCredentialKind.Restricted } },
        { "another client", Member(false, Scope.GlucoseRead, Scope.TreatmentsRead) with { ClientId = "other" } },
    };

    [Theory]
    [MemberData(nameof(Withdrawn))]
    public void A_credential_that_no_longer_grants_what_it_did_does_not_cover_the_connection(
        string because, HubAuthorization? current)
    {
        Member(false, Scope.GlucoseRead, Scope.TreatmentsRead).IsCoveredBy(current).Should().BeFalse(because);
    }

    public static TheoryData<string, HubAuthorization, HubAuthorization> StillCovered => new()
    {
        { "unchanged", Member(false, Scope.GlucoseRead), Member(false, Scope.GlucoseRead) },
        { "widened", Member(false, Scope.GlucoseRead), Member(false, Scope.GlucoseRead, Scope.DevicesRead) },
        { "clamp lifted", Member(true, Scope.GlucoseRead), Member(false, Scope.GlucoseRead) },
    };

    [Theory]
    [MemberData(nameof(StillCovered))]
    public void A_credential_that_still_grants_what_it_did_covers_the_connection(
        string because, HubAuthorization granted, HubAuthorization current)
    {
        granted.IsCoveredBy(current).Should().BeTrue(because);
    }

    private static HubInvocationContext Invocation(HubCallerContext context) => new(
        context,
        Mock.Of<IServiceProvider>(),
        hub: new StubHub(),
        hubMethod: typeof(AlertHub).GetMethod(nameof(AlertHub.Subscribe), BindingFlags.Public | BindingFlags.Instance)!,
        hubMethodArguments: []);

    private sealed class StubHub : Hub;

    [Fact]
    public async Task The_filter_hands_an_authorized_connection_to_the_revalidator_and_takes_it_back_on_disconnect()
    {
        var filter = new HubAuthorizationFilter(_revalidator);
        var connection = Connection("conn-1", Member(false, Scope.AlertsRead));

        await filter.InvokeMethodAsync(Invocation(connection.Object), _ => ValueTask.FromResult<object?>(null));
        await _revalidator.RevalidateAsync(CancellationToken.None);

        connection.Verify(c => c.Abort(), Times.Once);

        var disconnected = Connection("conn-2", Member(false, Scope.AlertsRead));
        await filter.InvokeMethodAsync(Invocation(disconnected.Object), _ => ValueTask.FromResult<object?>(null));
        await filter.OnDisconnectedAsync(
            new HubLifetimeContext(disconnected.Object, Mock.Of<IServiceProvider>(), new StubHub()),
            null,
            (_, _) => Task.CompletedTask);
        await _revalidator.RevalidateAsync(CancellationToken.None);

        disconnected.Verify(c => c.Abort(), Times.Never);
    }

    [Fact]
    public async Task The_filter_does_not_hand_over_a_connection_that_has_already_closed()
    {
        var filter = new HubAuthorizationFilter(_revalidator);
        var closing = Connection("closing", Member(false, Scope.AlertsRead));
        var aborted = new CancellationTokenSource();
        closing.SetupGet(c => c.ConnectionAborted).Returns(aborted.Token);

        await filter.InvokeMethodAsync(Invocation(closing.Object), _ =>
        {
            aborted.Cancel();
            return ValueTask.FromResult<object?>(null);
        });
        closing.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);
        await _revalidator.RevalidateAsync(CancellationToken.None);

        closing.Verify(c => c.Abort(), Times.Never);
    }
}
