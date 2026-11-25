using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Accepting a match answers success when the food entry is linked to the carb intake, by this
/// request or an earlier one. A missing target is 404 and a food entry already resolved elsewhere
/// is 409; both leave the suggestion notification alone.
/// </summary>
[Trait("Category", "Unit")]
public class MealMatchingControllerAcceptTests
{
    private readonly Mock<IMealMatchLinker> _linker = new();
    private readonly Mock<IInAppNotificationService> _notifications = new();

    private MealMatchingController Controller()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.CreateVersion7(),
        };

        return new MealMatchingController(
            Mock.Of<IMealMatchingService>(), _linker.Object, Mock.Of<IConnectorFoodEntryRepository>(),
            _notifications.Object, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private void ServiceReports(AcceptMatchOutcome outcome) =>
        _linker
            .Setup(s => s.LinkAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

    private void VerifyNotificationLeftAlone() =>
        _notifications.Verify(
            n => n.ArchiveBySourceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [Fact]
    public async Task AcceptMatch_answers404AndLeavesTheNotificationWhenTheTargetIsMissing()
    {
        ServiceReports(AcceptMatchOutcome.NotFound);

        var result = await Controller().AcceptMatch(
            new AcceptMatchRequest { FoodEntryId = Guid.NewGuid(), CarbIntakeId = Guid.NewGuid(), Carbs = 30 });

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(
            "This food or this carb entry no longer exists. Nothing changed.");
        VerifyNotificationLeftAlone();
    }

    [Fact]
    public async Task AcceptMatch_failsAndLeavesTheNotificationForAnOutcomeItDoesNotName()
    {
        ServiceReports((AcceptMatchOutcome)int.MaxValue);

        var accept = () => Controller().AcceptMatch(
            new AcceptMatchRequest { FoodEntryId = Guid.NewGuid(), CarbIntakeId = Guid.NewGuid(), Carbs = 30 });

        await accept.Should().ThrowAsync<InvalidOperationException>();
        VerifyNotificationLeftAlone();
    }

    [Fact]
    public async Task AcceptMatch_answers409AndLeavesTheNotificationWhenTheFoodEntryIsAlreadyResolved()
    {
        ServiceReports(AcceptMatchOutcome.AlreadyResolved);

        var result = await Controller().AcceptMatch(
            new AcceptMatchRequest { FoodEntryId = Guid.NewGuid(), CarbIntakeId = Guid.NewGuid(), Carbs = 30 });

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(
            "This food is already linked to a different carb entry, or it was dismissed or removed. Nothing changed.");
        VerifyNotificationLeftAlone();
    }

    [Theory]
    [InlineData(AcceptMatchOutcome.Linked)]
    [InlineData(AcceptMatchOutcome.AlreadyLinked)]
    public async Task AcceptMatch_answers204AndArchivesTheNotificationWhenTheFoodEntryIsLinkedToTheCarbIntake(
        AcceptMatchOutcome outcome)
    {
        ServiceReports(outcome);
        var foodEntryId = Guid.NewGuid();

        var result = await Controller().AcceptMatch(
            new AcceptMatchRequest { FoodEntryId = foodEntryId, CarbIntakeId = Guid.NewGuid(), Carbs = 30 });

        result.Should().BeOfType<NoContentResult>();
        _notifications.Verify(
            n => n.ArchiveBySourceAsync(
                It.IsAny<string>(), "meal_matching.suggested_match", foodEntryId.ToString(),
                NotificationArchiveReason.Completed, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
