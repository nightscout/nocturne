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
/// Accepting a match answers success only when the food entry was linked. A request that linked
/// nothing is a missing target, and it leaves the suggestion notification alone.
/// </summary>
[Trait("Category", "Unit")]
public class MealMatchingControllerAcceptTests
{
    private readonly Mock<IMealMatchingService> _service = new();
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
            _service.Object, Mock.Of<IConnectorFoodEntryRepository>(), _notifications.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private void ServiceReportsLinked(bool linked) =>
        _service
            .Setup(s => s.AcceptMatchAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(linked);

    [Fact]
    public async Task AcceptMatch_answers404AndLeavesTheNotificationWhenNothingWasLinked()
    {
        ServiceReportsLinked(false);

        var result = await Controller().AcceptMatch(
            new AcceptMatchRequest { FoodEntryId = Guid.NewGuid(), CarbIntakeId = Guid.NewGuid(), Carbs = 30 });

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        problem.Value.Should().BeOfType<ProblemDetails>();
        _notifications.Verify(
            n => n.ArchiveBySourceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AcceptMatch_answers204AndArchivesTheNotificationWhenTheMatchWasLinked()
    {
        ServiceReportsLinked(true);
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
