using System.Reflection;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// A region a connector's schema offers has to resolve to its own entry in the connector's server
/// mapping. A value that missed would fall through to another mapping entry and send a tenant's
/// credentials to the wrong environment with no error.
/// </summary>
public class ConnectorRegionResolutionTests
{
    public static TheoryData<string, string> Regions()
    {
        var data = new TheoryData<string, string>();
        foreach (var installer in MappedInstallers())
            foreach (var region in AllowedRegions(installer))
                data.Add(ConnectorInstallers.NameOf(installer), region);
        return data;
    }

    [Fact]
    public void TheMappedConnectors_AreFound()
    {
        MappedInstallers().Select(ConnectorInstallers.NameOf)
            .Should().Contain(["CareLink", "Dexcom", "Eversense", "Tidepool"]);
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public void EveryAllowedRegion_ResolvesToItsOwnMappingEntry(string connectorName, string region)
    {
        var installer = MappedInstallers().Single(i => ConnectorInstallers.NameOf(i) == connectorName);
        var options = OptionsOf(installer);
        var configType = installer.GetType().BaseType!.GetGenericArguments()[0];
        var config = (BaseConnectorConfiguration)Activator.CreateInstance(configType)!;
        RegionProperty(configType).SetValue(config, region);

        var resolver = Activator.CreateInstance(
            typeof(ConnectorServerResolver<>).MakeGenericType(configType),
            options.ServerMapping, options.GetServerRegion, options.DefaultServer)!;
        var resolved = (Uri?)resolver.GetType().GetMethod("Resolve")!.Invoke(resolver, [config]);

        var expected = options.ServerMapping!.Single(e => e.Key.Equals(region, StringComparison.OrdinalIgnoreCase)).Value;
        resolved.Should().NotBeNull();
        resolved!.AbsoluteUri.TrimEnd('/').Should().Be(ConnectorUrl.ResolveBase(expected, connectorName));
    }

    private static List<Nocturne.Connectors.Core.Interfaces.IConnectorInstaller> MappedInstallers() =>
        [.. ConnectorInstallers.Discover()
            .Where(i => ConnectorInstallers.ClosedBaseOf(i.GetType(), typeof(ConnectorInstaller<,,>)) is not null)
            .Where(i => OptionsOf(i).ServerMapping is not null)];

    private static ConnectorOptions OptionsOf(Nocturne.Connectors.Core.Interfaces.IConnectorInstaller installer)
    {
        var baseType = ConnectorInstallers.ClosedBaseOf(installer.GetType(), typeof(ConnectorInstaller<,,>))!;
        return (ConnectorOptions)baseType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(ConnectorOptions))
            .GetValue(installer)!;
    }

    private static PropertyInfo RegionProperty(Type configType) =>
        configType.GetProperties().Single(p =>
            p.GetCustomAttribute<ConnectorPropertyAttribute>() is { Key: ConnectorPropertyKey.Server or ConnectorPropertyKey.Region });

    private static string[] AllowedRegions(Nocturne.Connectors.Core.Interfaces.IConnectorInstaller installer) =>
        RegionProperty(installer.GetType().BaseType!.GetGenericArguments()[0])
            .GetCustomAttribute<ConnectorPropertyAttribute>()!.AllowedValues!;
}
