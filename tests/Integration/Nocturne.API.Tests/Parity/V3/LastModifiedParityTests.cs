using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V3;

/// <summary>
/// Parity tests for /api/v3/lastModified endpoint.
/// Returns last modification timestamps for each collection.
/// </summary>
public class LastModifiedParityTests : ParityTestBase
{
    public LastModifiedParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // LastModified returns timestamps which will be dynamic
        // We verify structure but not exact timestamp values
        return ComparisonOptions.Default.WithIgnoredFields(
            "srvDate",
            "collections" // The timestamps within will differ
        );
    }

    #region GET /api/v3/lastModified

    [Fact]
    public async Task GetLastModified_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/lastModified");
    }

    [Fact]
    public async Task GetLastModified_WithEntries_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        await AssertGetParityAsync("/api/v3/lastModified");
    }

    [Fact]
    public async Task GetLastModified_WithTreatments_ReturnsSameShape()
    {
        var treatment = TestDataFactory.CreateTreatment(eventType: "Correction Bolus", insulin: 2.0);
        await SeedTreatmentsAsync(treatment);

        await AssertGetParityAsync("/api/v3/lastModified");
    }

    [Fact]
    public async Task GetLastModified_WithDeviceStatus_ReturnsSameShape()
    {
        var status = TestDataFactory.CreateDeviceStatus();
        await SeedDeviceStatusAsync(status);

        await AssertGetParityAsync("/api/v3/lastModified");
    }

    [Fact]
    public async Task GetLastModified_WithMultipleCollections_ReturnsSameShape()
    {
        // Seed data to multiple collections
        await SeedEntrySequenceAsync(count: 2);

        var treatment = TestDataFactory.CreateTreatment();
        await SeedTreatmentsAsync(treatment);

        var status = TestDataFactory.CreateDeviceStatus();
        await SeedDeviceStatusAsync(status);

        await AssertGetParityAsync("/api/v3/lastModified");
    }

    #endregion

    #region Collection-Specific LastModified

    [Fact]
    public async Task GetLastModified_EntriesCollection_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        // Some implementations support filtering by collection
        await AssertGetParityAsync("/api/v3/lastModified?collection=entries");
    }

    [Fact]
    public async Task GetLastModified_TreatmentsCollection_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus"),
            TestDataFactory.CreateTreatment(eventType: "Meal Bolus")
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v3/lastModified?collection=treatments");
    }

    #endregion

    #region Response Headers

    [Fact]
    public async Task GetLastModified_IncludesHeaders_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        // LastModified endpoint should include relevant caching headers
        var headers = new Dictionary<string, string>();

        await AssertParityAsync(HttpMethod.Get, "/api/v3/lastModified", headers: headers);
    }

    #endregion
}
