using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tidepool.Mappers;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.Connectors.Tidepool.Tests.Mappers;

/// <summary>
/// The stored legacy id of every Tidepool record type, as exact strings.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class TidepoolLegacyIdTests
{
    private static readonly DateTime At = new(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SensorGlucose()
    {
        TidepoolBgValue[] values = [new() { Id = "bg-1", Time = At, Units = "mg/dL", Value = 120 }];

        new TidepoolSensorGlucoseMapper(NullLogger.Instance, DataSources.TidepoolConnector)
            .MapBgValues(values)
            .Single().LegacyId.Should().Be("tidepool_bg-1");
    }

    [Fact]
    public void BolusAndCarbs()
    {
        TidepoolBolus[] boluses = [new() { Id = "b-1", Normal = 2, Time = At }];
        TidepoolFood[] foods =
        [
            new() { Id = "f-1", Time = At, Nutrition = new() { Carbohydrate = new() { Net = 30 } } },
            new() { Id = "f-2", Time = At.AddHours(1), Nutrition = new() { Carbohydrate = new() { Net = 15 } } },
        ];

        var (mappedBoluses, mappedCarbs, _) = new TidepoolV4TreatmentMapper(NullLogger.Instance, DataSources.TidepoolConnector)
            .MapTreatments(boluses, foods);

        mappedBoluses.Single().LegacyId.Should().Be("tidepool_b-1");
        mappedCarbs.Select(c => c.LegacyId).Should().Equal("tidepool_f-1", "tidepool_f-2");
    }
}
