using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tidepool.Mappers;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.Connectors.Tidepool.Tests.Mappers;

/// <summary>
/// A null in a Tidepool data array is one malformed record. Each mapper drops it before mapping, so
/// the records beside it still publish rather than the run failing on every cycle.
/// </summary>
public class TidepoolNullRecordTests
{
    private const string Source = "tidepool-connector";
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("mmol/L", 5.0, 5.0 * GlucoseConstants.MgdlPerMmol)]
    [InlineData("mmol", 5.0, 5.0 * GlucoseConstants.MgdlPerMmol)]
    [InlineData("MMOL/L", 5.0, 5.0 * GlucoseConstants.MgdlPerMmol)]
    [InlineData("mmoll", 5.0, 5.0 * GlucoseConstants.MgdlPerMmol)]
    [InlineData(" mmol/L ", 5.0, 5.0 * GlucoseConstants.MgdlPerMmol)]
    [InlineData("mg/dL", 120.0, 120.0)]
    [InlineData("MGDL", 120.0, 120.0)]
    [InlineData(null, 120.0, 120.0)]
    [InlineData("g/L", 120.0, 120.0)]
    public void MapBgValues_ReadsTheUnitsSpelling(string? units, double value, double expectedMgdl)
    {
        TidepoolBgValue[] values = [new() { Id = "bg-1", Value = value, Units = units!, Time = Now }];

        var readings = new TidepoolSensorGlucoseMapper(NullLogger.Instance, Source).MapBgValues(values, Now);

        readings.Should().ContainSingle().Which.Mgdl.Should().Be(expectedMgdl);
    }

    [Fact]
    public void MapBgValues_NullValue_MapsTheOthers()
    {
        TidepoolBgValue[] values =
        [
            null!,
            new() { Id = "bg-1", Value = 120, Units = "mg/dL", Time = Now },
        ];

        var readings = new TidepoolSensorGlucoseMapper(NullLogger.Instance, Source).MapBgValues(values, Now);

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
            .MapTreatments(boluses, foods, Now);

        mappedBoluses.Should().ContainSingle().Which.Insulin.Should().Be(3.0);
        carbs.Should().ContainSingle().Which.Carbs.Should().Be(40);
    }
}
