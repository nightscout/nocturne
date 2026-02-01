using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/status endpoints.
/// Verifies that Nocturne status responses match Nightscout 15.0.3 structure.
/// </summary>
public class StatusParityTests : ParityTestBase
{
    public StatusParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // Status endpoint has some fields that will always differ
        return ComparisonOptions.Default.WithIgnoredFields(
            "serverTime",      // Current server time
            "serverTimeEpoch", // Same as above in epoch
            "bgnow",           // Current BG calculation
            "delta",           // Current delta calculation
            "upbat",           // Uploader battery
            "version",         // Nocturne uses its own version number
            "name",            // Nocturne may be configured differently
            "head"             // Git commit hash will differ
        );
    }

    [Fact]
    public async Task GetStatus_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/status");
    }

    [Fact]
    public async Task GetStatusJson_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/status.json");
    }

    [Fact]
    public async Task GetStatus_WithEntries_ReturnsSameShape()
    {
        // Seed some data so status has something to report
        await SeedEntrySequenceAsync(count: 3);

        await AssertGetParityAsync("/api/v1/status");
    }
}
