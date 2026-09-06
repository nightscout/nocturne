using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Pins the type narrowing on <see cref="NotificationsController.ExecuteAction"/>. Its
/// <c>[RequireScope]</c> gate accepts <c>device.notify</c> alongside <c>alerts.readwrite</c> so a
/// read-only member or the desktop Companion can acknowledge a firing alert, but the action it
/// guards is a dispatcher: <c>InAppNotificationService.ExecuteActionAsync</c> routes on the
/// notification's type to whichever <c>INotificationActionHandler</c> is registered, and those
/// handlers mutate their own domains — <c>TrackerSuggestionActionHandler</c>'s <c>accept</c>
/// completes and restarts tracker instances. So a caller admitted only by <c>device.notify</c>
/// reaches the dispatcher for an <c>alert.firing</c> notification and nothing else.
/// </summary>
[Trait("Category", "Unit")]
public class NotificationsControllerExecuteActionScopeTests
{
    /// <summary><c>TrackerSuggestionActionHandler.NotificationType</c>, a property rather than a
    /// constant, so it is repeated here to be usable in <see cref="InlineDataAttribute"/>.</summary>
    private const string TrackerSuggestionType = "tracker.suggested_match";

    private static readonly Guid Subject = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IInAppNotificationService> _notifications = new();

    /// <summary>
    /// The scopes a read-only member or the Companion's grant resolves to: <c>device.notify</c>
    /// admits it through the gate, and no alert write scope.
    /// </summary>
    private static readonly HashSet<string> DeviceNotifyOnly =
        [Scope.GlucoseRead, Scope.AlertsRead, Scope.DeviceNotify];

    private static readonly HashSet<string> AlertsWriter = [Scope.AlertsReadWrite];

    private NotificationsController CreateController(IReadOnlySet<string> grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Subject,
        };
        httpContext.Items["GrantedScopes"] = grantedScopes;

        return new NotificationsController(
            _notifications.Object,
            Mock.Of<ILogger<NotificationsController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private void NotificationIsOfType(Guid id, string type) =>
        _notifications
            .Setup(s => s.GetNotificationTypeAsync(id, Subject.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(type);

    private void ActionSucceeds(Guid id, string actionId) =>
        _notifications
            .Setup(s => s.ExecuteActionAsync(id, actionId, Subject.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

    [Fact]
    public async Task DeviceNotifyOnly_OnANonAlertNotification_IsForbiddenWithoutDispatching()
    {
        var id = Guid.CreateVersion7();
        NotificationIsOfType(id, TrackerSuggestionType);
        // Arranged to succeed, so a lost narrowing shows up as the dispatch it would allow rather
        // than as the 404 an unstubbed dispatcher returns.
        ActionSucceeds(id, "accept");

        var result = await CreateController(DeviceNotifyOnly).ExecuteAction(id, "accept");

        result.Should().BeOfType<ForbidResult>(
            "device.notify buys the excursion acknowledgement, not the tracker handler's accept");
        _notifications.Verify(
            s => s.ExecuteActionAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeviceNotifyOnly_OnAnAlertNotification_Dispatches()
    {
        var id = Guid.CreateVersion7();
        NotificationIsOfType(id, InAppProvider.NotificationType);
        ActionSucceeds(id, InAppProvider.AckActionId);

        var result = await CreateController(DeviceNotifyOnly)
            .ExecuteAction(id, InAppProvider.AckActionId);

        result.Should().BeOfType<NoContentResult>();
        _notifications.Verify(
            s => s.ExecuteActionAsync(
                id, InAppProvider.AckActionId, Subject.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeviceNotifyOnly_OnANotificationTheCallerDoesNotOwn_IsNotFound()
    {
        var id = Guid.CreateVersion7();
        _notifications
            .Setup(s => s.GetNotificationTypeAsync(id, Subject.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await CreateController(DeviceNotifyOnly).ExecuteAction(id, "accept");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(TrackerSuggestionType, "accept")]
    [InlineData(InAppProvider.NotificationType, InAppProvider.AckActionId)]
    public async Task AlertsReadWrite_DispatchesEveryNotificationType(string type, string actionId)
    {
        var id = Guid.CreateVersion7();
        NotificationIsOfType(id, type);
        ActionSucceeds(id, actionId);

        var result = await CreateController(AlertsWriter).ExecuteAction(id, actionId);

        result.Should().BeOfType<NoContentResult>();
        _notifications.Verify(
            s => s.ExecuteActionAsync(id, actionId, Subject.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);
        _notifications.Verify(
            s => s.GetNotificationTypeAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the narrowing is only for a caller the capability scope admitted");
    }
}
