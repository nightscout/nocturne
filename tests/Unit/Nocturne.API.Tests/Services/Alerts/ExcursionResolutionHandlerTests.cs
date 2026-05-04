using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class ExcursionResolutionHandlerTests
{
    private readonly Mock<IAlertRepository> _repository = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<IInAppNotificationService> _notification = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 4, 30, 14, 0, 0, TimeSpan.Zero));
    private readonly ExcursionResolutionHandler _sut;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _excursionId = Guid.NewGuid();

    public ExcursionResolutionHandlerTests()
    {
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _sut = new ExcursionResolutionHandler(
            _repository.Object,
            _broadcast.Object,
            _notification.Object,
            _timeProvider,
            NullLogger<ExcursionResolutionHandler>.Instance);
    }

    [Fact]
    public async Task HandleClosed_StampsResolutionReason_ExpiresDeliveries_AndBroadcasts()
    {
        var instanceId = Guid.NewGuid();
        var instance = new AlertInstanceSnapshot(instanceId, _tenantId, _excursionId,
            "escalating", DateTime.UtcNow, SnoozedUntil: null, SnoozeCount: 0);
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { instance });

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExpirePendingDeliveriesAsync(
            It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(instanceId)),
            It.IsAny<CancellationToken>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastAlertEventAsync("alert_resolved", It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task HandleClosed_HysteresisReason_MapsToHysteresisWireString()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.Hysteresis);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _excursionId, It.IsAny<DateTime>(), "hysteresis", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleClosed_NullCloseReason_PassesNullThrough()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        var transition = new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _excursionId, It.IsAny<DateTime>(), (string?)null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleClosed_NoInstances_SkipsExpireDeliveries()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.Verify(r => r.ExpirePendingDeliveriesAsync(
            It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleClosed_NonClosedTransition_NoOp()
    {
        var transition = new ExcursionTransition(ExcursionTransitionType.HysteresisStarted, _excursionId);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.VerifyNoOtherCalls();
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleClosed_AutoArchivesInAppNotificationsForEachRecipient()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "user-a", "user-b" });

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _notification.Verify(n => n.ArchiveBySourceAsync(
            "user-a",
            Nocturne.API.Services.Alerts.Providers.InAppProvider.NotificationType,
            _excursionId.ToString(),
            NotificationArchiveReason.ConditionMet,
            It.IsAny<CancellationToken>()), Times.Once);
        _notification.Verify(n => n.ArchiveBySourceAsync(
            "user-b",
            Nocturne.API.Services.Alerts.Providers.InAppProvider.NotificationType,
            _excursionId.ToString(),
            NotificationArchiveReason.ConditionMet,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleClosed_NoInAppRecipients_SkipsArchive()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _notification.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleClosed_BroadcastFailure_DoesNotPropagate()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _broadcast.Setup(b => b.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        var act = async () => await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
    }
}
