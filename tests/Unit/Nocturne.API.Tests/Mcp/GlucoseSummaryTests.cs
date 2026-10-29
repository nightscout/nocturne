using FluentAssertions;
using Nocturne.API.Mcp;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

public class GlucoseSummaryTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 29, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(6);

    private static DashboardChartData Fixture() => new()
    {
        GlucoseData =
        [
            new GlucosePointDto { Time = 1, Sgv = 60 },
            new GlucosePointDto { Time = 2, Sgv = 100 },
            new GlucosePointDto { Time = 3, Sgv = 140 },
            new GlucosePointDto { Time = 4, Sgv = 200 },
        ],
        Thresholds = new ChartThresholdsDto { Low = 70, High = 180, VeryLow = 54, VeryHigh = 250, GlucoseYMax = 300 },
        BolusMarkers = [new BolusMarkerDto { Time = 2, Insulin = 3 }],
        CarbMarkers = [new CarbMarkerDto { Time = 2, Carbs = 30 }, new CarbMarkerDto { Time = 3, Carbs = 10 }],
    };

    [Fact]
    public void States_the_figures_the_chart_shows()
    {
        var text = GlucoseSummary.Build(Fixture(), useMmol: false, From, To, omittedTracks: []);

        text.Should().Be(
            """
            Glucose from 2026-10-29 06:00 to 2026-10-29 12:00 UTC.
            Readings: 4.
            Min 60, max 200, mean 125 mg/dL.
            Time in range (70-180 mg/dL): 50%.
            Readings below range: 1. Readings above range: 1.
            Boluses: 1. Carb entries: 2.
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Writes_mmol_to_one_decimal()
    {
        var text = GlucoseSummary.Build(Fixture(), useMmol: true, From, To, omittedTracks: []);

        text.Should().Contain("Min 3.3, max 11.1, mean 6.9 mmol/L.")
            .And.Contain("Time in range (3.9-10.0 mmol/L): 50%.");
    }

    [Fact]
    public void Names_the_tracks_the_token_cannot_read_and_drops_their_counts()
    {
        var granted = new HashSet<string> { Scope.GlucoseRead, Scope.DevicesRead };
        var omitted = GlucoseSummary.OmittedTracks(s => Scope.Satisfies(granted, s));

        var text = GlucoseSummary.Build(Fixture(), useMmol: false, From, To, omitted);

        omitted.Should().Equal("insulin, carbs and basal", "profiles", "heart rate", "steps", "sleep");
        text.Should().EndWith(
            "Tracks omitted: insulin, carbs and basal, profiles, heart rate, steps, sleep (scope not granted).");
        text.Should().NotContain("Boluses");
    }

    [Fact]
    public void A_readwrite_grant_omits_nothing()
    {
        var granted = new HashSet<string>
        {
            Scope.GlucoseRead, Scope.TreatmentsReadWrite, Scope.DevicesRead, Scope.TherapyRead,
            Scope.HeartRateRead, Scope.StepCountRead, Scope.SleepRead,
        };

        GlucoseSummary.OmittedTracks(s => Scope.Satisfies(granted, s)).Should().BeEmpty();
    }

    [Fact]
    public void Says_so_when_there_are_no_readings()
    {
        var text = GlucoseSummary.Build(new DashboardChartData(), useMmol: false, From, To, omittedTracks: []);

        text.Should().Contain("No glucose readings in this period.");
    }
}
