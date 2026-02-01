using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/iob/* endpoints.
/// Covers IOB calculations: GET, GET/treatments, GET/hourly
/// </summary>
public class IobParityTests : ParityTestBase
{
    public IobParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // IOB calculations may have minor floating point differences
        // but the structure should match exactly
        return ComparisonOptions.Default;
    }

    #region Data Setup

    private async Task SeedIobTestDataAsync()
    {
        // Seed a profile (required for IOB calculations)
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

        // Seed treatments with insulin
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 2.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1)),
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 4.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-2)),
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 1.5,
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-30))
        };
        await SeedTreatmentsAsync(treatments);
    }

    #endregion

    #region GET /api/v1/iob

    [Fact]
    public async Task GetIob_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/iob");
    }

    [Fact]
    public async Task GetIob_WithData_ReturnsSameShape()
    {
        await SeedIobTestDataAsync();

        await AssertGetParityAsync("/api/v1/iob");
    }

    [Fact]
    public async Task GetIob_WithCount_ReturnsSameShape()
    {
        await SeedIobTestDataAsync();

        await AssertGetParityAsync("/api/v1/iob?count=5");
    }

    #endregion

    #region GET /api/v1/iob/treatments

    [Fact]
    public async Task GetIobTreatments_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/iob/treatments");
    }

    [Fact]
    public async Task GetIobTreatments_WithData_ReturnsSameShape()
    {
        await SeedIobTestDataAsync();

        await AssertGetParityAsync("/api/v1/iob/treatments");
    }

    [Fact]
    public async Task GetIobTreatments_WithCount_ReturnsSameShape()
    {
        await SeedIobTestDataAsync();

        await AssertGetParityAsync("/api/v1/iob/treatments?count=10");
    }

    #endregion

    #region GET /api/v1/iob/hourly

    [Fact]
    public async Task GetIobHourly_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/iob/hourly");
    }

    [Fact]
    public async Task GetIobHourly_WithData_ReturnsSameShape()
    {
        await SeedIobTestDataAsync();

        await AssertGetParityAsync("/api/v1/iob/hourly");
    }

    [Fact]
    public async Task GetIobHourly_WithHours_ReturnsSameShape()
    {
        await SeedIobTestDataAsync();

        await AssertGetParityAsync("/api/v1/iob/hourly?hours=6");
    }

    [Fact]
    public async Task GetIobHourly_With24Hours_ReturnsSameShape()
    {
        await SeedIobTestDataAsync();

        await AssertGetParityAsync("/api/v1/iob/hourly?hours=24");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task GetIob_NoProfile_ReturnsSameShape()
    {
        // Only seed treatments, no profile
        var treatment = TestDataFactory.CreateTreatment(insulin: 2.0);
        await SeedTreatmentsAsync(treatment);

        await AssertGetParityAsync("/api/v1/iob");
    }

    [Fact]
    public async Task GetIob_ZeroInsulin_ReturnsSameShape()
    {
        // Seed treatments without insulin
        var treatment = TestDataFactory.CreateTreatment(eventType: "Note", insulin: null);
        await SeedTreatmentsAsync(treatment);

        await AssertGetParityAsync("/api/v1/iob");
    }

    #endregion
}
