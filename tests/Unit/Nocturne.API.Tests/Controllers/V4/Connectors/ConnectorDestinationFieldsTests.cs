using System.Reflection;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

/// <summary>
/// <see cref="DelegatedConfigurationGuard"/> knows a destination only by its <c>uri</c> format, so a
/// connector field that names a host the connector signs in to without that format would let a
/// guarded caller send the stored secret anywhere. Every connector assembly the API ships is read here: each
/// free-text field that looks like an address is a guarded destination, and the guarded ones are
/// exactly the fields named below. A region or server choice picks among the vendor's own hosts.
/// </summary>
public class ConnectorDestinationFieldsTests
{
    private static readonly string[] GuardedDestinations =
    [
        "MyLifeConnectorConfiguration.ServiceUrl",
        "NightscoutConnectorConfiguration.RealtimeUrl",
        "NightscoutConnectorConfiguration.Url",
        "NocturneRemoteConnectorConfiguration.Url",
    ];

    private static readonly string[] AddressWords = ["Url", "Uri", "Host", "Endpoint", "Address", "Domain", "Server", "Region"];

    private static IEnumerable<PropertyInfo> ConnectorProperties() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Nocturne.Connectors.*.dll")
            .Where(path => !Path.GetFileName(path).Contains(".Tests", StringComparison.Ordinal))
            .Select(path => Assembly.Load(AssemblyName.GetAssemblyName(path)))
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(BaseConnectorConfiguration).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(p => p.GetCustomAttribute<ConnectorPropertyAttribute>() is not null);

    [Fact]
    public void TheGuardedDestinations_AreExactlyTheConnectorsFreeTextAddresses()
    {
        ConnectorProperties()
            .Where(DelegatedConfigurationGuard.ChoosesDestination)
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .Should().BeEquivalentTo(GuardedDestinations);
    }

    [Fact]
    public void EveryAddressLikeField_IsAGuardedDestinationOrAFixedChoice()
    {
        var loose = ConnectorProperties()
            .Where(p => AddressWords.Any(word => p.Name.Contains(word, StringComparison.Ordinal)))
            .Where(p => !DelegatedConfigurationGuard.ChoosesDestination(p)
                        && p.GetCustomAttribute<ConnectorPropertyAttribute>()!.AllowedValues is not { Length: > 0 })
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        loose.Should().BeEmpty("a free-text address a connector signs in to must carry the uri format the guard reads");
    }
}
