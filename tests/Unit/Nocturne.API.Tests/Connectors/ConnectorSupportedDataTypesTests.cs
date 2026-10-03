using System.Reflection;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// A connector's <see cref="ConnectorRegistrationAttribute.SupportedDataTypes"/> array is passed
/// straight through by <c>DataSourceService.GetConnectorCapabilities</c> to the API response, and
/// the frontend's connector settings page renders it with a keyed Svelte <c>{#each}</c> keyed on the
/// data type itself. A duplicate entry in the array is therefore a duplicate key, which throws
/// <c>each_key_duplicate</c> and takes down that connector's settings page entirely (#1797, where
/// Glooko listed <c>SyncDataType.TempBasals</c> twice).
/// </summary>
public class ConnectorSupportedDataTypesTests
{
    public static TheoryData<string> RegisteredConnectors() =>
        [.. Registrations().Select(r => r.ConnectorName)];

    [Theory]
    [MemberData(nameof(RegisteredConnectors))]
    public void SupportedDataTypes_HasNoDuplicates(string connectorName)
    {
        var registration = Registrations().Single(r => r.ConnectorName == connectorName);

        registration.SupportedDataTypes.Should().OnlyHaveUniqueItems(
            "a duplicate data type reaches the frontend's keyed {{#each}} and crashes that connector's settings page");
    }

    private static List<ConnectorRegistrationAttribute> Registrations() =>
        [.. ConnectorInstallers.Types()
            .Select(t => t.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false))
            .OfType<ConnectorRegistrationAttribute>()
            .DistinctBy(r => r.ConnectorName)];
}
