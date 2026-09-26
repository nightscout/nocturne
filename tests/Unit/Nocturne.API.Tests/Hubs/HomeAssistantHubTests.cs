using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Home Assistant acknowledges through the same decision as every other client, with the
/// credential its connection was admitted on, and no channel setting can widen or narrow that.
/// </summary>
[Trait("Category", "Unit")]
public class HomeAssistantHubTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static (HomeAssistantHub Hub, HubCallerContext Context) CreateHub(
        IAlertAcknowledgementService acknowledgementService, Guid subjectId, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(acknowledgementService)
                .BuildServiceProvider(),
        };
        httpContext.SetTenantContext(new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false));
        httpContext.SetAuthContext(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.OAuthAccessToken,
            SubjectId = subjectId,
        });
        httpContext.SetGrantedScopes(grantedScopes.ToHashSet());

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.Features).Returns(features);
        callerContext.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());
        callerContext.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);

        return (new HomeAssistantHub { Context = callerContext.Object }, callerContext.Object);
    }

    private static Mock<IAlertAcknowledgementService> Decides(Guid excursionId, AlertAcknowledgementOutcome outcome)
    {
        var service = new Mock<IAlertAcknowledgementService>();
        service
            .Setup(s => s.AcknowledgeExcursionAsync(
                Tenant, excursionId, "Kitchen", It.IsAny<AlertAcknowledgementAuthority>(), true,
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
        var service = Decides(excursionId, decided);
        var (hub, _) = CreateHub(service.Object, subjectId, grantedScope);

        var outcome = await hub.Acknowledge(excursionId, "Kitchen");

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
        var service = Decides(excursionId, AlertAcknowledgementOutcome.Muted);
        var (hub, context) = CreateHub(service.Object, Guid.NewGuid(), Scope.AlertsReadWrite);
        HubAuthorizationState.Grant(context, new HubAuthorization(
            Tenant, new HashSet<string> { Scope.DeviceNotify }, HubCredentialKind.Subject, admittedSubject,
            HistoryClamped: false));

        await hub.Acknowledge(excursionId, "Kitchen");

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
}
