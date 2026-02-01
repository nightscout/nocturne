using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V2;

/// <summary>
/// Parity tests for /api/v2/summary endpoints.
/// V2 Summary API provides aggregated data:
/// - GET /api/v2/summary - Get summary data for time window
/// </summary>
public class SummaryParityTests : ParityTestBase
{
    public SummaryParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // Summary responses contain dynamic timestamps and computed values
        return ComparisonOptions.Default.WithIgnoredFields(
            "serverTime",
            "serverTimeEpoch",
            "lastUpdated",
            "now"
        );
    }

    #region Helper Methods

    private async Task SeedProfileForTestAsync()
    {
        var profile = TestDataFactory.CreateProfile();
        var nsProfile = new
        {
            defaultProfile = profile.DefaultProfile,
            startDate = profile.StartDate,
            mills = profile.Mills,
            units = profile.Units,
            store = profile.Store
        };

        await NightscoutClient.PostAsJsonAsync("/api/v1/profile", nsProfile);
        await NocturneClient.PostAsJsonAsync("/api/v1/profile", profile);
    }

    #endregion

    #region GET /api/v2/summary

    [Fact]
    public async Task GetSummary_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/summary");
    }

    [Fact]
    public async Task GetSummary_DefaultHours_ReturnsSameShape()
    {
        // Seed data within the default 6-hour window
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v2/summary");
    }

    [Fact]
    public async Task GetSummary_WithHoursParam_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v2/summary?hours=12");
    }

    [Fact]
    public async Task GetSummary_ShortWindow_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v2/summary?hours=1");
    }

    [Fact]
    public async Task GetSummary_LongWindow_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 20);

        await AssertGetParityAsync("/api/v2/summary?hours=24");
    }

    [Fact]
    public async Task GetSummary_WithTreatments_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1)),
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 2.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-2)),
            TestDataFactory.CreateTreatment(
                eventType: "Temp Basal",
                insulin: 0.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-3))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v2/summary");
    }

    [Fact]
    public async Task GetSummary_WithProfile_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);
        await SeedProfileForTestAsync();

        await AssertGetParityAsync("/api/v2/summary");
    }

    [Fact]
    public async Task GetSummary_WithDeviceStatus_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);
        var deviceStatuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(
                device: "openaps://TestPump",
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-5)),
            TestDataFactory.CreateDeviceStatus(
                device: "loop://TestLoop",
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-10))
        };
        await SeedDeviceStatusAsync(deviceStatuses);

        await AssertGetParityAsync("/api/v2/summary");
    }

    [Fact]
    public async Task GetSummary_ComprehensiveData_ReturnsSameShape()
    {
        // Seed all data types for comprehensive summary
        await SeedEntrySequenceAsync(count: 20);

        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1)),
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 2.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-2)),
            TestDataFactory.CreateTreatment(
                eventType: "Site Change",
                timestamp: TestTimeProvider.GetTestTime().AddDays(-2)),
            TestDataFactory.CreateTreatment(
                eventType: "Sensor Start",
                timestamp: TestTimeProvider.GetTestTime().AddDays(-5))
        };
        await SeedTreatmentsAsync(treatments);

        var deviceStatuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(
                device: "openaps://TestPump",
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-5))
        };
        await SeedDeviceStatusAsync(deviceStatuses);

        await SeedProfileForTestAsync();

        await AssertGetParityAsync("/api/v2/summary?hours=24");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task GetSummary_InvalidHours_Zero_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/summary?hours=0");
    }

    [Fact]
    public async Task GetSummary_InvalidHours_Negative_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/summary?hours=-1");
    }

    [Fact]
    public async Task GetSummary_InvalidHours_String_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/summary?hours=invalid");
    }

    [Fact]
    public async Task GetSummary_VeryLargeHours_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v2/summary?hours=10000");
    }

    #endregion
}
