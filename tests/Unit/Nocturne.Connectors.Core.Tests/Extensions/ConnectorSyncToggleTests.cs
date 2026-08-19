using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Tests.Unit;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
/// The sync toggles are resolved from the names of <see cref="ConnectorPropertyKey"/> and
/// <see cref="SyncDataType"/> members rather than listed anywhere, so these tests hold the naming
/// convention that resolution depends on: a toggle that stops resolving would silently sync data a
/// user switched off, or offer a toggle for a data type the connector cannot produce.
/// </summary>
public class ConnectorSyncToggleTests
{
    [Fact]
    public void EverySyncToggleOnTheConfigurationResolvesToADataType()
    {
        var toggleKeys = SyncToggleProperties()
            .Select(property => property.GetCustomAttribute<ConnectorPropertyAttribute>()!.Key)
            .ToList();

        toggleKeys.Should().NotBeEmpty("the configuration declares sync toggle properties");
        toggleKeys.Should().OnlyContain(key => ConnectorSyncToggles.ByPropertyKey.ContainsKey(key),
            "an unresolved toggle is never matched against the connector's supported data types");
    }

    [Fact]
    public void EveryResolvedToggleIsABoolPropertyOnTheConfiguration()
    {
        var declared = SyncToggleProperties()
            .Select(property => property.GetCustomAttribute<ConnectorPropertyAttribute>()!.Key);

        ConnectorSyncToggles.ByPropertyKey.Keys.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void SyncIntervalIsNotAToggle()
    {
        ConnectorSyncToggles.ByPropertyKey.Should()
            .NotContainKey(ConnectorPropertyKey.SyncIntervalMinutes);
    }

    [Fact]
    public void EachToggleGatesOnlyItsOwnDataType()
    {
        foreach (var (key, dataType) in ConnectorSyncToggles.ByPropertyKey)
        {
            var configuration = new TestConnectorConfiguration();
            PropertyFor(key).SetValue(configuration, false);

            configuration.IsDataTypeEnabled(dataType).Should().BeFalse(
                "{0} is switched off", key);

            var others = ConnectorSyncToggles.ByPropertyKey.Values.Where(other => other != dataType);
            others.Should().OnlyContain(other => configuration.IsDataTypeEnabled(other),
                $"only {key} was switched off");
        }
    }

    [Fact]
    public void DataTypesWithNoToggleAreAlwaysEnabled()
    {
        var untoggled = Enum.GetValues<SyncDataType>()
            .Except(ConnectorSyncToggles.ByPropertyKey.Values)
            .ToList();

        untoggled.Should().Contain([SyncDataType.Calibrations, SyncDataType.BGChecks]);
        untoggled.Should().NotContain(SyncDataType.BasalInjections,
            "a connector declares basal injections supported, so the user must be able to switch them off");

        var allTogglesOff = new TestConnectorConfiguration();
        foreach (var key in ConnectorSyncToggles.ByPropertyKey.Keys)
            PropertyFor(key).SetValue(allTogglesOff, false);

        untoggled.Should().OnlyContain(dataType => allTogglesOff.IsDataTypeEnabled(dataType));
    }

    private static PropertyInfo PropertyFor(ConnectorPropertyKey key) =>
        SyncToggleProperties()
            .Single(property => property.GetCustomAttribute<ConnectorPropertyAttribute>()!.Key == key);

    private static IEnumerable<PropertyInfo> SyncToggleProperties() =>
        typeof(TestConnectorConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool)
                               && property.GetCustomAttribute<ConnectorPropertyAttribute>() is { } attribute
                               && attribute.GetKeyName().StartsWith("Sync", StringComparison.Ordinal));
}
