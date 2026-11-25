using FluentAssertions;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// A configuration type is read by reflection, so one whose constructor or getter throws costs the
/// form its defaults or that one field, never the whole schema.
/// </summary>
public class ConnectorConfigurationReflectionTests
{
    private sealed class WorkingConfiguration : BaseConnectorConfiguration;

    private sealed class ThrowingConstructorConfiguration : BaseConnectorConfiguration
    {
        public ThrowingConstructorConfiguration() => throw new InvalidOperationException("constructor failed");
    }

    private sealed class ConstructorWithArgumentConfiguration(string required) : BaseConnectorConfiguration
    {
        public string Required { get; } = required;
    }

    private sealed class ThrowingGetterConfiguration : BaseConnectorConfiguration
    {
        [ConnectorProperty(ConnectorPropertyKey.Username)]
        public string? Username => throw new InvalidOperationException("getter failed");

        [ConnectorProperty(ConnectorPropertyKey.Server)]
        public string? Server { get; set; } = "example.test";
    }

    private static bool HasDefault(System.Text.Json.JsonDocument schema, string property) =>
        schema.RootElement.GetProperty("properties").GetProperty(property).TryGetProperty("default", out _);

    [Fact]
    public void A_configuration_type_the_schema_can_instantiate_carries_its_defaults()
    {
        HasDefault(ConnectorConfigurationService.GenerateSchemaFromType(typeof(WorkingConfiguration)), "enabled")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(ThrowingConstructorConfiguration))]
    [InlineData(typeof(ConstructorWithArgumentConfiguration))]
    public void A_configuration_type_that_cannot_be_instantiated_still_yields_a_schema_without_defaults(Type configType)
    {
        var schema = ConnectorConfigurationService.GenerateSchemaFromType(configType);

        HasDefault(schema, "enabled").Should().BeFalse();
    }

    [Fact]
    public void A_property_whose_getter_throws_is_left_out_of_the_schema()
    {
        var properties = ConnectorConfigurationService.GenerateSchemaFromType(typeof(ThrowingGetterConfiguration))
            .RootElement.GetProperty("properties");

        properties.TryGetProperty("username", out _).Should().BeFalse();
        properties.TryGetProperty("server", out _).Should().BeTrue();
    }

    [Fact]
    public void A_property_whose_getter_throws_is_left_out_of_the_effective_configuration()
    {
        var effective = ConnectorConfigurationService.GetEffectiveConfiguration(new ThrowingGetterConfiguration());

        effective.Should().NotContainKey("username");
        effective.Should().Contain("server", "example.test");
    }
}
