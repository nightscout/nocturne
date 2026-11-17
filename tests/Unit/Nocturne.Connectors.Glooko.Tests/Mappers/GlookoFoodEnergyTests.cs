using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Glooko reports food energy in kilocalories; Nocturne stores kilojoules.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoFoodEnergyTests
{
    private readonly GlookoV4TreatmentMapper _mapper = new(
        "glooko-connector",
        new GlookoTimeMapper(new GlookoConnectorConfiguration(), NullLogger.Instance),
        NullLogger.Instance,
        TimeProvider.System);

    [Fact]
    public void A_v2_food_is_stored_in_kilojoules()
    {
        var batch = JsonSerializer.Deserialize<GlookoBatchData>(
            """
            {"foods":[{"timestamp":"2026-06-20T12:00:00.000Z","guid":"f-1","name":"Toast",
              "carbs":30.0,"calories":100.0}]}
            """)!;

        var entry = _mapper.MapFoodsToConnectorEntries(batch).Should().ContainSingle().Subject;

        entry.Energy.Should().Be(418.4m);
        entry.Food!.Energy.Should().Be(418.4m);
    }

    [Fact]
    public void A_v3_history_food_is_stored_in_kilojoules()
    {
        var histories = JsonSerializer.Deserialize<GlookoV3HistoriesResponse>(
            """
            {"histories":[{"type":"meals","guid":"meal-1","item":{
              "guid":"meal-1","timestamp":"2026-01-10T08:00:00Z","type":"breakfast","carbs":30.0,
              "foods":[{"guid":"food-1","name":"Toast","carbs":30.0,"calories":250.0}]}}]}
            """)!;

        var entry = _mapper.MapV3HistoryMealsToConnectorEntries(histories).Should().ContainSingle().Subject;

        entry.Energy.Should().Be(1046m);
        entry.Food!.Energy.Should().Be(1046m);
    }
}
