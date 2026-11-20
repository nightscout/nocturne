using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// The configuration form is driven by the schema, so a property conditioned on another has to
/// arrive there as <c>x-visibleWhen</c>, on secret and non-secret properties alike, naming the
/// master property the way the form names it.
/// </summary>
public class ConnectorConfigurationSchemaVisibilityTests
{
    private class TwoModeConfiguration : BaseConnectorConfiguration
    {
        [ConnectorProperty(ConnectorPropertyKey.Server, DefaultValue = "EU", AllowedValues = ["EU", "XT"])]
        public string Server { get; init; } = "EU";

        [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true,
            VisibleWhen = ConnectorPropertyKey.Server, VisibleWhenValues = ["EU"])]
        public string? Password { get; init; }

        [ConnectorProperty(ConnectorPropertyKey.Region, DefaultValue = "Auto",
            VisibleWhen = ConnectorPropertyKey.Server, VisibleWhenValues = ["XT"])]
        public string Region { get; init; } = "Auto";
    }

    [Fact]
    public void ConditionedProperties_CarryTheirCondition_UnconditionedDoNot()
    {
        using var schema = ConnectorConfigurationService.GenerateSchemaFromType(typeof(TwoModeConfiguration));
        var properties = schema.RootElement.GetProperty("properties");

        var password = properties.GetProperty("password").GetProperty("x-visibleWhen");
        password.GetProperty("property").GetString().Should().Be("server");
        password.GetProperty("values").EnumerateArray().Select(v => v.GetString()).Should().Equal("EU");

        var unit = properties.GetProperty("region").GetProperty("x-visibleWhen");
        unit.GetProperty("property").GetString().Should().Be("server");
        unit.GetProperty("values").EnumerateArray().Select(v => v.GetString()).Should().Equal("XT");

        properties.GetProperty("server").TryGetProperty("x-visibleWhen", out _).Should().BeFalse();
    }
}
