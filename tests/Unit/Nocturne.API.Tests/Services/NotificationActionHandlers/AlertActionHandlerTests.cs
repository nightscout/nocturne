using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.NotificationActionHandlers;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.NotificationActionHandlers;

[Trait("Category", "Unit")]
public class AlertActionHandlerTests
{
    private readonly Mock<IAlertAcknowledgementService> _ack = new();
    private readonly Mock<IInAppNotificationService> _notification = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly AlertActionHandler _sut;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _excursionId = Guid.NewGuid();
    private readonly Guid _notificationId = Guid.NewGuid();

    public AlertActionHandlerTests()
    {
        _tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);

        _sut = new AlertActionHandler(
            _ack.Object,
            _notification.Object,
            _tenantAccessor.Object,
            NullLogger<AlertActionHandler>.Instance);
    }

    [Fact]
    public void NotificationType_IsAlertFiringDiscriminator()
    {
        _sut.NotificationType.Should().Be(InAppProvider.NotificationType);
    }

    [Fact]
    public async Task Ack_AcknowledgesExcursionAndArchivesNotification()
    {
        var ok = await _sut.HandleAsync(
            _notificationId,
            InAppProvider.AckActionId,
            "user-123",
            _excursionId.ToString(),
            metadata: null);

        ok.Should().BeTrue();

        _ack.Verify(a => a.AcknowledgeExcursionAsync(
            _tenantId, _excursionId, "user:user-123", true, It.IsAny<CancellationToken>()), Times.Once);
        _notification.Verify(n => n.ArchiveNotificationAsync(
            _notificationId, NotificationArchiveReason.Completed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dismiss_ArchivesNotificationOnly()
    {
        var ok = await _sut.HandleAsync(
            _notificationId,
            InAppProvider.DismissActionId,
            "user-123",
            _excursionId.ToString(),
            metadata: null);

        ok.Should().BeTrue();

        _ack.VerifyNoOtherCalls();
        _notification.Verify(n => n.ArchiveNotificationAsync(
            _notificationId, NotificationArchiveReason.Dismissed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ack_NoTenantContext_ReturnsFalseAndDoesNotAck()
    {
        _tenantAccessor.Setup(t => t.IsResolved).Returns(false);

        var ok = await _sut.HandleAsync(
            _notificationId,
            InAppProvider.AckActionId,
            "user-123",
            _excursionId.ToString(),
            metadata: null);

        ok.Should().BeFalse();
        _ack.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvalidSourceId_ReturnsFalse()
    {
        var ok = await _sut.HandleAsync(
            _notificationId,
            InAppProvider.AckActionId,
            "user-123",
            sourceId: "not-a-guid",
            metadata: null);

        ok.Should().BeFalse();
        _ack.VerifyNoOtherCalls();
        _notification.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownAction_ReturnsFalse()
    {
        var ok = await _sut.HandleAsync(
            _notificationId,
            "snooze",
            "user-123",
            _excursionId.ToString(),
            metadata: null);

        ok.Should().BeFalse();
        _ack.VerifyNoOtherCalls();
        _notification.VerifyNoOtherCalls();
    }
}
