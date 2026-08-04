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
    // IP literals only: a hostname would make the assertion depend on the test machine's
    // DNS, and resolution now fails closed.
    [Theory]
    [InlineData("https://93.184.216.34/nocturne")]
    [InlineData("http://93.184.216.34/hook")]
    [InlineData("https://8.8.8.8:8443/hook")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/hook")]
    public async Task IsAllowed_AllowsPubliclyRoutableHttpDestinations(string url) =>
        (await WebhookDestination.IsAllowedAsync(url)).Should().BeTrue();

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
    public async Task IsAllowed_RejectsInternalDestinations(string url) =>
        (await WebhookDestination.IsAllowedAsync(url)).Should().BeFalse();

    [Fact]
    public async Task IsAllowed_RejectsAnUnresolvableHost()
    {
        // Fail closed: a name this process cannot resolve may still be resolvable by the
        // HTTP stack (container DNS, service discovery), so "nothing to check" must deny.
        (await WebhookDestination.IsAllowedAsync("https://nonexistent.invalid/hook")).Should().BeFalse();
        (await WebhookDestination.IsAllowedAsync("http://_http.nocturne-api/hook")).Should().BeFalse();
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("ftp://example.com/")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public async Task IsAllowed_RejectsNonHttpSchemesAndMalformedUrls(string url) =>
        (await WebhookDestination.IsAllowedAsync(url)).Should().BeFalse();
}
