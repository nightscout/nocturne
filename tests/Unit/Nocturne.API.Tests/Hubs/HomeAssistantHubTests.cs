using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// A Home Assistant connection subscribes as the OAuth client its token was issued to and receives
/// only that instance's dispatches; an instance counts as connected only while a connection of it
/// lives; and it acknowledges through the same decision as every other client, with the credential
/// and the principal its connection was admitted on.
/// </summary>
[Trait("Category", "Unit")]
public class HomeAssistantHubTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class SharedInMemoryFactory(string dbName) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
            ctx.TenantId = Tenant;
            return ctx;
        }
    }

    private readonly IDbContextFactory<NocturneDbContext> _contexts =
        new SharedInMemoryFactory($"ha_hub_{Guid.NewGuid()}");

    private readonly HomeAssistantConnections _connections = new();

    private sealed record Connection(
        HomeAssistantHub Hub,
        HubCallerContext Context,
        List<string> JoinedGroups,
        List<object?[]> SentToCaller);

    private Connection Connect(
        string? clientId,
        string[] grantedScopes,
        string connectionId = "conn-1",
        IAlertAcknowledgementService? acknowledgementService = null,
        Guid? subjectId = null,
        ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(acknowledgementService ?? Mock.Of<IAlertAcknowledgementService>())
                .AddSingleton(_contexts.AsNocturneFactory())
                .BuildServiceProvider(),
        };
        httpContext.SetTenantContext(new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false));
        httpContext.SetAuthContext(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.OAuthAccessToken,
            SubjectId = subjectId ?? Guid.NewGuid(),
            ClientId = clientId,
        });
        httpContext.SetGrantedScopes(Scope.Normalize(grantedScopes));

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.ConnectionId).Returns(connectionId);
        callerContext.SetupGet(c => c.Features).Returns(features);
        callerContext.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());
        callerContext.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);
        callerContext.SetupGet(c => c.User).Returns(user);

        var joined = new List<string>();
        var groups = new Mock<IGroupManager>();
        groups
            .Setup(g => g.AddToGroupAsync(connectionId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, group, _) => joined.Add(group))
            .Returns(Task.CompletedTask);

        var sent = new List<object?[]>();
        var caller = new Mock<ISingleClientProxy>();
        caller
            .Setup(c => c.SendCoreAsync("alert_dispatch", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => sent.Add(args))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(c => c.Caller).Returns(caller.Object);

        var hub = new HomeAssistantHub(_connections)
        {
            Context = callerContext.Object,
            Groups = groups.Object,
            Clients = clients.Object,
        };
        return new Connection(hub, callerContext.Object, joined, sent);
    }

    private static string Group(string name) => TenantAwareHub.FormatTenantGroup(Tenant.ToString(), name);

    private async Task<Guid> SeedFailedDeliveryAsync(string destination)
    {
        await using var db = await _contexts.CreateDbContextAsync();
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertRuleId = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow.AddMinutes(-20),
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertExcursionId = excursion.Id,
            TriggeredAt = excursion.StartedAt,
        };
        var delivery = new AlertDeliveryEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertInstanceId = instance.Id,
            ChannelType = ChannelType.HomeAssistant,
            Destination = destination,
            Payload = JsonSerializer.Serialize(new AlertPayload
            {
                AlertType = AlertConditionType.Threshold,
                RuleName = "Low",
                Severity = AlertRuleSeverity.Warning,
                TenantId = Tenant,
                GlucoseValue = 62,
                Trend = null,
                TrendRate = null,
                ReadingTimestamp = DateTime.UtcNow,
                SubjectName = "Synthetic",
                ExcursionId = excursion.Id,
                InstanceId = instance.Id,
                ActiveExcursionCount = 1,
            }),
            Status = "failed",
        };
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        db.AlertDeliveries.Add(delivery);
        await db.SaveChangesAsync();
        return delivery.Id;
    }

    private async Task<string> StatusOfAsync(Guid deliveryId)
    {
        await using var db = await _contexts.CreateDbContextAsync();
        return (await db.AlertDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Id == deliveryId)).Status;
    }

    [Theory]
    [InlineData("kitchen-ha", "garage-ha")]
    [InlineData(null, "garage-ha")]
    [InlineData("kitchen-ha", "")]
    public async Task Subscribe_refuses_any_instance_but_the_client_the_token_was_issued_to(
        string? clientId, string instanceId)
    {
        var connection = Connect(clientId, [Scope.AlertsRead, Scope.GlucoseRead]);

        var subscribe = () => connection.Hub.Subscribe(instanceId);

        await subscribe.Should().ThrowAsync<HubException>();
        connection.JoinedGroups.Should().BeEmpty();
        _connections.IsConnected(Tenant, instanceId).Should().BeFalse();
    }

    [Fact]
    public async Task Subscribe_replays_only_the_connections_own_failed_deliveries()
    {
        var own = await SeedFailedDeliveryAsync("kitchen-ha");
        var other = await SeedFailedDeliveryAsync("garage-ha");
        var connection = Connect("kitchen-ha", [Scope.AlertsRead]);

        await connection.Hub.Subscribe("kitchen-ha");

        connection.SentToCaller.Should().ContainSingle();
        (await StatusOfAsync(own)).Should().Be("delivered");
        (await StatusOfAsync(other)).Should().Be("failed");
        connection.JoinedGroups.Should().Contain(Group("ha:kitchen-ha")).And.NotContain(Group("ha:garage-ha"));
    }

    [Fact]
    public async Task An_instance_is_connected_only_while_one_of_its_connections_lives()
    {
        var first = Connect("kitchen-ha", [Scope.AlertsRead], connectionId: "conn-1");
        var second = Connect("kitchen-ha", [Scope.AlertsRead], connectionId: "conn-2");

        await first.Hub.Subscribe("kitchen-ha");
        await first.Hub.Subscribe("kitchen-ha");
        await second.Hub.Subscribe("kitchen-ha");

        await first.Hub.OnDisconnectedAsync(null);
        _connections.IsConnected(Tenant, "kitchen-ha").Should().BeTrue();

        await second.Hub.OnDisconnectedAsync(null);
        _connections.IsConnected(Tenant, "kitchen-ha").Should().BeFalse();
    }

    [Theory]
    [InlineData(false, new[] { Scope.AlertsRead })]
    [InlineData(true, new[] { Scope.AlertsRead, Scope.GlucoseRead })]
    public async Task The_glucose_relay_is_joined_only_by_a_credential_that_reads_glucose(
        bool joinsRelay, string[] scopes)
    {
        var connection = Connect("kitchen-ha", scopes);

        await connection.Hub.Subscribe("kitchen-ha");

        connection.JoinedGroups.Contains(Group("ha-glucose")).Should().Be(joinsRelay);
        connection.JoinedGroups.Should().Contain(Group("ha-alerts"));
    }

    private static Mock<IAlertAcknowledgementService> Decides(
        Guid excursionId, string acknowledgedBy, AlertAcknowledgementOutcome outcome)
    {
        var service = new Mock<IAlertAcknowledgementService>();
        service
            .Setup(s => s.AcknowledgeExcursionAsync(
                Tenant, excursionId, acknowledgedBy, It.IsAny<AlertAcknowledgementAuthority>(), true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        return service;
    }

    [Theory]
    [InlineData(AlertAcknowledgementOutcome.Muted, Scope.DeviceNotify)]
    [InlineData(AlertAcknowledgementOutcome.Acknowledged, Scope.AlertsReadWrite)]
    public async Task Acknowledge_hands_the_connections_own_authority_to_the_decision_and_returns_its_outcome(
        AlertAcknowledgementOutcome decided, string grantedScope)
    {
        var excursionId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var service = Decides(excursionId, "Kitchen", decided);
        var connection = Connect(
            "kitchen-ha", [grantedScope], acknowledgementService: service.Object, subjectId: subjectId);

        var outcome = await connection.Hub.Acknowledge(excursionId, "Kitchen");

        outcome.Should().Be(decided);
        service.Verify(
            s => s.AcknowledgeExcursionAsync(
                Tenant,
                excursionId,
                "Kitchen",
                It.Is<AlertAcknowledgementAuthority>(a =>
                    a.SubjectId == subjectId && a.GrantedScopes.SetEquals(new[] { grantedScope })),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Acknowledge_judges_the_authorization_the_connection_was_admitted_on_not_the_raw_request()
    {
        var excursionId = Guid.NewGuid();
        var admittedSubject = Guid.NewGuid();
        var service = Decides(excursionId, "Kitchen", AlertAcknowledgementOutcome.Muted);
        var connection = Connect("kitchen-ha", [Scope.AlertsReadWrite], acknowledgementService: service.Object);
        HubAuthorizationState.Grant(connection.Context, new HubAuthorization(
            Tenant, new HashSet<string> { Scope.DeviceNotify }, HubCredentialKind.Subject, admittedSubject,
            HistoryClamped: false));

        await connection.Hub.Acknowledge(excursionId, "Kitchen");

        service.Verify(
            s => s.AcknowledgeExcursionAsync(
                Tenant,
                excursionId,
                "Kitchen",
                It.Is<AlertAcknowledgementAuthority>(a =>
                    a.SubjectId == admittedSubject && a.GrantedScopes.SetEquals(new[] { Scope.DeviceNotify })),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Acknowledge_records_the_authenticated_principal_over_the_name_the_caller_supplies()
    {
        var excursionId = Guid.NewGuid();
        var service = Decides(excursionId, "Alice", AlertAcknowledgementOutcome.Acknowledged);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Alice")], "Nocturne"));
        var connection = Connect(
            "kitchen-ha", [Scope.AlertsReadWrite], acknowledgementService: service.Object, user: user);

        var outcome = await connection.Hub.Acknowledge(excursionId, "Bob");

        outcome.Should().Be(AlertAcknowledgementOutcome.Acknowledged);
    }
}
