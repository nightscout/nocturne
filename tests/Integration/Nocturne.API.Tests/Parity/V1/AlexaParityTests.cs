using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/alexa endpoint.
/// Covers Alexa Smart Home skill integration.
/// </summary>
public class AlexaParityTests : ParityTestBase
{
    public AlexaParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // Alexa responses may include timestamps that differ
        return ComparisonOptions.Default.WithIgnoredFields(
            "sessionId",
            "requestId",
            "timestamp"
        );
    }

    #region POST /api/v1/alexa

    [Fact]
    public async Task PostAlexa_LaunchRequest_ReturnsSameShape()
    {
        var request = new
        {
            version = "1.0",
            session = new
            {
                @new = true,
                sessionId = "test-session-123",
                application = new
                {
                    applicationId = "amzn1.ask.skill.test"
                }
            },
            request = new
            {
                type = "LaunchRequest",
                requestId = "test-request-123",
                timestamp = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                locale = "en-US"
            }
        };

        await AssertPostParityAsync("/api/v1/alexa", request);
    }

    [Fact]
    public async Task PostAlexa_IntentRequest_NSStatus_ReturnsSameShape()
    {
        // First seed some data for the status
        await SeedEntrySequenceAsync(count: 3);

        var request = new
        {
            version = "1.0",
            session = new
            {
                @new = false,
                sessionId = "test-session-456"
            },
            request = new
            {
                type = "IntentRequest",
                requestId = "test-request-456",
                timestamp = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                locale = "en-US",
                intent = new
                {
                    name = "NSStatus"
                }
            }
        };

        await AssertPostParityAsync("/api/v1/alexa", request);
    }

    [Fact]
    public async Task PostAlexa_IntentRequest_MetricNow_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        var request = new
        {
            version = "1.0",
            session = new
            {
                @new = false,
                sessionId = "test-session-789"
            },
            request = new
            {
                type = "IntentRequest",
                requestId = "test-request-789",
                timestamp = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                locale = "en-US",
                intent = new
                {
                    name = "MetricNow",
                    slots = new
                    {
                        metric = new { value = "bg" }
                    }
                }
            }
        };

        await AssertPostParityAsync("/api/v1/alexa", request);
    }

    [Fact]
    public async Task PostAlexa_SessionEndedRequest_ReturnsSameShape()
    {
        var request = new
        {
            version = "1.0",
            session = new
            {
                sessionId = "test-session-end"
            },
            request = new
            {
                type = "SessionEndedRequest",
                requestId = "test-request-end",
                timestamp = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                locale = "en-US",
                reason = "USER_INITIATED"
            }
        };

        await AssertPostParityAsync("/api/v1/alexa", request);
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task PostAlexa_InvalidRequest_ReturnsSameShape()
    {
        var request = new
        {
            invalid = "data"
        };

        await AssertPostParityAsync("/api/v1/alexa", request);
    }

    [Fact]
    public async Task PostAlexa_MissingVersion_ReturnsSameShape()
    {
        var request = new
        {
            session = new { sessionId = "test" },
            request = new { type = "LaunchRequest" }
        };

        await AssertPostParityAsync("/api/v1/alexa", request);
    }

    [Fact]
    public async Task PostAlexa_UnknownIntent_ReturnsSameShape()
    {
        var request = new
        {
            version = "1.0",
            session = new { sessionId = "test" },
            request = new
            {
                type = "IntentRequest",
                intent = new { name = "UnknownIntent" }
            }
        };

        await AssertPostParityAsync("/api/v1/alexa", request);
    }

    #endregion
}
