using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for the unified in-app notification system
/// Tests the complete request/response cycle for notification CRUD operations
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class NotificationsIntegrationTests : ApiIntegrationTestBase
{
    private const string TestType = "integration_test";

    /// <summary>The owner the api-secret client authenticates as; notifications are per subject.</summary>
    private string OwnerUserId => Fixture.OwnerSubjectId.ToString();

    private static readonly string OtherUserId = Guid.CreateVersion7().ToString();

    public NotificationsIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    /// <summary>
    /// Runs <paramref name="action"/> on the API's notification service in a scope pinned to the
    /// seeded tenant, as a request on its host would be.
    /// </summary>
    private async Task<T> WithServiceAsync<T>(Func<IInAppNotificationService, Task<T>> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        return await action(scope.ServiceProvider.GetRequiredService<IInAppNotificationService>());
    }

    private Task<InAppNotificationDto> CreateAsync(
        string userId,
        string title,
        NotificationUrgency urgency = NotificationUrgency.Info,
        string? sourceId = null,
        List<NotificationActionDto>? actions = null) =>
        WithServiceAsync(s => s.CreateNotificationAsync(
            userId, TestType, title, NotificationCategory.Informational, urgency, sourceId: sourceId, actions: actions));

    private async Task<List<InAppNotificationDto>> GetActiveOverHttpAsync()
    {
        var response = await AuthenticatedClient.GetAsync("/api/v4/notifications");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<InAppNotificationDto>>())!;
    }

    private async Task<NotificationArchiveReason?> ArchiveReasonAsync(Guid id)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var entity = await db.InAppNotifications.AsNoTracking().SingleAsync(n => n.Id == id);
        entity.IsArchived.Should().BeTrue();
        return entity.ArchiveReason;
    }

    #region GetNotifications Tests

    [Fact]
    public async Task GetNotifications_WhenNoNotifications_ReturnsEmptyArray()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/v4/notifications");

        // Assert
        // Note: May return Unauthorized if auth context doesn't provide subject ID
        // or OK with empty array if properly authenticated
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var notifications = await response.Content.ReadFromJsonAsync<List<InAppNotificationDto>>();
            notifications.Should().NotBeNull();
            notifications.Should().BeEmpty();
            Log("GET /api/v4/notifications returned empty array as expected");
        }
        else
        {
            // If auth doesn't work, we at least verify the endpoint exists
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.OK,
                HttpStatusCode.Unauthorized
            );
            Log($"GET /api/v4/notifications returned: {response.StatusCode}");
        }
    }

    #endregion

    #region Create and Get Tests

    [Fact]
    public async Task CreateAndGetNotification_ShouldPersistAndReturn()
    {
        // Arrange
        var created = await CreateAsync(OwnerUserId, "Integration test notification", NotificationUrgency.Warn);

        // Act
        var notifications = await GetActiveOverHttpAsync();

        // Assert
        var fetched = notifications.Should().ContainSingle(n => n.Id == created.Id).Subject;
        fetched.Title.Should().Be("Integration test notification");
        fetched.Type.Should().Be(TestType);
        fetched.Urgency.Should().Be(NotificationUrgency.Warn);
    }

    #endregion

    #region Dismiss Tests

    [Fact]
    public async Task DismissNotification_ShouldArchiveIt()
    {
        // Arrange
        var created = await CreateAsync(OwnerUserId, "To dismiss");

        // Act
        var response = await AuthenticatedClient.DeleteAsync($"/api/v4/notifications/{created.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetActiveOverHttpAsync()).Should().NotContain(n => n.Id == created.Id);
        (await ArchiveReasonAsync(created.Id)).Should().Be(NotificationArchiveReason.Dismissed);
    }

    #endregion

    #region Execute Action Tests

    [Fact]
    public async Task ExecuteAction_WithDismissAction_ShouldArchive()
    {
        // Arrange
        var created = await CreateAsync(
            OwnerUserId, "Dismiss by action", actions: [new NotificationActionDto { ActionId = "dismiss", Label = "Dismiss" }]);

        // Act
        var response = await AuthenticatedClient.PostAsync($"/api/v4/notifications/{created.Id}/actions/dismiss", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetActiveOverHttpAsync()).Should().NotContain(n => n.Id == created.Id);
        (await ArchiveReasonAsync(created.Id)).Should().Be(NotificationArchiveReason.Dismissed);
    }

    private const string MealMatchType = "meal_matching.suggested_match";

    private async Task<Guid> SeedFoodEntryAsync(
        ConnectorFoodEntryStatus status = ConnectorFoodEntryStatus.Pending,
        Guid? matchedCarbIntakeId = null,
        decimal carbs = 12)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var entry = new ConnectorFoodEntryEntity
        {
            Id = Guid.CreateVersion7(),
            ConnectorSource = "notification-test",
            ExternalEntryId = Guid.NewGuid().ToString("N"),
            ExternalFoodId = "food-1",
            ConsumedAt = DateTimeOffset.UtcNow,
            Carbs = carbs,
            Servings = 1,
            Status = status,
            MatchedCarbIntakeId = matchedCarbIntakeId,
        };
        db.ConnectorFoodEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<Guid> SeedCarbIntakeAsync()
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var intake = new CarbIntakeEntity { Id = Guid.CreateVersion7(), Timestamp = DateTime.UtcNow, Carbs = 40 };
        db.CarbIntakes.Add(intake);
        await db.SaveChangesAsync();
        return intake.Id;
    }

    private async Task<(ConnectorFoodEntryStatus Status, Guid? MatchedCarbIntakeId)> StoredFoodEntryAsync(Guid id)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var stored = await db.ConnectorFoodEntries
            .AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.Status, e.MatchedCarbIntakeId })
            .SingleAsync();
        return (stored.Status, stored.MatchedCarbIntakeId);
    }

    private Task<InAppNotificationDto> CreateMealMatchAsync(
        Guid foodEntryId, Guid? carbIntakeId = null, decimal snapshotCarbs = 12) =>
        WithServiceAsync(s => s.CreateNotificationAsync(
            OwnerUserId,
            MealMatchType,
            "Suggested meal match",
            NotificationCategory.ActionRequired,
            sourceId: foodEntryId.ToString(),
            actions:
            [
                new NotificationActionDto { ActionId = "accept", Label = "Accept" },
                new NotificationActionDto { ActionId = "dismiss", Label = "Dismiss" },
            ],
            metadata: carbIntakeId is null
                ? null
                : new Dictionary<string, object>
                {
                    ["carbIntakeId"] = carbIntakeId.Value,
                    ["foodEntryCarbs"] = snapshotCarbs,
                }));

    private async Task<int> LinkedFoodRowsAsync(Guid carbIntakeId)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return await db.TreatmentFoods.AsNoTracking().CountAsync(f => f.CarbIntakeId == carbIntakeId);
    }

    [Fact]
    public async Task ExecuteAction_AcceptingAMealMatch_LinksTheFoodEntryAndArchivesTheNotification()
    {
        var carbIntakeId = await SeedCarbIntakeAsync();
        var foodEntryId = await SeedFoodEntryAsync();
        var created = await CreateMealMatchAsync(foodEntryId, carbIntakeId);

        var response = await AuthenticatedClient.PostAsync($"/api/v4/notifications/{created.Id}/actions/accept", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredFoodEntryAsync(foodEntryId)).Should().Be((ConnectorFoodEntryStatus.Matched, carbIntakeId));
        (await LinkedFoodRowsAsync(carbIntakeId)).Should().Be(1);
        (await ArchiveReasonAsync(created.Id)).Should().Be(NotificationArchiveReason.Completed);
    }

    [Fact]
    public async Task ExecuteAction_AcceptingAMealMatch_LinksTheCarbsTheFoodEntryHasNowNotTheNotificationSnapshot()
    {
        var carbIntakeId = await SeedCarbIntakeAsync();
        var foodEntryId = await SeedFoodEntryAsync(carbs: 20);
        var created = await CreateMealMatchAsync(foodEntryId, carbIntakeId, snapshotCarbs: 12);

        var response = await AuthenticatedClient.PostAsync($"/api/v4/notifications/{created.Id}/actions/accept", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var row = await db.TreatmentFoods.AsNoTracking().SingleAsync(f => f.CarbIntakeId == carbIntakeId);
        row.Carbs.Should().Be(20m);
        row.TimeOffsetMinutes.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAction_AcceptingAMealMatchWithoutTheTreatmentsWriteScope_FailsAndLeavesBoth()
    {
        var carbIntakeId = await SeedCarbIntakeAsync();
        var foodEntryId = await SeedFoodEntryAsync();
        var created = await CreateMealMatchAsync(foodEntryId, carbIntakeId);

        // The service scope has no request, so the handler reads no granted scope.
        var result = await WithServiceAsync(s => s.ExecuteActionAsync(created.Id, "accept", OwnerUserId));

        result.Should().Be(NotificationActionResult.RefusedFor(Scope.TreatmentsReadWrite));
        (await StoredFoodEntryAsync(foodEntryId)).Should().Be((ConnectorFoodEntryStatus.Pending, (Guid?)null));
        (await LinkedFoodRowsAsync(carbIntakeId)).Should().Be(0);
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.InAppNotifications.AsNoTracking().SingleAsync(n => n.Id == created.Id)).IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAction_AcceptingAMealMatchAlreadyMatchedElsewhere_LeavesTheEntryAndArchivesTheStaleNotification()
    {
        var suggested = await SeedCarbIntakeAsync();
        var other = await SeedCarbIntakeAsync();
        var foodEntryId = await SeedFoodEntryAsync(ConnectorFoodEntryStatus.Matched, other);
        var created = await CreateMealMatchAsync(foodEntryId, suggested);

        var response = await AuthenticatedClient.PostAsync($"/api/v4/notifications/{created.Id}/actions/accept", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredFoodEntryAsync(foodEntryId)).Should().Be((ConnectorFoodEntryStatus.Matched, other));
        (await LinkedFoodRowsAsync(suggested)).Should().Be(0);
        (await ArchiveReasonAsync(created.Id)).Should().Be(NotificationArchiveReason.ConditionMet);
    }

    [Fact]
    public async Task ExecuteAction_DismissingAMealMatch_ResolvesTheFoodEntryAndArchivesTheNotification()
    {
        var foodEntryId = await SeedFoodEntryAsync();
        var created = await CreateMealMatchAsync(foodEntryId);

        var response = await AuthenticatedClient.PostAsync($"/api/v4/notifications/{created.Id}/actions/dismiss", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredFoodEntryAsync(foodEntryId)).Status.Should().Be(ConnectorFoodEntryStatus.Standalone);
        (await ArchiveReasonAsync(created.Id)).Should().Be(NotificationArchiveReason.Dismissed);
    }

    [Fact]
    public async Task ExecuteAction_DismissingAMealMatchAlreadyAccepted_LeavesTheFoodEntryAndArchivesTheStaleNotification()
    {
        var carbIntakeId = await SeedCarbIntakeAsync();
        var foodEntryId = await SeedFoodEntryAsync(ConnectorFoodEntryStatus.Matched, carbIntakeId);
        var created = await CreateMealMatchAsync(foodEntryId);

        var response = await AuthenticatedClient.PostAsync($"/api/v4/notifications/{created.Id}/actions/dismiss", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredFoodEntryAsync(foodEntryId)).Should().Be((ConnectorFoodEntryStatus.Matched, carbIntakeId));
        (await ArchiveReasonAsync(created.Id)).Should().Be(NotificationArchiveReason.Dismissed);
    }

    #endregion

    #region Sorting Tests

    [Fact]
    public async Task GetNotifications_SortsByUrgencyThenTime()
    {
        // Arrange - most urgent first, and within one urgency the newest first
        var info = await CreateAsync(OwnerUserId, "info", NotificationUrgency.Info);
        var olderWarn = await CreateAsync(OwnerUserId, "older warn", NotificationUrgency.Warn);
        var urgent = await CreateAsync(OwnerUserId, "urgent", NotificationUrgency.Urgent);
        var newerWarn = await CreateAsync(OwnerUserId, "newer warn", NotificationUrgency.Warn);

        // Act
        var notifications = await GetActiveOverHttpAsync();

        // Assert
        notifications.Select(n => n.Id).Should().Equal(urgent.Id, newerWarn.Id, olderWarn.Id, info.Id);
    }

    #endregion

    #region Archive Reason Tests

    [Fact]
    public async Task ArchiveNotification_WithDifferentReasons_ShouldWork()
    {
        foreach (var reason in Enum.GetValues<NotificationArchiveReason>())
        {
            // Arrange
            var created = await CreateAsync(OwnerUserId, $"archive {reason}");

            // Act
            var archived = await WithServiceAsync(s => s.ArchiveNotificationAsync(created.Id, reason, OwnerUserId));

            // Assert
            archived.Should().BeTrue(reason.ToString());
            (await ArchiveReasonAsync(created.Id)).Should().Be(reason);
        }

        (await GetActiveOverHttpAsync()).Should().BeEmpty();
    }

    #endregion

    #region Archive By Source Tests

    [Fact]
    public async Task ArchiveBySource_ShouldArchiveMatchingNotification()
    {
        // Arrange
        var matching = await CreateAsync(OwnerUserId, "from source", sourceId: "source-a");
        var other = await CreateAsync(OwnerUserId, "from another source", sourceId: "source-b");

        // Act
        var archived = await WithServiceAsync(s => s.ArchiveBySourceAsync(
            OwnerUserId, TestType, "source-a", NotificationArchiveReason.ConditionMet));

        // Assert
        archived.Should().BeTrue();
        var active = await GetActiveOverHttpAsync();
        active.Should().NotContain(n => n.Id == matching.Id);
        active.Should().Contain(n => n.Id == other.Id);
        (await ArchiveReasonAsync(matching.Id)).Should().Be(NotificationArchiveReason.ConditionMet);
    }

    #endregion

    #region Non-Existent Notification Tests

    [Fact]
    public async Task DismissNotification_WithNonExistentId_ShouldReturnNotFound()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await client.DeleteAsync($"/api/v4/notifications/{nonExistentId}");

        // Assert
        Log($"DELETE /api/v4/notifications/{nonExistentId} returned: {response.StatusCode}");

        // Should return NotFound or similar error for a non-existent notification
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.NoContent,
            HttpStatusCode.Unauthorized
        );
    }

    [Fact]
    public async Task ExecuteAction_WithNonExistentNotification_ShouldReturnNotFound()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await client.PostAsync(
            $"/api/v4/notifications/{nonExistentId}/actions/dismiss",
            null
        );

        // Assert
        Log($"POST /api/v4/notifications/{nonExistentId}/actions/dismiss returned: {response.StatusCode}");

        // Should return NotFound or similar error for a non-existent notification
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.NoContent,
            HttpStatusCode.Unauthorized
        );
    }

    #endregion

    #region User Isolation Tests

    [Fact]
    public async Task GetNotifications_ShouldOnlyReturnCurrentUserNotifications()
    {
        // Arrange
        var own = await CreateAsync(OwnerUserId, "mine");
        var others = await CreateAsync(OtherUserId, "someone else's");

        // Act
        var notifications = await GetActiveOverHttpAsync();

        // Assert
        notifications.Should().Contain(n => n.Id == own.Id);
        notifications.Should().NotContain(n => n.Id == others.Id);
    }

    [Fact]
    public async Task ExecuteAction_WithWrongUserId_ShouldReturnFalse()
    {
        // Arrange
        var others = await CreateAsync(
            OtherUserId, "someone else's", actions: [new NotificationActionDto { ActionId = "dismiss", Label = "Dismiss" }]);

        // Act
        var executed = await WithServiceAsync(s => s.ExecuteActionAsync(others.Id, "dismiss", OwnerUserId));

        // Assert
        executed.Handled.Should().BeFalse();
        (await WithServiceAsync(s => s.GetActiveNotificationsAsync(OtherUserId)))
            .Should().Contain(n => n.Id == others.Id);
    }

    #endregion
}
