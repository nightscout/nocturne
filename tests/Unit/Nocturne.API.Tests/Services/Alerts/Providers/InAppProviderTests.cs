using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Tests.Shared.Logging;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Providers;

[Trait("Category", "Unit")]
public class InAppProviderTests
{
    private const string Recipient = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";

    private readonly Mock<IInAppNotificationService> _notification = new();
    private readonly InAppProvider _sut;

    public InAppProviderTests()
    {
        _sut = new InAppProvider(_notification.Object, NullLogger<InAppProvider>.Instance);
    }

    private static AlertPayload MakePayload(
        AlertRuleSeverity severity = AlertRuleSeverity.Warning,
        decimal? glucose = 65m,
        decimal? trendRate = -1.2m) =>
        new()
        {
            AlertType = AlertConditionType.Threshold,
            RuleName = "Low glucose",
            GlucoseValue = glucose,
            Trend = null,
            TrendRate = trendRate,
            ReadingTimestamp = DateTime.UtcNow,
            ExcursionId = Guid.NewGuid(),
            InstanceId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SubjectName = "Test",
            ActiveExcursionCount = 1,
            Severity = severity,
        };

    [Fact]
    public async Task SendAsync_CreatesNotificationWithExpectedFields()
    {
        var payload = MakePayload();

        await _sut.SendAsync(Recipient, payload, CancellationToken.None);

        _notification.Verify(n => n.CreateNotificationAsync(
            Recipient,
            InAppNotificationType.AlertFiring.ToWireString(),
            "Low glucose",
            NotificationCategory.Alert,
            NotificationUrgency.Warn,
            null,
            null,
            It.IsAny<string>(),
            payload.ExcursionId.ToString(),
            It.Is<List<NotificationActionDto>>(a =>
                a.Count == 2
                && a.Any(x => x.ActionId == InAppProvider.AckActionId)
                && a.Any(x => x.ActionId == InAppProvider.DismissActionId)),
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(AlertRuleSeverity.Critical, NotificationUrgency.Urgent)]
    [InlineData(AlertRuleSeverity.Warning, NotificationUrgency.Warn)]
    [InlineData(AlertRuleSeverity.Info, NotificationUrgency.Info)]
    public async Task SendAsync_MapsSeverityToUrgency(AlertRuleSeverity severity, NotificationUrgency expected)
    {
        var payload = MakePayload(severity);

        await _sut.SendAsync(Recipient, payload, CancellationToken.None);

        _notification.Verify(n => n.CreateNotificationAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<NotificationCategory?>(),
            expected,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<List<NotificationActionDto>?>(),
            It.IsAny<ResolutionConditions?>(),
            It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_NoGlucoseValue_OmitsSubtitle()
    {
        var payload = MakePayload(glucose: null);

        await _sut.SendAsync(Recipient, payload, CancellationToken.None);

        _notification.Verify(n => n.CreateNotificationAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<NotificationCategory?>(),
            It.IsAny<NotificationUrgency?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            (string?)null,
            It.IsAny<string?>(),
            It.IsAny<List<NotificationActionDto>?>(),
            It.IsAny<ResolutionConditions?>(),
            It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_UnevaluableNotice_IsNotMirroredToDevices()
    {
        string? type = null, title = null, subtitle = null;
        NotificationCategory? category = null;
        NotificationUrgency? urgency = null;
        _notification
            .Setup(n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, NotificationCategory?, NotificationUrgency?, string?, string?, string?, string?,
                List<NotificationActionDto>?, ResolutionConditions?, Dictionary<string, object>?, CancellationToken>(
                (_, t, ti, c, u, _, _, s, _, _, _, _, _) =>
                {
                    (type, title, category, urgency, subtitle) = (t, ti, c, u, s);
                })
            .ReturnsAsync(new InAppNotificationDto());

        await _sut.SendAsync(
            Recipient,
            MakePayload(AlertRuleSeverity.Critical) with { Kind = AlertPayloadKind.Unevaluable },
            CancellationToken.None);

        type.Should().Be(InAppNotificationType.AlertUnevaluable.ToWireString());
        DeviceNotificationMirror.Qualifies(new InAppNotificationDto
        {
            Type = type!,
            Title = title!,
            Subtitle = subtitle,
            Category = category!.Value,
            Urgency = urgency!.Value,
        }).Should().BeFalse("its subtitle is a label key the desktop cannot resolve");
    }

    [Fact]
    public async Task SendAsync_PartlyUnevaluableNotice_IsTheUnevaluableTypeWithItsOwnSubtitle_AndIsArchivedAsOne()
    {
        string? type = null, title = null, subtitle = null;
        List<NotificationActionDto>? actions = null;
        _notification
            .Setup(n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, NotificationCategory?, NotificationUrgency?, string?, string?, string?, string?,
                List<NotificationActionDto>?, ResolutionConditions?, Dictionary<string, object>?, CancellationToken>(
                (_, t, ti, _, _, _, _, s, _, a, _, _, _) =>
                {
                    (type, title, subtitle, actions) = (t, ti, s, a);
                })
            .ReturnsAsync(new InAppNotificationDto());
        var payload = MakePayload(AlertRuleSeverity.Info) with { Kind = AlertPayloadKind.PartlyUnevaluable };

        await _sut.SendAsync(Recipient, payload, CancellationToken.None);

        (type, title, subtitle).Should().Be(
            (InAppNotificationType.AlertUnevaluable.ToWireString(), payload.RuleName, NotificationLabel.AlertPartlyUnevaluableSubtitle.ToWireString()));
        actions.Should().BeNullOrEmpty("there is no excursion to acknowledge");
        InAppProvider.NotificationTypeFor(new ExcursionRuleSnapshot(Guid.NewGuid(), payload.RuleName, IsUnevaluableNotice: true))
            .Should().Be(type, "resolving the notice archives the notifications of the type it created");
    }

    [Fact]
    public async Task SendAsync_ReportsTheRecipientReached()
    {
        (await _sut.SendAsync(Recipient, MakePayload(), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("user-123")]
    public async Task SendAsync_DestinationNamingNoSubject_CreatesNothingAndReportsNoRecipient(string destination)
    {
        var reached = await _sut.SendAsync(destination, MakePayload(), CancellationToken.None);

        reached.Should().BeFalse();
        _notification.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendAsync_NotifiesTheSubjectInTheFormTheNotificationCentreReadsBy()
    {
        await _sut.SendAsync(Recipient.ToUpperInvariant(), MakePayload(), CancellationToken.None);

        _notification.Verify(n => n.CreateNotificationAsync(
            Recipient,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<NotificationCategory?>(),
            It.IsAny<NotificationUrgency?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<List<NotificationActionDto>?>(),
            It.IsAny<ResolutionConditions?>(),
            It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private void CreationThrows(Exception failure) =>
        _notification.Setup(n => n.CreateNotificationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(),
                It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(),
                It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

    [Theory]
    [InlineData(typeof(NpgsqlException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    public async Task SendAsync_AFailureReachesTheCallerAndIsLoggedByNoOneHere(Type failureType)
    {
        var failure = (Exception)Activator.CreateInstance(failureType, "failed")!;
        CreationThrows(failure);
        var logger = new ListLogger<InAppProvider>();

        var send = () => new InAppProvider(_notification.Object, logger).SendAsync(Recipient, MakePayload(), CancellationToken.None);

        (await send.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        logger.Entries.Should().BeEmpty("the delivery service logs the failure once");
    }
}
