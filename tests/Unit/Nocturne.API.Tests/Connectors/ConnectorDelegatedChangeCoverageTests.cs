using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// <see cref="DelegatedConfigurationGuard"/> reads property metadata, so a connector added later is
/// covered only if its destination and its outbound switches carry that metadata. This sweeps every
/// shipped connector configuration and proves the guard refuses each one to a delegated credential.
/// </summary>
public class ConnectorDelegatedChangeCoverageTests
{
    private static readonly string[] DestinationNameParts = ["Url", "Uri", "Host", "Server", "Region", "Endpoint"];

    private static IEnumerable<Type> ConfigurationTypes() =>
        ConnectorInstallers.Types()
            .Where(t => t.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false) is not null)
            .DistinctBy(t => t.FullName);

    private static IEnumerable<(Type Type, PropertyInfo Property, ConnectorPropertyAttribute Attribute)> Properties() =>
        ConfigurationTypes().SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => (Type: type, Property: p, Attribute: p.GetCustomAttribute<ConnectorPropertyAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Type, x.Property, x.Attribute!)));

    private static string Key(PropertyInfo property)
    {
        var name = property.GetCustomAttribute<ConnectorPropertyAttribute>()!.GetKeyName();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    [Fact]
    public void EveryUriProperty_IsRefusedToADelegatedCredentialWhileASecretIsHeld()
    {
        var uris = Properties().Where(x => x.Attribute.Format == "uri").ToList();

        uris.Select(x => $"{x.Type.Name}.{x.Property.Name}").Should().Contain(
            ["NightscoutConnectorConfiguration.Url", "NightscoutConnectorConfiguration.RealtimeUrl",
             "NocturneRemoteConnectorConfiguration.Url", "MyLifeConnectorConfiguration.ServiceUrl"],
            "the sweep must find the known destinations, or it passes vacuously");

        foreach (var (type, property, _) in uris)
        {
            property.PropertyType.Should().Be(typeof(string), $"{type.Name}.{property.Name} is a URL");
            var key = Key(property);

            DelegatedConfigurationGuard.RefusedKeys(
                    Activator.CreateInstance(type)!,
                    JsonDocument.Parse($$"""{"{{key}}":"https://mine.example"}"""),
                    JsonDocument.Parse($$"""{"{{key}}":"https://other.example"}"""),
                    holdsStoredSecret: true)
                .Should().Contain(key, $"{type.Name}.{property.Name} chooses where the stored secret is sent");

            DelegatedConfigurationGuard.RefusedKeys(
                    Activator.CreateInstance(type)!,
                    null,
                    JsonDocument.Parse($$"""{"{{key}}":"https://other.example"}"""),
                    holdsStoredSecret: false)
                .Should().NotContain(key, "setting the destination before any secret is stored is first-time setup");
        }
    }

    [Fact]
    public void EveryPropertyThatSendsRecords_IsABooleanADelegatedCredentialCannotTurnOn()
    {
        var outbound = Properties().Where(x => x.Attribute.SendsRecords).ToList();

        outbound.Select(x => $"{x.Type.Name}.{x.Property.Name}").Should().Contain(
            "NightscoutConnectorConfiguration.WriteBackEnabled",
            "the sweep must find Nightscout write-back, or it passes vacuously");

        foreach (var (type, property, _) in outbound)
        {
            property.PropertyType.Should().Be(typeof(bool), $"{type.Name}.{property.Name} is a switch");
            var key = Key(property);

            DelegatedConfigurationGuard.RefusedKeys(
                    Activator.CreateInstance(type)!, null, JsonDocument.Parse($$"""{"{{key}}":true}"""), false)
                .Should().Contain(key, $"turning {type.Name}.{property.Name} on sends the tenant's records out");
        }
    }

    /// <summary>
    /// A host typed as free text but not marked as a URL would escape the guard. Every property
    /// named like a destination is either a URL or a choice among the vendor's own hosts.
    /// </summary>
    [Fact]
    public void EveryPropertyNamedLikeADestination_IsAUrlOrAFixedChoice()
    {
        var unmarked = Properties()
            .Where(x => DestinationNameParts.Any(part => x.Property.Name.Contains(part, StringComparison.Ordinal)))
            .Where(x => x.Property.PropertyType == typeof(string)
                        && x.Attribute.Format != "uri"
                        && x.Attribute.AllowedValues is not { Length: > 0 })
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        unmarked.Should().BeEmpty(
            "a free-text destination must carry Format = \"uri\" so DelegatedConfigurationGuard covers it");
    }
}
