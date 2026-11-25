using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Extensions;
using Nocturne.API.Services.NotificationActionHandlers;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Services.NotificationActionHandlers;

/// <summary>
/// What each action of <see cref="MealMatchActionHandler"/> links, archives or declines, and for which scope.
/// </summary>
[Trait("Category", "Unit")]
public class MealMatchActionHandlerTests
{
    private readonly Mock<IConnectorFoodEntryRepository> _foodEntries = new();

    private readonly Mock<IMealMatchLinker> _linker = new();

    private static Dictionary<string, object> Suggestion(Guid carbIntakeId) => new()
    {
        ["carbIntakeId"] = JsonDocument.Parse($"\"{carbIntakeId}\"").RootElement.Clone(),
        ["foodEntryCarbs"] = JsonDocument.Parse("12.5").RootElement.Clone(),
    };

    private void FoodEntryHas(Guid id, decimal carbs) =>
        _foodEntries
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorFoodEntry { Id = id, Carbs = carbs });

    private void AnyFoodEntryExists() =>
        _foodEntries
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new ConnectorFoodEntry { Id = id, Carbs = 12.5m });

    private Task<NotificationActionResult> Accept(
        string? sourceId, Dictionary<string, object>? metadata, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetGrantedScopes(grantedScopes.ToHashSet());
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContext);
        return Handler(accessor.Object).HandleAsync(Guid.NewGuid(), "accept", "user-1", sourceId, metadata);
    }

    private void VerifyNothingLinked() =>
        _linker.Verify(
            l => l.LinkAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [Theory]
    [InlineData(AcceptMatchOutcome.Linked)]
    [InlineData(AcceptMatchOutcome.AlreadyLinked)]
    public async Task Accept_linksTheCarbsTheFoodEntryHasNowAtTheCarbIntakeTimeAndArchivesAsCompleted(
        AcceptMatchOutcome outcome)
    {
        var foodEntryId = Guid.NewGuid();
        var carbIntakeId = Guid.NewGuid();
        FoodEntryHas(foodEntryId, carbs: 20m);
        _linker
            .Setup(l => l.LinkAsync(foodEntryId, carbIntakeId, 20m, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = await Accept(foodEntryId.ToString(), Suggestion(carbIntakeId), Scope.TreatmentsReadWrite);

        result.Should().Be(NotificationActionResult.Completed);
    }

    [Fact]
    public async Task Accept_withoutTheTreatmentsWriteScopeIsRefusedForThatScopeAndLinksNothing()
    {
        var result = await Accept(
            Guid.NewGuid().ToString(), Suggestion(Guid.NewGuid()), Scope.FoodReadWrite, Scope.DeviceNotify);

        result.Should().Be(NotificationActionResult.RefusedFor(Scope.TreatmentsReadWrite));
        VerifyNothingLinked();
    }

    [Fact]
    public async Task Accept_withoutARequestIsRefusedForTheTreatmentsWriteScopeAndLinksNothing()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);

        var result = await Handler(accessor.Object)
            .HandleAsync(Guid.NewGuid(), "accept", "user-1", Guid.NewGuid().ToString(), Suggestion(Guid.NewGuid()));

        result.Should().Be(NotificationActionResult.RefusedFor(Scope.TreatmentsReadWrite));
        VerifyNothingLinked();
    }

    [Theory]
    [InlineData("not-a-guid", true)]
    [InlineData(null, true)]
    [InlineData("valid", false)]
    public async Task Accept_withoutWhatItNeedsDeclinesAndLinksNothing(string? source, bool withMetadata)
    {
        var sourceId = source == "valid" ? Guid.NewGuid().ToString() : source;
        var metadata = withMetadata ? Suggestion(Guid.NewGuid()) : new Dictionary<string, object>();

        var result = await Accept(sourceId, metadata, Scope.TreatmentsReadWrite);

        result.Should().Be(NotificationActionResult.NotHandled);
        VerifyNothingLinked();
    }

    [Fact]
    public async Task Accept_declinesAndLinksNothingWhenTheFoodEntryIsMissing()
    {
        var result = await Accept(Guid.NewGuid().ToString(), Suggestion(Guid.NewGuid()), Scope.TreatmentsReadWrite);

        result.Should().Be(NotificationActionResult.NotHandled);
        VerifyNothingLinked();
    }

    [Fact]
    public async Task Accept_declinesWhenTheFoodEntryOrCarbIntakeIsMissing()
    {
        AnyFoodEntryExists();
        _linker
            .Setup(l => l.LinkAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AcceptMatchOutcome.NotFound);

        var result = await Accept(Guid.NewGuid().ToString(), Suggestion(Guid.NewGuid()), Scope.TreatmentsReadWrite);

        result.Should().Be(NotificationActionResult.NotHandled);
        result.Archive.Should().BeNull();
    }

    [Fact]
    public async Task Accept_archivesTheStaleSuggestionWhenTheFoodEntryWasResolvedElsewhere()
    {
        AnyFoodEntryExists();
        _linker
            .Setup(l => l.LinkAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AcceptMatchOutcome.AlreadyResolved);

        var result = await Accept(Guid.NewGuid().ToString(), Suggestion(Guid.NewGuid()), Scope.TreatmentsReadWrite);

        result.Handled.Should().BeTrue();
        result.Archive.Should().Be(NotificationArchiveReason.ConditionMet);
    }

    private Task<NotificationActionResult> Dismiss(string? sourceId, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetGrantedScopes(grantedScopes.ToHashSet());
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContext);
        return Handler(accessor.Object).HandleAsync(Guid.NewGuid(), "dismiss", "user-1", sourceId, null);
    }

    private MealMatchActionHandler Handler(IHttpContextAccessor accessor) =>
        new(_foodEntries.Object, _linker.Object, accessor, NullLogger<MealMatchActionHandler>.Instance);

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
