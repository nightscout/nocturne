using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Nocturne.API.Tests.Extensions;

public class ServiceDefaultsOtlpConfigurationTests
{
    private static IConfiguration Config(string? endpoint) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint }
            )
            .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsOtlpConfigured_IsFalse_WhenEndpointMissingOrBlank(string? endpoint)
    {
        Config(endpoint).IsOtlpConfigured().Should().BeFalse();
    }

    [Fact]
    public void IsOtlpConfigured_IsTrue_WhenEndpointSet()
    {
        Config("http://otel-collector:4317").IsOtlpConfigured().Should().BeTrue();
    }
}
