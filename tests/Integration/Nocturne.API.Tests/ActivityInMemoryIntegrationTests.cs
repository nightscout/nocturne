using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for Activity endpoints using in-memory database
/// Tests the complete request/response cycle with real database operations
/// </summary>
[Trait("Category", "Integration")]
public class ActivityInMemoryIntegrationTests : ApiIntegrationTestBase
{
    public ActivityInMemoryIntegrationTests(
        ApiIntegrationTestFixture fixture,
        Xunit.Abstractions.ITestOutputHelper output
    )
        : base(fixture, output) { }

    [Fact]
    public async Task GetActivities_WhenNoActivitiesExist_ShouldReturnEmptyArray()
    {
        // Arrange & Act
        var response = await ApiClient
            .GetAsync("/api/v1/activity", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var activities = await response.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        activities.Should().NotBeNull();
        activities.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateActivity_WithValidData_ShouldCreateAndReturnActivity()
    {
        // Arrange
        var newActivity = new Activity
        {
            Type = "Exercise",
            Description = "Integration test exercise",
            Duration = 45,
            Intensity = "Moderate",
            Notes = "Test activity for integration testing",
        };

        // Act
        var response = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                newActivity,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdActivities = await response.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        createdActivities.Should().NotBeNull();
        createdActivities.Should().ContainSingle();

        var createdActivity = createdActivities![0];
        createdActivity.Id.Should().NotBeNullOrEmpty();
        createdActivity.Type.Should().Be(newActivity.Type);
        createdActivity.Description.Should().Be(newActivity.Description);
        createdActivity.Duration.Should().Be(newActivity.Duration);
        createdActivity.Intensity.Should().Be(newActivity.Intensity);
        createdActivity.Notes.Should().Be(newActivity.Notes);
        createdActivity.CreatedAt.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateActivities_WithMultipleActivities_ShouldCreateAllActivities()
    {
        // Arrange
        var activities = new[]
        {
            new Activity
            {
                Type = "Exercise",
                Description = "Morning run",
                Duration = 30,
            },
            new Activity
            {
                Type = "Walking",
                Description = "Evening walk",
                Duration = 20,
            },
        };

        // Act
        var response = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                activities,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdActivities = await response.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        createdActivities.Should().NotBeNull();
        createdActivities.Should().HaveCount(2);

        foreach (var activity in createdActivities!)
        {
            activity.Id.Should().NotBeNullOrEmpty();
            activity.CreatedAt.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetActivity_AfterCreation_ShouldReturnCreatedActivity()
    {
        // Arrange - Create an activity first
        var newActivity = new Activity
        {
            Type = "Exercise",
            Description = "Test for get by ID",
            Duration = 25,
        };

        var createResponse = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                newActivity,
                cancellationToken: CancellationToken.None
            );
        var createdActivities = await createResponse.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        var activityId = createdActivities![0].Id;

        // Act
        var response = await ApiClient
            .GetAsync($"/api/v1/activity/{activityId}", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var retrievedActivity = await response.Content.ReadFromJsonAsync<Activity>(
            cancellationToken: CancellationToken.None
        );
        retrievedActivity.Should().NotBeNull();
        retrievedActivity!.Id.Should().Be(activityId);
        retrievedActivity.Type.Should().Be(newActivity.Type);
        retrievedActivity.Description.Should().Be(newActivity.Description);
    }

    [Fact]
    public async Task GetActivity_WithNonExistentId_ShouldReturnNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString();

        // Act
        var response = await ApiClient
            .GetAsync($"/api/v1/activity/{nonExistentId}", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateActivity_WithValidData_ShouldUpdateAndReturnActivity()
    {
        // Arrange - Create an activity first
        var originalActivity = new Activity
        {
            Type = "Exercise",
            Description = "Original description",
            Duration = 30,
        };

        var createResponse = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                originalActivity,
                cancellationToken: CancellationToken.None
            );
        var createdActivities = await createResponse.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        var activityId = createdActivities![0].Id;

        var updatedActivity = new Activity
        {
            Type = "Walking",
            Description = "Updated description",
            Duration = 45,
            Intensity = "High",
        };

        // Act
        var response = await ApiClient
            .PutAsJsonAsync(
                $"/api/v1/activity/{activityId}",
                updatedActivity,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var returnedActivity = await response.Content.ReadFromJsonAsync<Activity>(
            cancellationToken: CancellationToken.None
        );
        returnedActivity.Should().NotBeNull();
        returnedActivity!.Id.Should().Be(activityId);
        returnedActivity.Type.Should().Be(updatedActivity.Type);
        returnedActivity.Description.Should().Be(updatedActivity.Description);
        returnedActivity.Duration.Should().Be(updatedActivity.Duration);
        returnedActivity.Intensity.Should().Be(updatedActivity.Intensity);
    }

    [Fact]
    public async Task UpdateActivity_WithNonExistentId_ShouldReturnNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString();
        var updatedActivity = new Activity { Type = "Exercise", Description = "Test" };

        // Act
        var response = await ApiClient
            .PutAsJsonAsync(
                $"/api/v1/activity/{nonExistentId}",
                updatedActivity,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteActivity_WithExistingId_ShouldDeleteAndReturnSuccess()
    {
        // Arrange - Create an activity first
        var newActivity = new Activity
        {
            Type = "Exercise",
            Description = "To be deleted",
            Duration = 20,
        };

        var createResponse = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                newActivity,
                cancellationToken: CancellationToken.None
            );
        var createdActivities = await createResponse.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        var activityId = createdActivities![0].Id;

        // Act
        var response = await ApiClient
            .DeleteAsync($"/api/v1/activity/{activityId}", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify the activity is actually deleted
        var getResponse = await ApiClient
            .GetAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteActivity_WithNonExistentId_ShouldReturnNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString();

        // Act
        var response = await ApiClient
            .DeleteAsync($"/api/v1/activity/{nonExistentId}", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetActivities_WithPaginationParameters_ShouldRespectParameters()
    {
        // Arrange - Create multiple activities
        var activities = Enumerable
            .Range(1, 15)
            .Select(i => new Activity
            {
                Type = "Exercise",
                Description = $"Activity {i}",
                Duration = i * 5,
            })
            .ToArray();

        await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                activities,
                cancellationToken: CancellationToken.None
            );

        // Act - Get with pagination
        var response = await ApiClient
            .GetAsync("/api/v1/activity?count=5&skip=5", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var retrievedActivities = await response.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        retrievedActivities.Should().NotBeNull();
        retrievedActivities.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetActivities_AfterCreatingMultiple_ShouldReturnInDescendingOrderByCreatedAt()
    {
        // Arrange - Create activities with different timestamps
        var firstActivity = new Activity { Type = "Exercise", Description = "First activity" };
        await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                firstActivity,
                cancellationToken: CancellationToken.None
            );

        // Small delay to ensure different timestamps
        await Task.Delay(100, CancellationToken.None);

        var secondActivity = new Activity { Type = "Walking", Description = "Second activity" };
        await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                secondActivity,
                cancellationToken: CancellationToken.None
            );

        // Act
        var response = await ApiClient
            .GetAsync("/api/v1/activity", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var activities = await response.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        activities.Should().NotBeNull();
        activities.Should().HaveCountGreaterThanOrEqualTo(2);

        // Most recent should be first (descending order)
        var latestActivity = activities!.FirstOrDefault(a => a.Description == "Second activity");
        var earliestActivity = activities.FirstOrDefault(a => a.Description == "First activity");

        if (latestActivity != null && earliestActivity != null)
        {
            var latestIndex = Array.IndexOf(activities, latestActivity);
            var earliestIndex = Array.IndexOf(activities, earliestActivity);
            latestIndex.Should().BeLessThan(earliestIndex);
        }
    }

    [Fact]
    public async Task CreateActivity_WithInvalidData_ShouldReturnBadRequest()
    {
        // Arrange
        var invalidJson = "{ invalid json }";
        var content = new StringContent(invalidJson, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await ApiClient
            .PostAsync("/api/v1/activity", content, CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActivityWorkflow_FullCrudCycle_ShouldWorkCorrectly()
    {
        // Arrange
        var originalActivity = new Activity
        {
            Type = "Exercise",
            Description = "Full CRUD test",
            Duration = 30,
            Intensity = "Moderate",
        };

        // Act 1: Create
        var createResponse = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                originalActivity,
                cancellationToken: CancellationToken.None
            );
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdActivities = await createResponse.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        var activityId = createdActivities![0].Id;

        // Act 2: Read
        var getResponse = await ApiClient
            .GetAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var retrievedActivity = await getResponse.Content.ReadFromJsonAsync<Activity>(
            cancellationToken: CancellationToken.None
        );
        retrievedActivity!.Description.Should().Be(originalActivity.Description);

        // Act 3: Update
        var updatedActivity = new Activity
        {
            Type = "Walking",
            Description = "Updated CRUD test",
            Duration = 45,
            Intensity = "High",
        };
        var updateResponse = await ApiClient
            .PutAsJsonAsync(
                $"/api/v1/activity/{activityId}",
                updatedActivity,
                cancellationToken: CancellationToken.None
            );
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act 4: Verify Update
        var getUpdatedResponse = await ApiClient
            .GetAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        var updatedRetrievedActivity = await getUpdatedResponse.Content.ReadFromJsonAsync<Activity>(
            cancellationToken: CancellationToken.None
        );
        updatedRetrievedActivity!.Description.Should().Be("Updated CRUD test");
        updatedRetrievedActivity.Type.Should().Be("Walking");

        // Act 5: Delete
        var deleteResponse = await ApiClient
            .DeleteAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act 6: Verify Delete
        var getFinalResponse = await ApiClient
            .GetAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        getFinalResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
