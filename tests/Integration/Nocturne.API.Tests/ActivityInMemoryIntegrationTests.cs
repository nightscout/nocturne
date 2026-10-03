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
        var response = await AuthenticatedClient
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
        var response = await AuthenticatedClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                newActivity,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "Nightscout answers a created activity with 200");
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
        var response = await AuthenticatedClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                activities,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "Nightscout answers a created activity with 200");
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

        var createResponse = await AuthenticatedClient
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
        var response = await AuthenticatedClient
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
        var response = await AuthenticatedClient
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

        var createResponse = await AuthenticatedClient
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
        var response = await AuthenticatedClient
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
    public async Task UpdateActivity_WithNonExistentId_InsertsIt()
    {
        var response = await AuthenticatedClient.PutAsJsonAsync(
            $"/api/v1/activity/{Guid.NewGuid()}",
            new Activity { Type = "Exercise", Description = "Inserted by PUT" },
            cancellationToken: CancellationToken.None
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var saved = await response.Content.ReadFromJsonAsync<Activity>(
            cancellationToken: CancellationToken.None
        );
        saved!.Description.Should().Be("Inserted by PUT");
        saved.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SaveActivity_WithTheStoredIdInTheBody_UpdatesInPlace()
    {
        var create = await AuthenticatedClient.PostAsJsonAsync(
            "/api/v1/activity",
            new Activity { Type = "Exercise", Description = "Before", Duration = 30 },
            cancellationToken: CancellationToken.None
        );
        var id = (await create.Content.ReadFromJsonAsync<Activity[]>())![0].Id;

        var put = await AuthenticatedClient.PutAsJsonAsync(
            "/api/v1/activity",
            new Activity { Id = id, Type = "Exercise", Description = "After", Duration = 45 },
            cancellationToken: CancellationToken.None
        );

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var saved = await put.Content.ReadFromJsonAsync<Activity>();
        saved!.Id.Should().Be(id);
        saved.Description.Should().Be("After");

        var stored = await AuthenticatedClient.GetFromJsonAsync<Activity>($"/api/v1/activity/{id}");
        stored!.Description.Should().Be("After");
        stored.Duration.Should().Be(45);
    }

    [Fact]
    public async Task SaveActivity_WithAnArray_IsRefused()
    {
        var put = await AuthenticatedClient.PutAsJsonAsync(
            "/api/v1/activity",
            new[] { new Activity { Type = "Exercise", Description = "In an array" } },
            cancellationToken: CancellationToken.None
        );

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveActivity_OfAnActivityTheUserDeleted_IsRefusedAndNotBroughtBack(bool clientId)
    {
        var description = $"deleted-{Guid.NewGuid():N}";
        var create = await AuthenticatedClient.PostAsJsonAsync(
            "/api/v1/activity",
            new Activity
            {
                Id = clientId ? $"exercise-{Guid.NewGuid():N}" : null,
                Type = "Exercise",
                Description = description,
                Duration = 30,
            },
            cancellationToken: CancellationToken.None
        );
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await create.Content.ReadFromJsonAsync<Activity[]>())![0].Id!;
        (await AuthenticatedClient.DeleteAsync($"/api/v1/activity/{id}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var put = await AuthenticatedClient.PutAsJsonAsync(
            "/api/v1/activity",
            new Activity { Id = id, Type = "Exercise", Description = description, Duration = 45 },
            cancellationToken: CancellationToken.None
        );
        var putById = await AuthenticatedClient.PutAsJsonAsync(
            $"/api/v1/activity/{id}",
            new Activity { Type = "Exercise", Description = description, Duration = 60 },
            cancellationToken: CancellationToken.None
        );

        put.StatusCode.Should().Be(HttpStatusCode.Conflict, await put.Content.ReadAsStringAsync());
        putById.StatusCode.Should().Be(HttpStatusCode.Conflict, await putById.Content.ReadAsStringAsync());
        (await AuthenticatedClient.GetAsync($"/api/v1/activity/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        var all = await AuthenticatedClient.GetFromJsonAsync<Activity[]>("/api/v1/activity?count=1000");
        all!.Should().NotContain(a => a.Description == description);
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

        var createResponse = await AuthenticatedClient
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
        var response = await AuthenticatedClient
            .DeleteAsync($"/api/v1/activity/{activityId}", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        status.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        status.GetProperty("deletedCount").GetInt64().Should().Be(1);
        status.GetProperty("n").GetInt64().Should().Be(1);

        // Verify the activity is actually deleted
        var getResponse = await AuthenticatedClient
            .GetAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteActivity_WithNonExistentId_AnswersOkWithNoneDeleted()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString();

        // Act
        var response = await AuthenticatedClient
            .DeleteAsync($"/api/v1/activity/{nonExistentId}", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        status.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        status.GetProperty("deletedCount").GetInt64().Should().Be(0);
        status.GetProperty("n").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task GetActivities_WithPaginationParameters_ShouldRespectParameters()
    {
        // Arrange - Create multiple activities an hour apart: same-type spans from one source
        // inside the dedup window collapse to one primary, and the list hides the rest
        var start = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var activities = Enumerable
            .Range(1, 15)
            .Select(i => new Activity
            {
                Type = "Exercise",
                Description = $"Activity {i}",
                Duration = i * 5,
                Mills = start + i * (long)TimeSpan.FromHours(1).TotalMilliseconds,
            })
            .ToArray();

        await AuthenticatedClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                activities,
                cancellationToken: CancellationToken.None
            );

        // Act - Get with pagination
        var response = await AuthenticatedClient
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
        await AuthenticatedClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                firstActivity,
                cancellationToken: CancellationToken.None
            );

        // Small delay to ensure different timestamps
        await Task.Delay(100, CancellationToken.None);

        var secondActivity = new Activity { Type = "Walking", Description = "Second activity" };
        await AuthenticatedClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                secondActivity,
                cancellationToken: CancellationToken.None
            );

        // Act
        var response = await AuthenticatedClient
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
        var response = await AuthenticatedClient
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
        var createResponse = await AuthenticatedClient
            .PostAsJsonAsync(
                "/api/v1/activity",
                originalActivity,
                cancellationToken: CancellationToken.None
            );
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Nightscout answers a created activity with 200");
        var createdActivities = await createResponse.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        var activityId = createdActivities![0].Id;

        // Act 2: Read
        var getResponse = await AuthenticatedClient
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
        var updateResponse = await AuthenticatedClient
            .PutAsJsonAsync(
                $"/api/v1/activity/{activityId}",
                updatedActivity,
                cancellationToken: CancellationToken.None
            );
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act 4: Verify Update
        var getUpdatedResponse = await AuthenticatedClient
            .GetAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        var updatedRetrievedActivity = await getUpdatedResponse.Content.ReadFromJsonAsync<Activity>(
            cancellationToken: CancellationToken.None
        );
        updatedRetrievedActivity!.Description.Should().Be("Updated CRUD test");
        updatedRetrievedActivity.Type.Should().Be("Walking");

        // Act 5: Delete
        var deleteResponse = await AuthenticatedClient
            .DeleteAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act 6: Verify Delete
        var getFinalResponse = await AuthenticatedClient
            .GetAsync($"/api/v1/activity/{activityId}", CancellationToken.None);
        getFinalResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateActivity_OfASleepSessionTheUserDeleted_ShouldAnswerConflict()
    {
        var sleep = new Activity
        {
            Id = "sleep-put-after-delete",
            Type = "sleep",
            Mills = 1_767_300_000_000,
            Duration = 480,
        };
        var createResponse = await AuthenticatedClient
            .PostAsJsonAsync("/api/v1/activity", sleep, cancellationToken: CancellationToken.None);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await createResponse.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        var sessionId = created!.Should().ContainSingle().Which.Id;

        var deleteResponse = await AuthenticatedClient
            .DeleteAsync($"/api/v1/activity/{sessionId}", CancellationToken.None);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updateResponse = await AuthenticatedClient
            .PutAsJsonAsync($"/api/v1/activity/{sleep.Id}", sleep, cancellationToken: CancellationToken.None);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a PUT must not bring back a sleep session the user deleted");
        var getResponse = await AuthenticatedClient
            .GetAsync($"/api/v1/activity/{sessionId}", CancellationToken.None);
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateActivity_BySessionGuidOfASleepSessionTheUserDeleted_ShouldAnswerConflict()
    {
        var sleep = new Activity
        {
            Id = "sleep-put-by-guid-after-delete",
            Type = "sleep",
            Mills = 1_767_400_000_000,
            Duration = 420,
        };
        var createResponse = await AuthenticatedClient
            .PostAsJsonAsync("/api/v1/activity", sleep, cancellationToken: CancellationToken.None);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await createResponse.Content.ReadFromJsonAsync<Activity[]>(
            cancellationToken: CancellationToken.None
        );
        var sessionId = created!.Should().ContainSingle().Which.Id;
        Guid.TryParse(sessionId, out _).Should().BeTrue("v1 projects a sleep session with its Guid as id");

        var deleteResponse = await AuthenticatedClient
            .DeleteAsync($"/api/v1/activity/{sessionId}", CancellationToken.None);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var update = new Activity { Id = sessionId, Type = "sleep", Mills = sleep.Mills, Duration = 450 };
        var updateResponse = await AuthenticatedClient
            .PutAsJsonAsync($"/api/v1/activity/{sessionId}", update, cancellationToken: CancellationToken.None);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a PUT by session Guid must not bring back a sleep session the user deleted");
        var getResponse = await AuthenticatedClient
            .GetAsync($"/api/v1/activity/{sessionId}", CancellationToken.None);
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateActivities_XDripHeartRateResentNextCycle_StoresEachSampleOnceWithItsType()
    {
        var first = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        long[] times = [first, first + 60_000, first + 120_000];

        var cycle1 = await PostJsonAsync(XDripHeartRates((times[0], 70), (times[1], 75)));
        var cycle2 = await PostJsonAsync(XDripHeartRates((times[1], 75), (times[2], 80)));

        cycle1.StatusCode.Should().Be(HttpStatusCode.OK);
        cycle2.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await GetHeartRateActivitiesAsync(times);
        stored.Select(a => (a.Mills, a.Type, a.Bpm)).Should().BeEquivalentTo(
            [(times[0], "hr-bpm", 70), (times[1], "hr-bpm", 75), (times[2], "hr-bpm", 80)]);
    }

    [Fact]
    public async Task CreateActivities_HeartRateThatFailsToStore_Returns500AndStoresNothing()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeMilliseconds();
        var tooLongDevice = new string('d', 300);

        var response = await PostJsonAsync(
            $$"""[{"type":"hr-bpm","timeStamp":{{at}},"bpm":70,"device":"{{tooLongDevice}}"}]""");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await GetHeartRateActivitiesAsync([at])).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateActivities_SleepThatFailsToStore_Returns500AndStoresNothing()
    {
        var at = DateTimeOffset.UtcNow.AddHours(-36).ToUnixTimeMilliseconds();

        var response = await PostJsonAsync(
            $$"""[{"_id":"5f1a2b3c4d5e6f7a8b9c0d1f","type":"sleep","mills":{{at}},"duration":1e12}]""");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await GetActivitiesAtAsync("sleep", at)).Should().Be(0);
    }

    private async Task<int> GetActivitiesAtAsync(string type, long mills)
    {
        var response = await AuthenticatedClient.GetAsync("/api/v1/activity?count=1000", CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return doc.RootElement.EnumerateArray().Count(a =>
            a.TryGetProperty("type", out var t) && t.GetString() == type
            && a.GetProperty("mills").GetInt64() == mills);
    }

    private static string XDripHeartRates(params (long At, int Bpm)[] readings) =>
        "[" + string.Join(",", readings.Select(r =>
            $$"""{"type":"hr-bpm","timeStamp":{{r.At}},"created_at":"{{DateTimeOffset.FromUnixTimeMilliseconds(r.At):yyyy-MM-dd'T'HH:mm:ss'Z'}}","bpm":{{r.Bpm}}}""")) + "]";

    private Task<HttpResponseMessage> PostJsonAsync(string json) =>
        AuthenticatedClient.PostAsync(
            "/api/v1/activity",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            CancellationToken.None);

    private async Task<List<(long Mills, string? Type, int Bpm)>> GetHeartRateActivitiesAsync(long[] times)
    {
        var response = await AuthenticatedClient.GetAsync("/api/v1/activity?count=1000", CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return doc.RootElement.EnumerateArray()
            .Where(a => a.TryGetProperty("bpm", out _) && times.Contains(a.GetProperty("mills").GetInt64()))
            .Select(a => (
                a.GetProperty("mills").GetInt64(),
                a.TryGetProperty("type", out var type) ? type.GetString() : null,
                a.GetProperty("bpm").GetInt32()))
            .ToList();
    }
}
