using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
/// The base address a connector's client is registered with. A tenant-supplied value reaches this
/// through <c>TenantUrlConnectorInstaller</c>, so it may be a bare host.
/// </summary>
public class ConnectorClientBaseAddressTests
{
    private const string ClientName = "test-connector-client";

    [Theory]
    [InlineData("httpbin.example.com", "https://httpbin.example.com/")]
    [InlineData("http://api.example.com", "http://api.example.com/")]
    // A base address's path is made to end in a slash, so a relative request resolves under the
    // whole path instead of dropping its last segment.
    [InlineData("https://api.example.com/ns/", "https://api.example.com/ns/")]
    [InlineData("api.example.com/ns", "https://api.example.com/ns/")]
    [InlineData("https://api.example.com/ns", "https://api.example.com/ns/")]
    // The slash goes on the path, not on the end of the value.
    [InlineData("https://api.example.com/ns?a=b", "https://api.example.com/ns/?a=b")]
    [InlineData("https://api.example.com?a=b", "https://api.example.com/?a=b")]
    public void ConnectorClient_ResolvesItsBaseAddress(string configured, string expected)
    {
        BaseAddressFor(configured).Should().Be(expected);
    }

    private static string? BaseAddressFor(string? configured)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(ClientName).ConfigureConnectorClient(configured);

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        return client.BaseAddress?.ToString();
    }
}
