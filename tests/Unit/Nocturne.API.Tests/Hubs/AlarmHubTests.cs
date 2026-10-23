using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// A subscriber to the alarm hub hears the alarm still sounding once it has joined the room, and a
/// failed replay never fails the subscribe. An ack goes through <see cref="IAlarmAckHandler"/>.
/// </summary>
[Trait("Category", "Unit")]
public class AlarmHubTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string InstanceKeyHash = "instance-key-hash";

    private static readonly EngineAlarm Sounding = new(EngineAlarmEvents.UrgentAlarm, new NotificationBase
    {
        Level = 2,
        Title = "Urgent low",
        Group = "0192a3b4-0000-7000-8000-000000000001",
    });

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private readonly Mock<IEngineAlarmRelay> _relay = new();
    private readonly Mock<IAlarmAckHandler> _ackHandler = new();
    private readonly Mock<IHubTokenAuthorizer> _tokenAuthorizer = new();
    private readonly List<string> _calls = [];
    private readonly List<(string Method, object?[] Args)> _sentToCaller = [];

    private static HubAuthorization Authorization(HubCredentialKind kind) => new(
        Tenant,
        Scope.Normalize([Scope.FullAccess]),
        kind,
        kind == HubCredentialKind.Subject ? Guid.NewGuid() : null,
        HistoryClamped: false);

    private static string AlarmRoom => TenantAwareHub.FormatTenantGroup(Tenant.ToString(), "alarm-subscribers");

    private AlarmHub Hub(HubAuthorization? granted = null)
    {
        _tokenAuthorizer
            .Setup(a => a.AuthorizeInstanceKey(InstanceKeyHash, Tenant))
            .Returns(Authorization(HubCredentialKind.Infrastructure));

        var httpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        httpContext.SetTenantContext(new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false));
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("bridge");
        context.SetupGet(c => c.Features).Returns(features);
        context.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());
        context.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);
        if (granted is not null) HubAuthorizationState.Grant(context.Object, granted);

        var groups = new Mock<IGroupManager>();
        groups
            .Setup(g => g.AddToGroupAsync("bridge", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, group, _) => _calls.Add($"join {group}"))
            .Returns(Task.CompletedTask);

        var caller = new Mock<ISingleClientProxy>();
        caller
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) =>
            {
                _calls.Add($"send {method}");
                _sentToCaller.Add((method, args));
            })
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(c => c.Caller).Returns(caller.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());

        return new AlarmHub(NullLogger<AlarmHub>.Instance, _tokenAuthorizer.Object, _ackHandler.Object, _relay.Object)
        {
            Context = context.Object,
            Groups = groups.Object,
            Clients = clients.Object,
        };
    }

    private static bool Succeeded(object result) =>
        (bool)result.GetType().GetProperty("success")!.GetValue(result)!;

    [Fact]
    public async Task A_subscriber_hears_the_sounding_alarm_after_it_joins_the_room()
    {
        _relay.Setup(r => r.HighestAlarmingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Sounding);

        var result = await Hub().Subscribe(new AlarmSubscribeRequest { Secret = InstanceKeyHash });

        Succeeded(result).Should().BeTrue();
        _calls.Should().Equal($"join {AlarmRoom}", $"send {EngineAlarmEvents.UrgentAlarm}");
        _sentToCaller.Single().Args.Should().Equal(Sounding.Notify);
    }

    [Fact]
    public async Task A_subscriber_hears_nothing_when_nothing_sounds()
    {
        _relay.Setup(r => r.HighestAlarmingAsync(It.IsAny<CancellationToken>())).ReturnsAsync((EngineAlarm?)null);

        var result = await Hub().Subscribe(new AlarmSubscribeRequest { Secret = InstanceKeyHash });

        Succeeded(result).Should().BeTrue();
        _sentToCaller.Should().BeEmpty();
    }

    public static TheoryData<Exception> ReplayFailures => new()
    {
        new InvalidOperationException("database down"),
        new OperationCanceledException(),
    };

    [Theory]
    [MemberData(nameof(ReplayFailures))]
    public async Task A_replay_that_fails_still_reports_the_subscribe_as_successful(Exception failure)
    {
        _relay.Setup(r => r.HighestAlarmingAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await Hub().Subscribe(new AlarmSubscribeRequest { Secret = InstanceKeyHash });

        Succeeded(result).Should().BeTrue();
        _calls.Should().Equal($"join {AlarmRoom}");
    }

    [Fact]
    public async Task The_bridge_is_told_the_sounding_alarm_for_a_socket_that_subscribes_later()
    {
        _relay.Setup(r => r.HighestAlarmingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Sounding);

        var current = await Hub(Authorization(HubCredentialKind.Infrastructure)).CurrentAlarm();

        current.Should().BeSameAs(Sounding.Notify);
    }

    [Fact]
    public async Task Only_the_bridge_may_ask_for_the_sounding_alarm()
    {
        _relay.Setup(r => r.HighestAlarmingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Sounding);

        var attempt = () => Hub(Authorization(HubCredentialKind.Subject)).CurrentAlarm();

        await attempt.Should().ThrowAsync<HubException>();
        _relay.Verify(r => r.HighestAlarmingAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("0192a3b4-0000-7000-8000-000000000001")]
    [InlineData("default")]
    public async Task An_ack_goes_through_the_alarm_ack_handler(string group)
    {
        _ackHandler
            .Setup(h => h.AckAsync(2, group, 1_800_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationAckResponse { Success = true, Message = "snoozed", Timestamp = 1 });

        await Hub(Authorization(HubCredentialKind.Infrastructure)).Ack(2, group, 1_800_000);

        _ackHandler.Verify(h => h.AckAsync(2, group, 1_800_000, It.IsAny<CancellationToken>()), Times.Once);
        _sentToCaller.Should().ContainSingle(s => s.Method == "ackConfirm");
    }
}
