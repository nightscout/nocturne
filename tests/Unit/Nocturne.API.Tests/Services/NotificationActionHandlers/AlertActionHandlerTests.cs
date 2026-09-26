using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.NotificationActionHandlers;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.API.Tests.Services.NotificationActionHandlers;

/// <summary>
/// The Acknowledge action on an in-app alert notification reaches the one acknowledgement decision
/// with the request's own authority, so it cannot acknowledge for everyone on behalf of a member who
/// could only mute from the HTTP endpoint.
/// </summary>
[Trait("Category", "Unit")]
public class AlertActionHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly Mock<IAlertAcknowledgementService> _acknowledgement = new();
    private readonly Mock<IHttpContextAccessor> _httpContextAccessor = new();

    private AlertActionHandler CreateHandler() => new(
        _acknowledgement.Object,
        MockTenantAccessor.Create(Tenant).Object,
        _httpContextAccessor.Object,
        NullLogger<AlertActionHandler>.Instance);

    private void CallerIs(Guid subjectId, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetAuthContext(new AuthContext { IsAuthenticated = true, SubjectId = subjectId });
        httpContext.SetGrantedScopes(grantedScopes.ToHashSet());
        _httpContextAccessor.SetupGet(a => a.HttpContext).Returns(httpContext);
    }

    [Theory]
    [InlineData(AlertAcknowledgementOutcome.Muted, Scope.DeviceNotify)]
    [InlineData(AlertAcknowledgementOutcome.Acknowledged, Scope.AlertsReadWrite)]
    public async Task Ack_passes_the_callers_authority_to_the_decision(
        AlertAcknowledgementOutcome decided, string grantedScope)
    {
        var excursionId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        CallerIs(subjectId, grantedScope);
        _acknowledgement
            .Setup(s => s.AcknowledgeExcursionAsync(
                Tenant, excursionId, It.IsAny<string>(), It.IsAny<AlertAcknowledgementAuthority>(), true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(decided);

        var result = await CreateHandler().HandleAsync(
            Guid.NewGuid(), InAppProvider.AckActionId, subjectId.ToString(), excursionId.ToString(), null);

        result.Should().Be(NotificationActionResult.Completed);
        _acknowledgement.Verify(
            s => s.AcknowledgeExcursionAsync(
                Tenant,
                excursionId,
                $"user:{subjectId}",
                It.Is<AlertAcknowledgementAuthority>(a =>
                    a.SubjectId == subjectId && a.GrantedScopes.SetEquals(new[] { grantedScope })),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Ack_without_a_request_is_not_handled_rather_than_acknowledged_for_everyone()
    {
        _httpContextAccessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);

        var result = await CreateHandler().HandleAsync(
            Guid.NewGuid(), InAppProvider.AckActionId, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), null);

        result.Should().Be(NotificationActionResult.NotHandled);
        _acknowledgement.Verify(
            s => s.AcknowledgeExcursionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<AlertAcknowledgementAuthority>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
