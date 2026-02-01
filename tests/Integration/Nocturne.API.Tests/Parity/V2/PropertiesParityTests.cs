using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V2;

/// <summary>
/// Parity tests for /api/v2/properties endpoints.
/// V2 Properties API provides client properties and settings:
/// - GET /api/v2/properties - Get all properties
/// - GET /api/v2/properties/{path} - Get specific properties
/// </summary>
public class PropertiesParityTests : ParityTestBase
{
    public PropertiesParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // Properties responses contain dynamic computed values
        return ComparisonOptions.Default.WithIgnoredFields(
            "serverTime",
            "serverTimeEpoch",
            "uptime",
            "lastUpdated"
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

    #region GET /api/v2/properties

    [Fact]
    public async Task GetAllProperties_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/properties");
    }

    [Fact]
    public async Task GetAllProperties_WithData_ReturnsSameShape()
    {
        // Seed some data so properties have values to compute
        await SeedEntrySequenceAsync(count: 5);
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v2/properties");
    }

    [Fact]
    public async Task GetAllProperties_Pretty_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        await AssertGetParityAsync("/api/v2/properties?pretty=true");
    }

    #endregion

    #region GET /api/v2/properties/{path}

    [Fact]
    public async Task GetSpecificProperty_Iob_ReturnsSameShape()
    {
        // Seed insulin treatments for IOB calculation
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 2.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1)),
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-2))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v2/properties/iob");
    }

    [Fact]
    public async Task GetSpecificProperty_Cob_ReturnsSameShape()
    {
        // Seed treatments for COB calculation (Meal Bolus without carbs will have 0 COB)
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1)),
            TestDataFactory.CreateTreatment(
                eventType: "Carb Correction",
                insulin: 1.0,
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-30))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v2/properties/cob");
    }

    [Fact]
    public async Task GetSpecificProperty_Basal_ReturnsSameShape()
    {
        await SeedProfileForTestAsync();

        await AssertGetParityAsync("/api/v2/properties/basal");
    }

    [Fact]
    public async Task GetSpecificProperty_Bwp_ReturnsSameShape()
    {
        // BWP (Bolus Wizard Preview) requires entries and profile
        await SeedEntrySequenceAsync(count: 5);
        await SeedProfileForTestAsync();

        await AssertGetParityAsync("/api/v2/properties/bwp");
    }

    [Fact]
    public async Task GetSpecificProperty_Cage_ReturnsSameShape()
    {
        // CAGE (Cannula Age) requires site change treatment
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Site Change",
                timestamp: TestTimeProvider.GetTestTime().AddDays(-2))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v2/properties/cage");
    }

    [Fact]
    public async Task GetSpecificProperty_Sage_ReturnsSameShape()
    {
        // SAGE (Sensor Age) requires sensor start treatment
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Sensor Start",
                timestamp: TestTimeProvider.GetTestTime().AddDays(-5))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v2/properties/sage");
    }

    [Fact]
    public async Task GetMultipleProperties_CommaSeparated_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 2.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1))
        };
        await SeedTreatmentsAsync(treatments);
        await SeedProfileForTestAsync();

        await AssertGetParityAsync("/api/v2/properties/iob,cob,basal");
    }

    [Fact]
    public async Task GetSpecificProperty_Pretty_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        await AssertGetParityAsync("/api/v2/properties/iob?pretty=true");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task GetSpecificProperty_NonExistent_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/properties/nonexistentproperty");
    }

    [Fact]
    public async Task GetSpecificProperty_EmptyPath_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/properties/");
    }

    [Fact]
    public async Task GetMultipleProperties_SomeInvalid_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        await AssertGetParityAsync("/api/v2/properties/iob,invalidprop,cob");
    }

    #endregion
}
