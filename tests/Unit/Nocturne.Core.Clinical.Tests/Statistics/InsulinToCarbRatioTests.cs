using FluentAssertions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class InsulinToCarbRatioTests
{
    private static readonly DateTime DayStart = new(2024, 11, 18, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DayEnd = DayStart.AddDays(1);

    private readonly StatisticsService _statisticsService = new(TimeProvider.System);

    private static CarbIntake Carbs(double hours, double grams, Guid? correlationId = null) =>
        new() { Id = Guid.NewGuid(), Carbs = grams, Timestamp = DayStart.AddHours(hours), CorrelationId = correlationId };

    private static Bolus Bolus(double hours, double units, Guid? correlationId = null, bool automatic = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Insulin = units,
            Timestamp = DayStart.AddHours(hours),
            CorrelationId = correlationId,
            Automatic = automatic,
        };

    private InsulinDeliveryStatistics Delivery(
        IEnumerable<Bolus> boluses, IEnumerable<CarbIntake> carbs, IEnumerable<Bolus>? algorithmBoluses = null) =>
        _statisticsService.CalculateInsulinDeliveryStatistics(
            boluses, algorithmBoluses ?? [], Array.Empty<TempBasal>(), carbs, DayStart, DayEnd, new DateTimeOffset(DayEnd).ToUnixTimeMilliseconds());

    private static (List<Bolus> Boluses, List<CarbIntake> Carbs) ExampleDay() =>
    (
        [Bolus(8 - 20 / 60.0, 5), Bolus(12.5, 6), Bolus(19, 7)],
        [Carbs(8, 50), Carbs(12.5, 60), Carbs(15.5, 20), Carbs(19, 70), Carbs(22, 15)]
    );

    [Fact]
    public void ExampleDay_RatioIsMealGramsOverTheirBolusesWithSnackAndHypoUnpaired()
    {
        var (boluses, carbs) = ExampleDay();

        var result = Delivery(boluses, carbs);

        result.IcRatio.Should().Be(10.0, "180 g of meals over their 18 U");
        result.UnpairedCarbs.Should().Be(35, "the 20 g snack and the 15 g hypo treatment had no bolus");
        result.MealBoluses.Should().Be(3);
        result.CorrectionBoluses.Should().Be(0);
    }

    [Fact]
    public void ExampleDay_TreatmentSummaryRatioUsesTheSamePairing()
    {
        var (boluses, carbs) = ExampleDay();

        _statisticsService.CalculateTreatmentSummary(boluses, carbs).CarbToInsulinRatio.Should().Be(10.0);
    }

    [Fact]
    public void PreBolusFortyFiveMinutesAhead_PairsWithTheMeal()
    {
        var result = Delivery([Bolus(12 - 0.75, 4)], [Carbs(12, 48)]);

        result.IcRatio.Should().Be(12.0);
        result.UnpairedCarbs.Should().Be(0);
        result.MealBoluses.Should().Be(1);
    }

    [Fact]
    public void BolusFortyMinutesAfterTheMeal_IsACorrection()
    {
        var result = Delivery([Bolus(12 + 40 / 60.0, 4)], [Carbs(12, 48)]);

        result.IcRatio.Should().BeNull();
        result.UnpairedCarbs.Should().Be(48);
        result.MealBoluses.Should().Be(0);
        result.CorrectionBoluses.Should().Be(1);
    }

    [Fact]
    public void BolusBetweenTwoMeals_PairsOnlyWithTheNearer()
    {
        var result = Delivery([Bolus(12 + 25 / 60.0, 5)], [Carbs(12, 30), Carbs(13, 50)]);

        result.IcRatio.Should().Be(6.0, "the bolus is 25 minutes after the 30 g meal and 35 before the 50 g one");
        result.UnpairedCarbs.Should().Be(50);
    }

    [Fact]
    public void BolusEquidistantFromTwoMeals_PairsWithTheEarlier()
    {
        var result = Delivery([Bolus(12.5, 5)], [Carbs(12.25, 30), Carbs(12.75, 50)]);

        result.IcRatio.Should().Be(6.0);
        result.UnpairedCarbs.Should().Be(50);
    }

    [Fact]
    public void CorrelationId_WinsOverDistance()
    {
        var link = Guid.NewGuid();

        var result = Delivery([Bolus(12, 5, link)], [Carbs(12, 30), Carbs(14, 50, link)]);

        result.IcRatio.Should().Be(10.0, "the bolus is linked to the 50 g meal two hours away");
        result.UnpairedCarbs.Should().Be(30);
    }

    [Fact]
    public void ExampleDay_AnAlgorithmBolusBeforeTheHypoTreatmentLeavesItUnpaired()
    {
        var (boluses, carbs) = ExampleDay();

        var result = Delivery(boluses, carbs, [Bolus(21 + 10 / 60.0, 0.3, automatic: true)]);

        result.IcRatio.Should().Be(10.0);
        result.UnpairedCarbs.Should().Be(35);
    }

    [Fact]
    public void AlgorithmBolusInTheWindowOfAManuallyBolusedMeal_AddsToItsUnits()
    {
        var result = Delivery([Bolus(12, 2)], [Carbs(12, 30)], [Bolus(12.25, 1, automatic: true)]);

        result.IcRatio.Should().Be(10.0, "30 g over 2 U manual plus 1 U automatic");
        result.UnpairedCarbs.Should().Be(0);
    }

    [Fact]
    public void ClosedLoopDayWithOnlyAlgorithmBoluses_HasNoRatioAndEveryCarbUnpaired()
    {
        List<Bolus> smbs = [Bolus(7.75, 0.5, automatic: true), Bolus(12.25, 0.8, automatic: true), Bolus(18.5, 1, automatic: true)];
        List<CarbIntake> carbs = [Carbs(8, 40), Carbs(12.5, 50), Carbs(19, 60)];

        var result = Delivery([], carbs, smbs);

        result.IcRatio.Should().BeNull();
        result.UnpairedCarbs.Should().Be(150);
        _statisticsService.CalculateTreatmentSummary(smbs, carbs).CarbToInsulinRatio.Should().BeNull();
    }

    [Fact]
    public void NoPairedInsulin_RatioIsNull()
    {
        var result = Delivery([Bolus(6, 2)], [Carbs(12, 15)]);

        result.IcRatio.Should().BeNull();
        result.UnpairedCarbs.Should().Be(15);
        _statisticsService.CalculateTreatmentSummary([Bolus(6, 2)], [Carbs(12, 15)]).CarbToInsulinRatio.Should().BeNull();
    }
}
