using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Twiist.Mappers;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Connectors.Twiist.Utilities;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Mappers;

/// <summary>
/// The stored legacy id of every Twiist record type, as exact strings.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class TwiistLegacyIdTests
{
    private static readonly DateTime At = new(2026, 6, 16, 22, 42, 0, DateTimeKind.Utc);

    [Fact]
    public void Glucose()
    {
        new TwiistGlucoseMapper(NullLogger.Instance).MapGlucoseRecords([new GlucoseRecord(At, 120)])
            .Single().LegacyId.Should().Be("twiist_glucose_1781649720000");
    }

    [Fact]
    public void Bolus()
    {
        var mapper = new TwiistInsulinMapper(NullLogger.Instance);
        List<TwiistInsulinDose> doses =
        [
            new() { Identifier = "dose-1", DoseType = "Bolus", StartDate = At, Value = 1.5m },
            new() { DoseType = "Bolus", StartDate = At.AddMinutes(5), Value = 2m },
        ];

        mapper.MapBoluses(doses).Select(b => b.LegacyId).Should().Equal("twiist_bolus_dose-1", "twiist_bolus_639172468200000000");
    }

    [Fact]
    public void Meal()
    {
        new TwiistMealMapper(NullLogger.Instance)
            .MapMeals([new TwiistMeal { StartDate = At, Grams = 30m }])
            .Single().LegacyId.Should().Be("twiist_meal_1781649720000");
    }

    [Fact]
    public void TempBasal()
    {
        var blob = TwiistTempBasalMapperTests.BuildBlob((At, At.AddMinutes(5), 0.5));

        new TwiistTempBasalMapper(NullLogger.Instance).MapTempBasals(blob, 0.4)
            .Single().LegacyId.Should().Be("twiist_tempbasal_639172465200000000");
    }

    [Fact]
    public void Suspension()
    {
        List<TwiistInsulinDose> doses =
        [
            new() { DoseType = "Suspend", StartDate = At },
            new() { DoseType = "Resume", StartDate = At.AddMinutes(20) },
        ];

        new TwiistTempBasalMapper(NullLogger.Instance).MapSuspensions(doses)
            .Single().LegacyId.Should().Be("twiist_suspend_639172465200000000");
    }
}
