using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V3;

/// <summary>
/// Parity tests for /api/v3/status endpoint.
/// Returns server status including API version and enabled features.
/// </summary>
public class StatusParityTests : ParityTestBase
{
    public StatusParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // Status responses include server-specific values that will differ
        return ComparisonOptions.Default.WithIgnoredFields(
            "version",           // Different version strings
            "apiVersion",        // May differ between implementations
            "serverTime",        // Current server time
            "serverTimeEpoch",   // Same as above in epoch
            "srvDate",           // Server date
            "storage",           // Storage backend (PostgreSQL vs MongoDB)
            "head",              // Git commit hash
            "settings"           // Server-specific settings may differ
        );
    }

    #region GET /api/v3/status

    [Fact]
    public async Task GetStatus_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/status");
    }

    [Fact]
    public async Task GetStatus_WithData_ReturnsSameShape()
    {
        // Seed some data to ensure status reflects active system
        await SeedEntrySequenceAsync(count: 3);

        await AssertGetParityAsync("/api/v3/status");
    }

    [Fact]
    public async Task GetStatus_WithTreatments_ReturnsSameShape()
    {
        var treatment = TestDataFactory.CreateTreatment(eventType: "Correction Bolus", insulin: 2.0);
        await SeedTreatmentsAsync(treatment);

        await AssertGetParityAsync("/api/v3/status");
    }

    #endregion

    #region Response Structure Verification

    [Fact]
    public async Task GetStatus_HasRequiredFields_ReturnsSameShape()
    {
        // This test verifies both systems include the same top-level fields
        // The actual values may differ but structure should match
        await AssertGetParityAsync("/api/v3/status");
    }

    #endregion
}
