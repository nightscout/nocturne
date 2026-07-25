using FluentAssertions;
using Nocturne.API.Services.Alerts.Webhooks;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Webhooks;

/// <summary>
/// The webhook sender runs inside the deployment's network, so a member-supplied URL
/// must not be able to reach the database, a sibling service, or the cloud metadata
/// endpoint.
/// </summary>
public class WebhookDestinationTests
{
    [Theory]
    [InlineData("https://hooks.example.com/nocturne")]
    [InlineData("http://93.184.216.34/hook")]
    public void IsAllowed_AllowsPubliclyRoutableHttpDestinations(string url) =>
        WebhookDestination.IsAllowed(url).Should().BeTrue();

    [Theory]
    [InlineData("http://127.0.0.1:1610/api/v4/admin/demo/reset")] // loopback: the API itself
    [InlineData("http://localhost:5432/")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]      // cloud metadata
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.4.2/")]
    [InlineData("http://192.168.1.10/")]
    [InlineData("http://100.100.0.1/")]                            // carrier-grade NAT
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[fd00::1]/")]                              // IPv6 unique-local
    public void IsAllowed_RejectsInternalDestinations(string url) =>
        WebhookDestination.IsAllowed(url).Should().BeFalse();

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("ftp://example.com/")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void IsAllowed_RejectsNonHttpSchemesAndMalformedUrls(string url) =>
        WebhookDestination.IsAllowed(url).Should().BeFalse();
}
