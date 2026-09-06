using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Configurations;

public class TwiistConnectorConfigurationBindingTests
{
    [Fact]
    public void ApplyJsonToConfig_BindsPatientId_FromPersistedConfigKey()
    {
        // Shape persisted by the UI: the patient id lives under the "patientId" key
        // (the camel-cased ConnectorPropertyKey.PatientId). The binder resolves JSON keys
        // from the camel-cased property name, so the property must be named PatientId for the
        // value to bind. Before the rename it was PwdId -> looked for "pwdId" -> never matched,
        // so the connector synced with an empty id and hit /pwd//package (404).
        const string pwdUuid = "7c4a6533-b8db-4cc4-823e-6ac9c99e07e5";
        using var doc = JsonDocument.Parse(
            $$"""
            {
                "enabled": true,
                "username": "follower@example.com",
                "patientId": "{{pwdUuid}}",
                "syncGlucose": true
            }
            """);

        var config = new TwiistConnectorConfiguration();
        ConnectorConfigurationBinder.ApplyJsonToConfig(doc, config);

        config.PatientId.Should().Be(pwdUuid);
        config.Username.Should().Be("follower@example.com");
    }

    [Fact]
    public void ConnectorProperties_HaveNameMatchingConfigKey()
    {
        // The binder keys off the camel-cased property NAME while values are persisted under the
        // camel-cased ConnectorProperty KEY. If they diverge for any property, that value silently
        // never binds. Guard the whole config so a future rename can't reintroduce the bug.
        var mismatches = new List<string>();

        foreach (var property in typeof(TwiistConnectorConfiguration)
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
