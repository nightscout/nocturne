using FluentAssertions;
using Nocturne.Core.Models.Connectors;
using Xunit;

namespace Nocturne.Core.Models.Tests.Connectors;

[Trait("Category", "Unit")]
public class ConnectorErrorCategoryTests
{
    [Fact]
    public void AHealthyConnectorWithNoMessageHasNone() =>
        ConnectorErrorCategory.Classify(isHealthy: true, null).Should().Be(ConnectorErrorCategory.None);

    [Fact]
    public void AnUnhealthyConnectorWithNoMessageIsUnknown() =>
        ConnectorErrorCategory.Classify(isHealthy: false, "  ").Should().Be(ConnectorErrorCategory.Unknown);

    [Theory]
    [InlineData("HttpRequestException: Response status code does not indicate success: 429 (Too Many Requests).", "rate_limited")]
    [InlineData("Glooko is throttling requests", "rate_limited")]
    [InlineData("Rate limit reached, retry later", "rate_limited")]
    [InlineData("Data fetch failed: Response status code does not indicate success: 401 (Unauthorized).", "auth")]
    [InlineData("HTTP 403 from the vendor", "auth")]
    [InlineData("Dexcom did not accept this sign-in. Check the username and password in the connector settings and save them again.", "auth")]
    [InlineData("MyLife authentication failed; see connector logs for the failing step", "auth")]
    [InlineData("Re-authentication failed after Glooko rejected the session", "auth")]
    [InlineData("invalid_grant: refresh token expired", "auth")]
    [InlineData("HttpRequestException: Response status code does not indicate success: 503 (Service Unavailable).", "upstream")]
    [InlineData("Response status code does not indicate success: 504 (Gateway Timeout).", "upstream")]
    [InlineData("Could not sign in to Dexcom. This is usually temporary; the next sync will try again.", "upstream")]
    [InlineData("No data returned from any CareLink endpoint", "upstream")]
    [InlineData("CareLink sent its newer data format (patientData), which this version cannot read", "upstream")]
    [InlineData("JsonException: '<' is an invalid start of a value.", "upstream")]
    [InlineData("The Nightscout URL must be an http or https address.", "config")]
    [InlineData("UriFormatException: Invalid URI: The hostname could not be parsed.", "config")]
    [InlineData("Connector is not configured", "config")]
    [InlineData("HttpRequestException: No such host is known. (share2.dexcom.com:443)", "network")]
    [InlineData("SocketException: Connection refused", "network")]
    [InlineData("The dexcom sync exceeded 00:05:00.", "network")]
    [InlineData("Failed to sync Glucose: The SSL connection could not be established, see inner exception.", "network")]
    [InlineData("Sync failed", "unknown")]
    [InlineData("Chunk 3/500 failed (2026-11-01 to 2026-11-02)", "unknown")]
    public void MapsAMessageToItsCategory(string message, string expected) =>
        ConnectorErrorCategory.Classify(isHealthy: false, message).Should().Be(expected);

    [Fact]
    public void AMessageLeftOnAHealthyConnectorIsStillClassified() =>
        ConnectorErrorCategory.Classify(isHealthy: true, "SocketException: Connection reset by peer")
            .Should().Be(ConnectorErrorCategory.Network);
}
