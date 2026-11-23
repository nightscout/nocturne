using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Dismissing a match answers success when the food entry is dismissed, by this request or an
/// earlier one. A food entry that does not exist is 404 and one already linked or withdrawn is 409;
/// both leave the suggestion notification alone. Suggestions default their range from the time provider.
/// </summary>
[Trait("Category", "Unit")]
public class MealMatchingControllerDismissTests
{
    private readonly Mock<IMealMatchingService> _service = new();
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 21, 9, 30, 0, TimeSpan.Zero));

    private MealMatchingController Controller()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.CreateVersion7(),
        };

        return new MealMatchingController(
            _service.Object, Mock.Of<IMealMatchLinker>(), Mock.Of<IConnectorFoodEntryRepository>(),
            _notifications.Object, _time)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private void ServiceReports(DismissMatchOutcome outcome) =>
        _service
            .Setup(s => s.DismissMatchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

    private void VerifyNotificationLeftAlone() =>
        _notifications.Verify(
            n => n.ArchiveBySourceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [Fact]
    public async Task DismissMatch_answers404AndLeavesTheNotificationWhenTheFoodEntryIsMissing()
    {
        ServiceReports(DismissMatchOutcome.NotFound);

        var result = await Controller().DismissMatch(new DismissMatchRequest { FoodEntryId = Guid.NewGuid() });

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(
            "This food no longer exists. Nothing changed.");
        VerifyNotificationLeftAlone();
    }

    [Fact]
    public async Task DismissMatch_answers409AndLeavesTheNotificationWhenTheFoodEntryIsAlreadyResolved()
    {
        ServiceReports(DismissMatchOutcome.AlreadyResolved);

        var result = await Controller().DismissMatch(new DismissMatchRequest { FoodEntryId = Guid.NewGuid() });

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(
            "This food is already linked to a carb entry, or it was removed. Nothing changed.");
        VerifyNotificationLeftAlone();
    }

    [Fact]
    public async Task DismissMatch_failsAndLeavesTheNotificationForAnOutcomeItDoesNotName()
    {
        ServiceReports((DismissMatchOutcome)int.MaxValue);

        var dismiss = () => Controller().DismissMatch(new DismissMatchRequest { FoodEntryId = Guid.NewGuid() });

        await dismiss.Should().ThrowAsync<InvalidOperationException>();
        VerifyNotificationLeftAlone();
    }

    [Theory]
    [InlineData(DismissMatchOutcome.Dismissed)]
    [InlineData(DismissMatchOutcome.AlreadyDismissed)]
    public async Task DismissMatch_answers204AndArchivesTheNotificationWhenTheFoodEntryIsDismissed(
        DismissMatchOutcome outcome)
    {
        ServiceReports(outcome);
        var foodEntryId = Guid.NewGuid();

        var result = await Controller().DismissMatch(new DismissMatchRequest { FoodEntryId = foodEntryId });

        result.Should().BeOfType<NoContentResult>();
        _notifications.Verify(
            n => n.ArchiveBySourceAsync(
                It.IsAny<string>(), "meal_matching.suggested_match", foodEntryId.ToString(),
                NotificationArchiveReason.Dismissed, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSuggestions_defaultsItsRangeToTheDayBeforeTheTimeProvidersNow()
    {
        _service
            .Setup(s => s.GetSuggestionsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await Controller().GetSuggestions(null, null);

        _service.Verify(
            s => s.GetSuggestionsAsync(_time.GetUtcNow().AddDays(-1), _time.GetUtcNow(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
