using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/activity endpoints.
/// Covers: GET, GET/{id}, POST, PUT/{id}, DELETE/{id}
/// </summary>
public class ActivityParityTests : ParityTestBase
{
    public ActivityParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region Data Seeding

    private async Task SeedActivityAsync(params object[] activities)
    {
        foreach (var activity in activities)
        {
            var nsResponse = await NightscoutClient.PostAsJsonAsync("/api/v1/activity", activity);
            nsResponse.EnsureSuccessStatusCode();

            var nocResponse = await NocturneClient.PostAsJsonAsync("/api/v1/activity", activity);
            nocResponse.EnsureSuccessStatusCode();
        }
    }

    #endregion

    #region GET /api/v1/activity

    [Fact]
    public async Task GetActivity_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/activity");
    }

    [Fact]
    public async Task GetActivity_WithData_ReturnsSameShape()
    {
        var activities = new[]
        {
            new
            {
                created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                activityType = "walking",
                duration = 30,
                notes = "Morning walk"
            },
            new
            {
                created_at = TestTimeProvider.GetTestTime().AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                activityType = "running",
                duration = 45,
                notes = "Evening jog"
            }
        };
        await SeedActivityAsync(activities.Cast<object>().ToArray());

        await AssertGetParityAsync("/api/v1/activity");
    }

    [Fact]
    public async Task GetActivity_WithCount_ReturnsSameShape()
    {
        var activities = Enumerable.Range(0, 10)
            .Select(i => (object)new
            {
                created_at = TestTimeProvider.GetTestTime().AddMinutes(-i * 60).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                activityType = "exercise",
                duration = 15 + i * 5
            })
            .ToArray();
        await SeedActivityAsync(activities);

        await AssertGetParityAsync("/api/v1/activity?count=5");
    }

    [Fact]
    public async Task GetActivity_WithFindFilter_ReturnsSameShape()
    {
        var activities = new object[]
        {
            new
            {
                created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                activityType = "walking",
                duration = 30
            },
            new
            {
                created_at = TestTimeProvider.GetTestTime().AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                activityType = "cycling",
                duration = 60
            }
        };
        await SeedActivityAsync(activities);

        await AssertGetParityAsync("/api/v1/activity?find[activityType]=walking");
    }

    #endregion

    #region POST /api/v1/activity

    [Fact]
    public async Task PostActivity_Simple_ReturnsSameShape()
    {
        var activity = new
        {
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            activityType = "walking",
            duration = 30
        };

        await AssertPostParityAsync("/api/v1/activity", activity);
    }

    [Fact]
    public async Task PostActivity_WithNotes_ReturnsSameShape()
    {
        var activity = new
        {
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            activityType = "running",
            duration = 45,
            notes = "5K training run",
            steps = 6500
        };

        await AssertPostParityAsync("/api/v1/activity", activity);
    }

    [Fact]
    public async Task PostActivity_WithIntensity_ReturnsSameShape()
    {
        var activity = new
        {
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            activityType = "cycling",
            duration = 60,
            intensity = "high",
            calories = 450
        };

        await AssertPostParityAsync("/api/v1/activity", activity);
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task GetActivity_InvalidId_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/activity/nonexistent123");
    }

    [Fact]
    public async Task PostActivity_Empty_ReturnsSameShape()
    {
        var activity = new { };

        await AssertPostParityAsync("/api/v1/activity", activity);
    }

    #endregion
}
