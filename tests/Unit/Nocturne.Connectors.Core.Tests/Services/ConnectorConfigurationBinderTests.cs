using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class ConnectorConfigurationBinderTests
{
    private class DifferingNameConfig : BaseConnectorConfiguration
    {
        [ConnectorProperty(ConnectorPropertyKey.AccessToken, Required = true, Secret = true)]
        public string TokenPropertyWithDifferentName { get; set; } = string.Empty;

        [ConnectorProperty(ConnectorPropertyKey.Url, Required = true)]
        public string CustomUrlProperty { get; set; } = string.Empty;

        public string UnattributedProperty { get; set; } = string.Empty;
    }

    [Fact]
    public void ApplySecretsToConfig_BindsViaAttributeKeyName()
    {
        const string secretValue = "noc_secret_12345";
        var secrets = new Dictionary<string, string>
        {
            ["accessToken"] = secretValue,
        };

        var config = new DifferingNameConfig();
        ConnectorConfigurationBinder.ApplySecretsToConfig(secrets, config);

        config.TokenPropertyWithDifferentName.Should().Be(
            secretValue,
            "binder should resolve key name from [ConnectorProperty(ConnectorPropertyKey.AccessToken)] attribute");
    }

    [Fact]
    public void ApplyJsonToConfig_BindsViaAttributeKeyName()
    {
        const string url = "https://example.com";
        const string plainValue = "direct-property-value";
        using var doc = JsonDocument.Parse(
            $$"""
            {
                "url": "{{url}}",
                "unattributedProperty": "{{plainValue}}"
            }
            """);

        var config = new DifferingNameConfig();
        ConnectorConfigurationBinder.ApplyJsonToConfig(doc, config);

        config.CustomUrlProperty.Should().Be(
            url,
            "binder should resolve key name from [ConnectorProperty(ConnectorPropertyKey.Url)] attribute");
        config.UnattributedProperty.Should().Be(
            plainValue,
            "binder should fall back to property name when no [ConnectorProperty] is present");
    }
}
