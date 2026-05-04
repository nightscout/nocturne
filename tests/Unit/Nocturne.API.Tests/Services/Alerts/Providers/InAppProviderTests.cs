using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Providers;

[Trait("Category", "Unit")]
public class InAppProviderTests
{
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

        await _sut.SendAsync("user-123", payload, CancellationToken.None);

        _notification.Verify(n => n.CreateNotificationAsync(
            "user-123",
            InAppProvider.NotificationType,
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

        await _sut.SendAsync("user-123", payload, CancellationToken.None);

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

        await _sut.SendAsync("user-123", payload, CancellationToken.None);

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
    public async Task SendAsync_EmptyDestination_SkipsNotification()
    {
        await _sut.SendAsync("", MakePayload(), CancellationToken.None);

        _notification.VerifyNoOtherCalls();
    }
}
