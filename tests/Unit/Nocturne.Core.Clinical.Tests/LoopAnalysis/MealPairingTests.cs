using FluentAssertions;
using Nocturne.Core.Clinical.LoopAnalysis;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class MealPairingTests
{
    private const long Minute = 60_000;
    private static readonly long Meal = new DateTimeOffset(2024, 6, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static InsulinDose Manual(long at, Guid? correlationId = null) => new(at, 4, false, correlationId);

    [Theory]
    [InlineData(-60, true)]
    [InlineData(-45, true)]
    [InlineData(30, true)]
    [InlineData(-61, false)]
    [InlineData(31, false)]
    [InlineData(40, false)]
    public void ADose_PairsFromAnHourBeforeTheMealToHalfAnHourAfter(long minutes, bool pairs)
    {
        var pairing = new MealPairing([new CarbEvent(Meal, 40)]);

        pairing.MealOf(Manual(Meal + minutes * Minute)).HasValue.Should().Be(pairs);
    }

    [Fact]
    public void ADoseBetweenTwoMeals_PairsWithTheNearerOnly()
    {
        var pairing = new MealPairing([new CarbEvent(Meal, 30), new CarbEvent(Meal + 60 * Minute, 50)]);

        pairing.MealOf(Manual(Meal + 35 * Minute)).Should().Be(1);
        pairing.MealOf(Manual(Meal + 30 * Minute)).Should().Be(0, "a tie goes to the earlier meal");
    }

    [Fact]
    public void ACorrelationId_PairsWhateverTheDistance()
    {
        var link = Guid.NewGuid();
        var pairing = new MealPairing([new CarbEvent(Meal, 30), new CarbEvent(Meal + 3 * 60 * Minute, 50, link)]);

        pairing.MealOf(Manual(Meal, link)).Should().Be(1);
        pairing.MealOf(Manual(Meal, Guid.NewGuid())).Should().Be(0, "an unmatched link falls back to the window");
    }

    [Fact]
    public void EntriesAtOneInstant_AreOneMeal()
    {
        var link = Guid.NewGuid();
        var pairing = new MealPairing([new CarbEvent(Meal, 30), new CarbEvent(Meal, 20, link)]);

        pairing.Meals.Should().ContainSingle().Which.Grams.Should().Be(50);
        pairing.MealOf(Manual(Meal - 5 * 60 * Minute, link)).Should().Be(0);
    }

    [Fact]
    public void CorrectionBoluses_AreTheManualDosesPairedWithNoMeal()
    {
        var link = Guid.NewGuid();
        CarbEvent[] carbs = [new(Meal, 40), new(Meal + 5 * 60 * Minute, 30, link)];
        InsulinDose[] doses =
        [
            Manual(Meal - 45 * Minute),
            Manual(Meal + 40 * Minute),
            Manual(Meal + 2 * 60 * Minute, link),
            new(Meal + 3 * 60 * Minute, 0.5, true),
        ];

        EventAnchors.CorrectionBoluses(doses, carbs).Select(a => a.Mills).Should().Equal(Meal + 40 * Minute);
    }
}
