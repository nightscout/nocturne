using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

public class DemoPhysiologyTests
{
    private static readonly OrefProfile Profile = new()
    {
        Dia = 3,
        CurrentBasal = 1,
        Sens = 40,
        CarbRatio = 10,
        Curve = "rapid-acting",
        Peak = 75,
    };

    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void Simulator_OneUnitLowersGlucoseBySensInTotal()
    {
        var simulator = new OrefPhysiologySimulator(NullLogger<OrefPhysiologySimulator>.Instance, Profile);
        simulator.AddInsulinDose(Start, 5);

        var drop = Enumerable.Range(0, 12 * 6)
            .Sum(i => simulator.CalculateInsulinActivity(Start.AddMinutes(5 * i)));

        drop.Should().BeApproximately(5 * 40, 5);
    }

    [Fact]
    public void Simulator_OneGramRaisesGlucoseBySensOverCarbRatioInTotal()
    {
        var simulator = new OrefPhysiologySimulator(NullLogger<OrefPhysiologySimulator>.Instance, Profile);
        simulator.AddCarbs(Start, 50, 2);

        var rise = Enumerable.Range(0, 12 * 6)
            .Sum(i => simulator.CalculateCarbAbsorptionRate(Start.AddMinutes(5 * i)));

        rise.Should().BeApproximately(50 * 4.0, 10);
    }

    /// <summary>
    /// Every report is judged against this stream. It has to look like a real
    /// T1D on AID: mostly in range, with real highs and some lows. Bounds are
    /// loose because the generator is unseeded.
    /// </summary>
    [Fact]
    public void Timeline_GlucoseMixLooksLikeRealType1()
    {
        var generator = new DemoDataGenerator(
            Options.Create(new DemoModeConfiguration { BackfillDays = 28 }),
            NullLogger<DemoDataGenerator>.Instance,
            NullLoggerFactory.Instance);

        var glucose = generator.GenerateHistoricalTimeline().Select(s => s.Entry.Sgv ?? 0).ToList();
        double Percent(Func<double, bool> f) => 100.0 * glucose.Count(f) / glucose.Count;

        Percent(g => g >= 70 && g <= 180).Should().BeInRange(55, 88);
        Percent(g => g > 180).Should().BeGreaterThan(8);
        Percent(g => g > 250).Should().BeGreaterThan(0);
        Percent(g => g < 70).Should().BeInRange(1, 10);
    }
}
