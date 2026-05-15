using FluentAssertions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class ConnectorServerResolverTests
{
    private static readonly Dictionary<string, string> DexcomMapping = new()
    {
        ["US"] = "share2.dexcom.com",
        ["EU"] = "shareous1.dexcom.com"
    };

    private static string GetServer(BaseConnectorConfiguration config) => ((TestConfig)config).Server;

    private static TestConfig CreateConfig(string server = "US") => new() { Server = server };

    [Fact]
    public void Resolve_ReturnsCorrectUri_WhenConfigIsUs()
    {
        var resolver = new ConnectorServerResolver<TestConfig>(DexcomMapping, GetServer, "share2.dexcom.com");
        var config = CreateConfig("US");

        var result = resolver.Resolve(config);

        result.Should().NotBeNull();
        result!.Host.Should().Be("share2.dexcom.com");
    }

    [Fact]
    public void Resolve_ReturnsCorrectUri_WhenConfigIsEu()
    {
        var resolver = new ConnectorServerResolver<TestConfig>(DexcomMapping, GetServer, "share2.dexcom.com");
        var config = CreateConfig("EU");

        var result = resolver.Resolve(config);

        result.Should().NotBeNull();
        result!.Host.Should().Be("shareous1.dexcom.com");
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoMappingConfigured()
    {
        var resolver = new ConnectorServerResolver<TestConfig>(null, null, null);
        var config = CreateConfig();

        var result = resolver.Resolve(config);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_ReturnsDefaultServer_WhenNoMapping()
    {
        var resolver = new ConnectorServerResolver<TestConfig>(null, null, "api.example.com");
        var config = CreateConfig();

        var result = resolver.Resolve(config);

        result.Should().NotBeNull();
        result!.Host.Should().Be("api.example.com");
    }

    [Fact]
    public void BuildUrl_CombinesBaseAndPath()
    {
        var resolver = new ConnectorServerResolver<TestConfig>(DexcomMapping, GetServer, "share2.dexcom.com");
        var config = CreateConfig("US");

        var result = resolver.BuildUrl(config, "/ShareWebServices/Auth");

        result.Should().Be("https://share2.dexcom.com/ShareWebServices/Auth");
    }

    [Fact]
    public void BuildUrl_ReturnsPathOnly_WhenNoResolver()
    {
        var resolver = new ConnectorServerResolver<TestConfig>(null, null, null);
        var config = CreateConfig();

        var result = resolver.BuildUrl(config, "/api/v1/data");

        result.Should().Be("/api/v1/data");
    }

    [Fact]
    public void Resolve_AddsHttpsScheme_WhenMissing()
    {
        var resolver = new ConnectorServerResolver<TestConfig>(DexcomMapping, GetServer, "share2.dexcom.com");
        var config = CreateConfig("US");

        var result = resolver.Resolve(config);

        result.Should().NotBeNull();
        result!.Scheme.Should().Be("https");
        result.Host.Should().Be("share2.dexcom.com");
    }

    [Fact]
    public void Resolve_PreservesScheme_WhenPresent()
    {
        var mapping = new Dictionary<string, string> { ["US"] = "https://share2.dexcom.com" };
        var resolver = new ConnectorServerResolver<TestConfig>(mapping, GetServer, "https://share2.dexcom.com");
        var config = CreateConfig("US");

        var result = resolver.Resolve(config);

        result.Should().NotBeNull();
        result!.Scheme.Should().Be("https");
        result.Host.Should().Be("share2.dexcom.com");
    }

    private class TestConfig : BaseConnectorConfiguration
    {
        public string Server { get; set; } = "US";

        // BaseConnectorConfiguration requires ConnectSource
        public TestConfig() => ConnectSource = ConnectSource.Dexcom;
    }
}
