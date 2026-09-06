using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Xunit;

namespace Nocturne.Connectors.NocturneRemote.Tests.Configurations;

public class NocturneRemoteConnectorConfigurationBindingTests
{
    [Fact]
    public void ApplySecretsToConfig_BindsAccessToken_FromPersistedSecretKey()
    {
        const string secretToken = "noc_secret_token_12345";
        var secrets = new Dictionary<string, string>
        {
            ["accessToken"] = secretToken,
        };

        var config = new NocturneRemoteConnectorConfiguration();
        ConnectorConfigurationBinder.ApplySecretsToConfig(secrets, config);

        config.AccessToken.Should().Be(secretToken);
    }

    [Fact]
    public void ApplyJsonToConfig_BindsUrlAndSettings_FromPersistedConfigKey()
    {
        const string remoteUrl = "https://nocturne.example.com";
        using var doc = JsonDocument.Parse(
            $$"""
            {
                "enabled": true,
                "url": "{{remoteUrl}}",
                "maxCount": 250
            }
            """);

        var config = new NocturneRemoteConnectorConfiguration();
        ConnectorConfigurationBinder.ApplyJsonToConfig(doc, config);

        config.Url.Should().Be(remoteUrl);
        config.MaxCount.Should().Be(250);
        config.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ConnectorProperties_HaveNameMatchingConfigKey()
    {
        // The binder keys off the camel-cased property NAME while values are persisted under the
        // camel-cased ConnectorProperty KEY. If they diverge for any property, that value silently
        // never binds. Guard the whole config so a future rename can't reintroduce the bug.
        var mismatches = new List<string>();

        foreach (var property in typeof(NocturneRemoteConnectorConfiguration)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = property.GetCustomAttribute<ConnectorPropertyAttribute>();
            if (attr is null)
                continue;

            var nameKey = Camel(property.Name);
            var configKey = Camel(attr.GetKeyName());
            if (nameKey != configKey)
                mismatches.Add($"{property.Name} binds '{nameKey}' but persists '{configKey}'");
        }

        mismatches.Should().BeEmpty();
    }

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
}
