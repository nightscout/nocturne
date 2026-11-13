using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.V4;
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

    [Theory]
    [InlineData("""{"enabled":false}""")]
    [InlineData("""{"Enabled":false}""")]
    [InlineData("""{"ENABLED":false}""")]
    public void ApplyJsonToConfig_ReadsTheEnabledFlagInAnyCasing(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var config = new DifferingNameConfig();
        ConnectorConfigurationBinder.ApplyJsonToConfig(doc, config);

        config.Enabled.Should().BeFalse("the stored flag is the same key whatever its casing");
    }

    [Fact]
    public void ApplyJsonToConfig_LeavesEnabledAloneWhenTheDocumentHasNoFlag()
    {
        using var doc = JsonDocument.Parse("""{"url":"https://example.com"}""");

        var config = new DifferingNameConfig();
        ConnectorConfigurationBinder.ApplyJsonToConfig(doc, config);

        config.Enabled.Should().BeTrue();
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

    [Theory]
    [InlineData("Smoothed", GlucoseProcessing.Smoothed)]
    [InlineData("Unsmoothed", GlucoseProcessing.Unsmoothed)]
    public void ApplyJsonToConfig_BindsAnEnumByTheNameTheSchemaOffers(string name, GlucoseProcessing expected)
    {
        using var doc = JsonDocument.Parse($$"""{"glucoseProcessing":"{{name}}"}""");
        var config = new DifferingNameConfig
        {
            GlucoseProcessing = expected == GlucoseProcessing.Smoothed
                ? GlucoseProcessing.Unsmoothed
                : GlucoseProcessing.Smoothed,
        };

        ConnectorConfigurationBinder.ApplyJsonToConfig(doc, config);

        config.GlucoseProcessing.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"glucoseProcessing":"Raw"}""")]
    [InlineData("""{"glucoseProcessing":"smoothed"}""")]
    [InlineData("""{"glucoseProcessing":1}""")]
    [InlineData("""{"glucoseProcessing":null}""")]
    public void ApplyJsonToConfig_LeavesAnEnumAloneWhenTheValueIsNotAName(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var config = new DifferingNameConfig { GlucoseProcessing = GlucoseProcessing.Unsmoothed };

        ConnectorConfigurationBinder.ApplyJsonToConfig(doc, config);

        config.GlucoseProcessing.Should().Be(GlucoseProcessing.Unsmoothed);
    }

    [Theory]
    [InlineData("""{"syncTempBasals":"no"}""", "syncTempBasals")]
    [InlineData("""{"syncIntervalMinutes":"5"}""", "syncIntervalMinutes")]
    [InlineData("""{"syncIntervalMinutes":5.5}""", "syncIntervalMinutes")]
    [InlineData("""{"url":3}""", "url")]
    [InlineData("""{"glucoseProcessing":"Raw"}""", "glucoseProcessing")]
    [InlineData("""{"Enabled":"yes"}""", "enabled")]
    public void UnbindableKeys_NamesTheKeysWhoseValueDoesNotConvert(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);

        ConnectorConfigurationBinder.UnbindableKeys(ConnectorConfigurationBinder.BoundKeyTypes(typeof(DifferingNameConfig)), doc.RootElement)
            .Should().Equal(key);
    }

    [Fact]
    public void UnbindableKeys_IsEmptyWhenEveryValueConverts()
    {
        using var doc = JsonDocument.Parse(
            """{"url":"u","syncTempBasals":false,"syncIntervalMinutes":5,"glucoseProcessing":"Unsmoothed","enabled":true,"removed":1}""");

        ConnectorConfigurationBinder.UnbindableKeys(ConnectorConfigurationBinder.BoundKeyTypes(typeof(DifferingNameConfig)), doc.RootElement)
            .Should().BeEmpty();
    }
}
