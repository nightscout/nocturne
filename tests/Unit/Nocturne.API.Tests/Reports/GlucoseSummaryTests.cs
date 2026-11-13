using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Reports;

public class GlucoseSummaryTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 29, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(6);
    private static readonly StatisticsService Statistics = new(TimeProvider.System);

    private static long At(int minutes) => From.AddMinutes(minutes).ToUnixTimeMilliseconds();

    private static DashboardChartData Fixture() => new()
    {
        GlucoseData =
        [
            new GlucosePointDto { Time = At(0), Sgv = 60 },
            new GlucosePointDto { Time = At(5), Sgv = 100 },
            new GlucosePointDto { Time = At(10), Sgv = 140 },
            new GlucosePointDto { Time = At(15), Sgv = 200 },
        ],
        Thresholds = new ChartThresholdsDto { Low = 70, High = 180, VeryLow = 54, VeryHigh = 250, GlucoseYMax = 300 },
        BolusMarkers = [new BolusMarkerDto { Time = At(5), Insulin = 3 }],
        CarbMarkers = [new CarbMarkerDto { Time = At(5), Carbs = 30 }, new CarbMarkerDto { Time = At(10), Carbs = 10 }],
    };

    [Fact]
    public void States_the_figures_the_chart_shows()
    {
        var text = GlucoseSummary.Build(Fixture(), Statistics, useMmol: false, From, To);

        text.Should().Be(
            """
            Glucose from 2026-10-29 06:00 to 2026-10-29 12:00 UTC.
            Readings: 4.
            Min 60, max 200, mean 125 mg/dL.
            Time in range (70-180 mg/dL): 50%. Below: 25%. Above: 25%.
            Lows (episodes below range): 0. Highs (episodes above range): 0.
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Writes_mmol_to_one_decimal()
    {
        var text = GlucoseSummary.Build(Fixture(), Statistics, useMmol: true, From, To);

        text.Should().Contain("Min 3.3, max 11.1, mean 6.9 mmol/L.")
            .And.Contain("Time in range (3.9-10.0 mmol/L): 50%.");
    }

    [Fact]
    public void Names_its_omitted_fields_and_no_marker_counts()
    {
        var text = GlucoseSummary.Build(Fixture(), Statistics, useMmol: false, From, To, omittedFields: ["iob", "cob"]);

        text.Should().EndWith("Fields omitted: iob, cob (scope not granted).").And.NotContain("Boluses");
    }

    [Fact]
    public void Says_so_when_there_are_no_readings()
    {
        var text = GlucoseSummary.Build(new DashboardChartData(), Statistics, useMmol: false, From, To);

        text.Should().Contain("No glucose readings in this period.");
    }
}
