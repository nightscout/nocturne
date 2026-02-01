using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V2;

/// <summary>
/// Parity tests for /api/v2/loop endpoints.
/// Loop API provides APNS integration for iOS Loop app:
/// - POST /api/v2/loop/send - Send Loop notification via APNS
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

    #region POST /api/v2/loop/send

    [Fact]
    public async Task PostLoopSend_Empty_ReturnsSameShape()
    {
        var request = new { };

        await AssertPostParityAsync("/api/v2/loop/send", request);
    }

    [Fact]
    public async Task PostLoopSend_MissingData_ReturnsSameShape()
    {
        var request = new
        {
            loopSettings = new
            {
                deviceToken = "test-device-token",
                bundleIdentifier = "com.loopkit.Loop"
            }
        };

        await AssertPostParityAsync("/api/v2/loop/send", request);
    }

    [Fact]
    public async Task PostLoopSend_MissingLoopSettings_ReturnsSameShape()
    {
        var request = new
        {
            data = new
            {
                title = "Test Alert",
                message = "Test loop notification",
                level = 1
            }
        };

        await AssertPostParityAsync("/api/v2/loop/send", request);
    }

    [Fact]
    public async Task PostLoopSend_Complete_ReturnsSameShape()
    {
        var request = new
        {
            data = new
            {
                title = "Low Blood Sugar",
                message = "Blood sugar is 65 mg/dL",
                level = 2,
                sound = "alarm"
            },
            loopSettings = new
            {
                deviceToken = "test-device-token-1234567890",
                bundleIdentifier = "com.loopkit.Loop"
            }
        };

        await AssertPostParityAsync("/api/v2/loop/send", request);
    }

    [Fact]
    public async Task PostLoopSend_WithSoundOptions_ReturnsSameShape()
    {
        var request = new
        {
            data = new
            {
                title = "Urgent Alert",
                message = "Immediate attention required",
                level = 3,
                sound = "critical",
                vibrate = true
            },
            loopSettings = new
            {
                deviceToken = "abcdef1234567890abcdef1234567890",
                bundleIdentifier = "com.loopkit.Loop.dev"
            }
        };

        await AssertPostParityAsync("/api/v2/loop/send", request);
    }

    [Fact]
    public async Task PostLoopSend_InvalidDeviceToken_ReturnsSameShape()
    {
        var request = new
        {
            data = new
            {
                title = "Test",
                message = "Test message"
            },
            loopSettings = new
            {
                deviceToken = "",  // Empty device token
                bundleIdentifier = "com.loopkit.Loop"
            }
        };

        await AssertPostParityAsync("/api/v2/loop/send", request);
    }

    #endregion
}
