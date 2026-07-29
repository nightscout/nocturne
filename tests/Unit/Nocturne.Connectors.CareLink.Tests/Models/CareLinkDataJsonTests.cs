using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Models;

public class CareLinkDataJsonTests
{
    [Theory]
    [InlineData("bgUnits")]
    [InlineData("bgunits")]
    [InlineData("BGUNITS")]
    public void Deserialize_BindsBgUnits_WhateverTheCasing(string key)
    {
        var json = $"{{\"{key}\":\"MMOL/L\"}}";

        var data = JsonSerializer.Deserialize<CareLinkData>(json, JsonDefaults.CaseInsensitive);

        data!.BgUnits.Should().Be("MMOL/L");
    }

    /// <summary>
    /// Every CareLink model must be usable with the connector's case-insensitive options. Two
    /// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> values that differ
    /// only in case are the same property name to System.Text.Json, and it throws while building
    /// the type's metadata — before any response body is read. That takes out every endpoint at
    /// once and surfaces only as a generic "endpoint unavailable" warning.
    /// </summary>
    [Fact]
    public void CareLinkModels_HaveNoCaseInsensitivePropertyNameCollisions()
    {
        var models = typeof(CareLinkData).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && !t.IsGenericTypeDefinition
                        && t.Namespace == typeof(CareLinkData).Namespace)
            .ToList();

        models.Should().NotBeEmpty();

        foreach (var model in models)
        {
            var buildMetadata = () => JsonDefaults.CaseInsensitive.GetTypeInfo(model);
            buildMetadata.Should().NotThrow(
                $"{model.Name} must deserialize under the connector's case-insensitive options");
        }
    }
}
