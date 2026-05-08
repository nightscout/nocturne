using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

[Trait("Category", "Unit")]
public class HaloDialSchemaMigratorTests
{
    [Fact]
    public void V1Json_RoundTripsViaMigrator()
    {
        var original = new HaloDialConfig
        {
            ColorMode = HaloDialColorMode.Continuous,
            HistoryMinutes = 60,
            PredictionMinutes = 90,
        };
        var json = JsonSerializer.Serialize(original);
        var raw = JsonDocument.Parse(json).RootElement;

        var migrated = HaloDialSchemaMigrator.Migrate(raw);

        migrated.ColorMode.Should().Be(HaloDialColorMode.Continuous);
        migrated.HistoryMinutes.Should().Be(60);
        migrated.PredictionMinutes.Should().Be(90);
        migrated.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void MissingSchemaVersion_IsTreatedAsV1()
    {
        var raw = JsonDocument.Parse("""{"colorMode":"Discrete","historyMinutes":30}""").RootElement;

        var migrated = HaloDialSchemaMigrator.Migrate(raw);

        migrated.HistoryMinutes.Should().Be(30);
        migrated.ColorMode.Should().Be(HaloDialColorMode.Discrete);
    }

    [Fact]
    public void UnknownFutureFields_AreIgnoredWithoutThrowing()
    {
        var raw = JsonDocument.Parse(
            """{"schemaVersion":1,"historyMinutes":15,"someFutureField":"new","nestedFuture":{"a":1}}""")
            .RootElement;

        var migrated = HaloDialSchemaMigrator.Migrate(raw);

        migrated.HistoryMinutes.Should().Be(15);
    }

    [Fact]
    public void NonObjectInput_ReturnsDefaults()
    {
        var raw = JsonDocument.Parse("[]").RootElement;

        var migrated = HaloDialSchemaMigrator.Migrate(raw);

        migrated.HistoryMinutes.Should().Be(15);
        migrated.PredictionMinutes.Should().Be(45);
    }

    [Fact]
    public void NullJsonValue_ReturnsDefaults()
    {
        var raw = JsonDocument.Parse("null").RootElement;

        var migrated = HaloDialSchemaMigrator.Migrate(raw);

        migrated.SchemaVersion.Should().Be(1);
    }
}
