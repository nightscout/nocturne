using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tidepool.Mappers;
using Nocturne.Connectors.Tidepool.Models;
using Xunit;

namespace Nocturne.Connectors.Tidepool.Tests.Mappers;

/// <summary>
/// A null in a Tidepool data array is one malformed record. Each mapper drops it before mapping, so
/// the records beside it still publish rather than the run failing on every cycle.
/// </summary>
public class TidepoolNullRecordTests
{
    private const string Source = "tidepool-connector";

    [Fact]
    public void MapBgValues_NullValue_MapsTheOthers()
    {
        TidepoolBgValue[] values =
        [
            null!,
            new() { Id = "bg-1", Value = 120, Units = "mg/dL", Time = DateTime.UtcNow },
        ];

        var readings = new TidepoolSensorGlucoseMapper(NullLogger.Instance, Source).MapBgValues(values);

        readings.Should().ContainSingle().Which.Mgdl.Should().Be(120);
    }

    [Fact]
    public void MapTreatments_NullBolusAndFood_MapsTheOthers()
    {
        var at = DateTime.UtcNow;
        TidepoolBolus[] boluses = [null!, new() { Id = "b1", Normal = 3.0, Time = at }];
        TidepoolFood[] foods =
        [
            new()
            {
                Id = "f1",
                Time = at,
                Nutrition = new TidepoolNutrition { Carbohydrate = new TidepoolCarbohydrate { Net = 40 } },
            },
            null!,
        ];

        var (mappedBoluses, carbs, _) = new TidepoolV4TreatmentMapper(NullLogger.Instance, Source)
            .MapTreatments(boluses, foods);

        mappedBoluses.Should().ContainSingle().Which.Insulin.Should().Be(3.0);
        carbs.Should().ContainSingle().Which.Carbs.Should().Be(40);
    }
}
