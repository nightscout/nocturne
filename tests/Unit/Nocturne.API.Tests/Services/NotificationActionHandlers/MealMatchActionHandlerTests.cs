using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Extensions;
using Nocturne.API.Services.NotificationActionHandlers;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Services.NotificationActionHandlers;

/// <summary>
/// The dismiss action on a meal match notification asks for it to be archived as dismissed once the food
/// entry is dismissed. A food entry that is missing, or that an accept has already linked, is left as it
/// is and the handler declines, so the orchestrator archives the stale suggestion. A caller without
/// <see cref="Scope.FoodReadWrite"/> cannot move a food entry from the sidebar, as it cannot from the meals
/// page, so the handler declines for it too.
/// </summary>
[Trait("Category", "Unit")]
public class MealMatchActionHandlerTests
{
    private readonly Mock<IConnectorFoodEntryRepository> _foodEntries = new();

    private Task<NotificationActionResult> Dismiss(string? sourceId, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetGrantedScopes(grantedScopes.ToHashSet());
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContext);
        return Handler(accessor.Object).HandleAsync(Guid.NewGuid(), "dismiss", "user-1", sourceId, null);
    }

    private MealMatchActionHandler Handler(IHttpContextAccessor accessor) =>
        new(_foodEntries.Object, accessor, NullLogger<MealMatchActionHandler>.Instance);

    [Theory]
    [InlineData(DismissMatchOutcome.Dismissed)]
    [InlineData(DismissMatchOutcome.AlreadyDismissed)]
    public async Task Dismiss_archivesTheNotificationWhenTheFoodEntryIsDismissed(DismissMatchOutcome outcome)
    {
        var foodEntryId = Guid.NewGuid();
        _foodEntries.Setup(r => r.DismissAsync(foodEntryId, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);

        var result = await Dismiss(foodEntryId.ToString(), Scope.FoodReadWrite);

        result.Should().Be(NotificationActionResult.Dismissed);
    }

    [Theory]
    [InlineData(DismissMatchOutcome.NotFound)]
    [InlineData(DismissMatchOutcome.AlreadyResolved)]
    public async Task Dismiss_declinesWhenTheFoodEntryWasNotDismissed(DismissMatchOutcome outcome)
    {
        var foodEntryId = Guid.NewGuid();
        _foodEntries.Setup(r => r.DismissAsync(foodEntryId, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);

        var result = await Dismiss(foodEntryId.ToString(), Scope.FoodReadWrite);

        result.Should().Be(NotificationActionResult.NotHandled);
        result.Archive.Should().BeNull();
    }

    [Fact]
    public async Task Dismiss_withASourceThatNamesNoFoodEntry_changesNoFoodEntry()
    {
        var result = await Dismiss("not-a-guid", Scope.FoodReadWrite);

        result.Should().Be(NotificationActionResult.Dismissed);
        _foodEntries.Verify(
            r => r.DismissAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dismiss_declinesWithoutTheFoodWriteScopeAndLeavesTheFoodEntry()
    {
        var foodEntryId = Guid.NewGuid();

        var result = await Dismiss(foodEntryId.ToString(), Scope.AlertsReadWrite, Scope.DeviceNotify);

        result.Should().Be(NotificationActionResult.NotHandled);
        _foodEntries.Verify(
            r => r.DismissAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dismiss_withoutARequestDeclinesAndLeavesTheFoodEntry()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);

        var result = await Handler(accessor.Object)
            .HandleAsync(Guid.NewGuid(), "dismiss", "user-1", Guid.NewGuid().ToString(), null);

        result.Should().Be(NotificationActionResult.NotHandled);
        _foodEntries.Verify(
            r => r.DismissAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
