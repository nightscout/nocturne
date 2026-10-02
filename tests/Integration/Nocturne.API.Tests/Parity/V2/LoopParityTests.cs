using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V2;

/// <summary>
/// Parity tests for /api/v2/loop endpoints.
/// - GET /api/v2/loop/status - Get Loop service configuration status
/// </summary>
public class LoopParityTests : ParityTestBase
{
    public LoopParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // Loop responses contain dynamic timestamps
        return ComparisonOptions.Default.WithIgnoredFields(
            "timestamp",
            "sent",
            "deliveredAt"
        );
    }

    #region GET /api/v2/loop/status

    [Fact]
    public async Task GetLoopStatus_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/loop/status");
    }

    #endregion
}
