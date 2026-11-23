using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.NotificationActionHandlers;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.NotificationActionHandlers;

/// <summary>
/// The dismiss action on a meal match notification archives it only when the food entry was dismissed.
/// A food entry that is missing, or that an accept has already linked, leaves the notification and the
/// food entry as they are.
/// </summary>
[Trait("Category", "Unit")]
public class MealMatchActionHandlerTests
{
    private readonly Mock<IConnectorFoodEntryRepository> _foodEntries = new();

    private Task<NotificationActionResult> Dismiss(string? sourceId) =>
        new MealMatchActionHandler(_foodEntries.Object, NullLogger<MealMatchActionHandler>.Instance)
            .HandleAsync(Guid.NewGuid(), "dismiss", "user-1", sourceId, null);

    [Theory]
    [InlineData(DismissMatchOutcome.Dismissed)]
    [InlineData(DismissMatchOutcome.AlreadyDismissed)]
    public async Task Dismiss_archivesTheNotificationWhenTheFoodEntryIsDismissed(DismissMatchOutcome outcome)
    {
        var foodEntryId = Guid.NewGuid();
        _foodEntries.Setup(r => r.DismissAsync(foodEntryId, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);

        var result = await Dismiss(foodEntryId.ToString());

        result.Should().Be(NotificationActionResult.Dismissed);
    }

    [Theory]
    [InlineData(DismissMatchOutcome.NotFound)]
    [InlineData(DismissMatchOutcome.AlreadyResolved)]
    public async Task Dismiss_leavesTheNotificationWhenTheFoodEntryWasNotDismissed(DismissMatchOutcome outcome)
    {
        var foodEntryId = Guid.NewGuid();
        _foodEntries.Setup(r => r.DismissAsync(foodEntryId, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);

        var result = await Dismiss(foodEntryId.ToString());

        result.Should().Be(NotificationActionResult.NotHandled);
        result.Archive.Should().BeNull();
    }

    [Fact]
    public async Task Dismiss_withASourceThatNamesNoFoodEntry_changesNoFoodEntry()
    {
        var result = await Dismiss("not-a-guid");

        result.Should().Be(NotificationActionResult.Dismissed);
        _foodEntries.Verify(
            r => r.DismissAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
