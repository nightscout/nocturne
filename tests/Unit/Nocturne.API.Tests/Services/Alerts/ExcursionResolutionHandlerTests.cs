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
    private readonly Mock<IEngineAlarmRelay> _alarmRelay = new();
    private readonly Mock<IInAppNotificationService> _notification = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 4, 30, 14, 0, 0, TimeSpan.Zero));
    private readonly ExcursionResolutionHandler _sut;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _excursionId = Guid.NewGuid();
    private readonly Guid _ruleId = Guid.NewGuid();

    public ExcursionResolutionHandlerTests()
    {
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionRuleSnapshot(_ruleId, "Urgent Low"));

        _sut = new ExcursionResolutionHandler(
            _repository.Object,
            _broadcast.Object,
            _notification.Object,
            _alarmRelay.Object,
            _timeProvider,
            NullLogger<ExcursionResolutionHandler>.Instance);
    }

    [Fact]
    public async Task HandleClosed_StampsResolutionReason_ExpiresDeliveries_AndBroadcasts()
    {
        var instanceId = Guid.NewGuid();
        var instance = new AlertInstanceSnapshot(instanceId, _tenantId, _excursionId,
            "escalating", DateTime.UtcNow, SnoozedUntil: null, SnoozeCount: 0);
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { instance });

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _tenantId, _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExpirePendingDeliveriesAsync(
            _tenantId,
            It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(instanceId)),
            It.IsAny<CancellationToken>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()), Times.Once);
        _alarmRelay.Verify(
            r => r.ClearIfQuietAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { _excursionId })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleClosed_BroadcastCarriesTheExcursionRuleAndReason()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        AlertResolvedEvent? sent = null;
        _broadcast.Setup(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()))
            .Callback<AlertResolvedEvent>(e => sent = e)
            .Returns(Task.CompletedTask);

        await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.Hysteresis),
            _tenantId,
            CancellationToken.None);

        sent.Should().BeEquivalentTo(new AlertResolvedEvent
        {
            TenantId = _tenantId,
            ExcursionId = _excursionId,
            AlertRuleId = _ruleId,
            RuleName = "Urgent Low",
            ResolvedAt = _timeProvider.GetUtcNow().UtcDateTime,
            Reason = ExcursionCloseReason.Hysteresis,
        });
    }

    [Fact]
    public async Task HandleClosed_ExcursionWithoutRule_ResolvesWithoutBroadcasting()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExcursionRuleSnapshot?)null);

        await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve),
            _tenantId,
            CancellationToken.None);

        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _tenantId, _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()), Times.Never);
    }

    [Fact]
    public async Task HandleClosed_RuleLookupFailure_StillResolvesAndClears()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = async () => await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve),
            _tenantId,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _tenantId, _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.DeleteExcursionMutesAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()), Times.Once);
        _alarmRelay.Verify(
            r => r.ClearIfQuietAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()), Times.Never);
    }

    [Fact]
    public async Task HandleClosed_DeletesEveryMembersMuteOfTheExcursion()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve),
            _tenantId,
            CancellationToken.None);

        _repository.Verify(r => r.DeleteExcursionMutesAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleClosed_HysteresisReason_MapsToHysteresisWireString()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.Hysteresis);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _tenantId, _excursionId, It.IsAny<DateTime>(), "hysteresis", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleClosed_NullCloseReason_PassesNullThrough()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        var transition = new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _tenantId, _excursionId, It.IsAny<DateTime>(), (string?)null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleClosed_NoInstances_SkipsExpireDeliveries()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _repository.Verify(r => r.ExpirePendingDeliveriesAsync(
            _tenantId, It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
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
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        _notification.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleClosed_BroadcastFailure_DoesNotPropagate()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_tenantId, _excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _broadcast.Setup(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        var act = async () => await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _tenantId, _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
    }
}
