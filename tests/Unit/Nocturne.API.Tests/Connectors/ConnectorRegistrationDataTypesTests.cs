using System.Reflection;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// The setup page lists a connector's supported data types in a keyed loop, so a type declared
/// twice on a registration crashes the page for that connector (Svelte's each_key_duplicate).
/// </summary>
public class ConnectorRegistrationDataTypesTests
{
    public static TheoryData<string> Registrations() =>
        [.. ConnectorInstallers.Discover()
            .Select(i => i.GetType().Assembly)
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false) is not null)
            .Select(t => t.AssemblyQualifiedName!)];

    [Theory]
    [MemberData(nameof(Registrations))]
    public void SupportedDataTypes_AreDeclaredOnce(string configTypeName)
    {
        var registration = Type.GetType(configTypeName)!.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false)!;

        registration.SupportedDataTypes.Should().OnlyHaveUniqueItems(
            $"{registration.ConnectorName} declares a data type twice");
    }
}
