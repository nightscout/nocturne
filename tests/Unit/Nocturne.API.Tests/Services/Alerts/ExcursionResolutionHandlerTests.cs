using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
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
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_excursionId, It.IsAny<CancellationToken>()))
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
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
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
    public async Task HandleClosed_NoticeThatARuleCannotBeEvaluated_IsResolvedOnlyToTheHomeAssistantInstancesItReached()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionRuleSnapshot(_ruleId, "Urgent Low", IsUnevaluableNotice: true));
        _repository.Setup(r => r.GetHomeAssistantInstancesSentExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["kitchen-ha"]);

        await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.Manual),
            _tenantId,
            CancellationToken.None);

        _broadcast.Verify(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()), Times.Never,
            "the alerts hub's subscribers were never sent the notice");
        _broadcast.Verify(b => b.BroadcastAlertResolvedToHomeAssistantAsync(
            It.Is<AlertResolvedEvent>(e => e.ExcursionId == _excursionId && e.AlertRuleId == _ruleId),
            It.Is<IReadOnlyCollection<string>>(i => i.SequenceEqual(new[] { "kitchen-ha" }))), Times.Once);
        _notification.Verify(n => n.ArchiveBySourceAsync(
            It.IsAny<string>(), "alert.firing", It.IsAny<string>(), It.IsAny<NotificationArchiveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleClosed_ExcursionWithoutRule_ResolvesWithoutBroadcasting()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExcursionRuleSnapshot?)null);

        await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve),
            _tenantId,
            CancellationToken.None);

        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()), Times.Never);
    }

    [Fact]
    public async Task HandleClosed_RuleLookupFailure_StillResolvesAndClears()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("db down"));

        var act = async () => await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve),
            _tenantId,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.DeleteExcursionMutesAsync(_excursionId, It.IsAny<CancellationToken>()), Times.Once);
        _alarmRelay.Verify(
            r => r.ClearIfQuietAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()), Times.Never);
    }

    [Fact]
    public async Task HandleClosed_RuleLookupFailure_ArchivesEveryAlertKindOfInAppNotificationAndNoOther()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("db down"));
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["member"]);

        await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.Manual),
            _tenantId,
            CancellationToken.None);

        string[] alertTypes = ["alert.firing", "alert.unevaluable", "alert.held"];
        foreach (var type in alertTypes)
        {
            _notification.Verify(n => n.ArchiveBySourceAsync(
                "member", type, _excursionId.ToString(), NotificationArchiveReason.ConditionMet,
                It.IsAny<CancellationToken>()), Times.Once, "a notice's notification is cleared even when its rule cannot be read");
        }
        _notification.Verify(n => n.ArchiveBySourceAsync(
            It.IsAny<string>(), It.Is<string>(type => !alertTypes.Contains(type)), It.IsAny<string>(),
            It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleClosed_DeletesEveryMembersMuteOfTheExcursion()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        await _sut.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve),
            _tenantId,
            CancellationToken.None);

        _repository.Verify(r => r.DeleteExcursionMutesAsync(_excursionId, It.IsAny<CancellationToken>()), Times.Once);
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
            InAppNotificationType.AlertFiring.ToWireString(),
            _excursionId.ToString(),
            NotificationArchiveReason.ConditionMet,
            It.IsAny<CancellationToken>()), Times.Once);
        _notification.Verify(n => n.ArchiveBySourceAsync(
            "user-b",
            InAppNotificationType.AlertFiring.ToWireString(),
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
        _broadcast.Setup(b => b.BroadcastAlertResolvedAsync(It.IsAny<AlertResolvedEvent>()))
            .ThrowsAsync(new HttpRequestException("hub down"));

        var transition = new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve);

        var act = async () => await _sut.HandleClosedAsync(transition, _tenantId, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _repository.Verify(r => r.ResolveInstancesForExcursionAsync(
            _excursionId, It.IsAny<DateTime>(), "auto", It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<Exception> Defects => new()
    {
        new InvalidOperationException("a second operation was started on this context"),
        new NullReferenceException(),
    };

    private ExcursionTransition Closed() =>
        new(ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.Manual);

    [Theory]
    [MemberData(nameof(Defects))]
    public async Task HandleClosed_ADefectReadingTheRule_Propagates(Exception defect)
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetExcursionRuleAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(defect);

        var act = () => _sut.HandleClosedAsync(Closed(), _tenantId, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(defect);
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public async Task HandleClosed_ADefectLoadingTheInAppDestinations_Propagates(Exception defect)
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(defect);

        var act = () => _sut.HandleClosedAsync(Closed(), _tenantId, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(defect);
    }

    [Fact]
    public async Task HandleClosed_AStoreFailureLoadingTheInAppDestinations_StillClears()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("db down"));

        await _sut.HandleClosedAsync(Closed(), _tenantId, CancellationToken.None);

        _alarmRelay.Verify(
            r => r.ClearIfQuietAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public async Task HandleClosed_ADefectArchivingARecipientsNotification_Propagates(Exception defect)
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["user-a", "user-b"]);
        _notification
            .Setup(n => n.ArchiveBySourceAsync(
                "user-a", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationArchiveReason>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(defect);

        var act = () => _sut.HandleClosedAsync(Closed(), _tenantId, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(defect);
    }

    [Fact]
    public async Task HandleClosed_AStoreFailureArchivingOneRecipient_ArchivesTheNext()
    {
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());
        _repository.Setup(r => r.GetInAppDestinationsForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["user-a", "user-b"]);
        _notification
            .Setup(n => n.ArchiveBySourceAsync(
                "user-a", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationArchiveReason>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("db down"));

        await _sut.HandleClosedAsync(Closed(), _tenantId, CancellationToken.None);

        _notification.Verify(n => n.ArchiveBySourceAsync(
            "user-b", It.IsAny<string>(), _excursionId.ToString(), NotificationArchiveReason.ConditionMet,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
