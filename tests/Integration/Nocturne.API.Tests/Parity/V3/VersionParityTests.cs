using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V3;

/// <summary>
/// Parity tests for /api/v3/version endpoint.
/// Returns API version information.
/// </summary>
public class VersionParityTests : ParityTestBase
{
    public VersionParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // Version endpoint has fields that will differ between implementations
        return ComparisonOptions.Default.WithIgnoredFields(
            "version",        // Nocturne has different version string
            "apiVersion",     // May differ
            "srvDate",        // Current server timestamp
            "storage",        // Storage backend differs (PostgreSQL vs MongoDB)
            "head"            // Git commit hash
        );
    }

    #region GET /api/v3/version

    [Fact]
    public async Task GetVersion_ReturnsSameShape()
    {
        // /api/v3/version is public (no auth required)
        await AssertGetParityAsync("/api/v3/version");
    }

    [Fact]
    public async Task GetVersion_WithData_ReturnsSameShape()
    {
        // Seed some data to ensure version endpoint works with active system
        await SeedEntrySequenceAsync(count: 3);

        await AssertGetParityAsync("/api/v3/version");
    }

    #endregion
}
