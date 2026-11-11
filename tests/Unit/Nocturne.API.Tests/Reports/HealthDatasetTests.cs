using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>Heart rate, steps and sleep as report datasets.</summary>
public class HealthDatasetTests
{
    private static readonly DateTimeOffset To = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

    private static ReportDatasetRequest Request(ReportDatasetParameters parameters, IServiceProvider services, ReportReadLog log, TimeSpan span) =>
        new(parameters, ReportPanelKind.Chart, To - span, To, false, new HashSet<string>(), services, log, Zone: TimeZoneInfo.Utc);

    [Fact]
    public async Task Heart_rate_rows_hold_the_mean_lowest_and_highest_minute_average_of_each_slot()
    {
        var heart = new Mock<IHeartRateService>();
        heart.Setup(h => h.GetHeartRateMinuteAveragesByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                // Minutes 9954 and 9955 of the week: one seven-minute slot.
                new HeartRate { Timestamp = To.AddHours(-2).AddMinutes(-6).UtcDateTime, Bpm = 60 },
                new HeartRate { Timestamp = To.AddHours(-2).AddMinutes(-5).UtcDateTime, Bpm = 80 },
                new HeartRate { Timestamp = To.AddHours(-1).UtcDateTime, Bpm = 100 },
            ]);
        var log = new ReportReadLog();

        var output = await new HeartRateDataset().RunAsync(
            Request(new HeartRateParameters(), new ServiceCollection().AddSingleton(heart.Object).BuildServiceProvider(), log, TimeSpan.FromHours(3)),
            CancellationToken.None);

        output.Rows.Should().HaveCount(3, "three hours of minutes fit the budget, so each minute is its own slot");
        output.Sentence.Should().Be("Heart rate: mean 80 bpm, from 60 to 100 bpm, over 3 minutes.");
        log.Datasets.Should().Equal("heart_rates");

        var week = await new HeartRateDataset().RunAsync(
            Request(new HeartRateParameters(), new ServiceCollection().AddSingleton(heart.Object).BuildServiceProvider(), new ReportReadLog(), TimeSpan.FromDays(7)),
            CancellationToken.None);
        var first = week.Rows.First();
        new[] { first["bpm"], first["bpmMin"], first["bpmMax"] }.Should().Equal([70.0, 60.0, 80.0], "a week is cut into seven-minute slots");
        week.Resolution!.IntervalMinutes.Should().Be(7);
    }

    [Fact]
    public async Task Steps_are_added_up_per_slot_without_records_that_may_be_running_totals()
    {
        var steps = new Mock<IStepCountService>();
        steps.Setup(s => s.GetStepCountsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new StepCount { Timestamp = To.AddHours(-2).UtcDateTime, Metric = 300 },
                new StepCount { Timestamp = To.AddHours(-2).AddMinutes(5).UtcDateTime, Metric = 200 },
                new StepCount { Timestamp = To.AddHours(-1).UtcDateTime, Metric = 9000, Source = StepCount.PossibleRunningTotalFlag },
            ]);

        var output = await new StepsDataset().RunAsync(
            Request(new StepsParameters(), new ServiceCollection().AddSingleton(steps.Object).BuildServiceProvider(), new ReportReadLog(), TimeSpan.FromDays(1)),
            CancellationToken.None);

        output.Rows.Should().ContainSingle().Which["steps"].Should().Be(500.0);
        output.Facts[0].Value.Should().Be("500");
    }

    [Fact]
    public async Task A_sleep_session_is_a_span_and_hours_on_its_night_without_its_heart_figures()
    {
        var sleep = new Mock<ISleepSessionRepository>();
        sleep.Setup(s => s.GetSessionsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SleepSession
                {
                    StartTime = To.AddDays(-1).AddHours(-2).UtcDateTime, EndTime = To.AddDays(-1).AddHours(6).UtcDateTime,
                    Type = SleepSessionType.Overnight, TotalSleepMs = 7 * 3_600_000L + 1_800_000, AvgHeartRate = 55,
                },
                new SleepSession
                {
                    StartTime = To.AddHours(-10).UtcDateTime, EndTime = To.AddHours(-9).UtcDateTime, Type = SleepSessionType.Nap, TotalSleepMs = 3_600_000,
                },
            ]);

        var output = await new SleepDataset().RunAsync(
            Request(new SleepParameters(), new ServiceCollection().AddSingleton(sleep.Object).BuildServiceProvider(), new ReportReadLog(), TimeSpan.FromDays(3)),
            CancellationToken.None);

        output.Rows.Select(r => r["kind"]).Should().Equal("Overnight", "Nap");
        output.Rows[0]["totalSleep"].Should().Be(7.5);
        output.Rows[0]["night"].Should().Be(To.AddDays(-1).ToUnixTimeMilliseconds());
        output.Rows.Should().OnlyContain(r => r.Keys.All(k => !k.Contains("heart", StringComparison.OrdinalIgnoreCase)));
        new SleepDataset().Fields.Select(f => f.Name).Should().NotContain(n => n.Contains("Heart") || n.Contains("Hrv") || n.Contains("Spo2"));
        output.Sentence.Should().Be("Overnight sleep: median 7.5 h, from 7.5 h to 7.5 h over 1 night.");
    }

    [Theory]
    [InlineData("heart_rate", Scope.HeartRateRead)]
    [InlineData("steps", Scope.StepCountRead)]
    [InlineData("sleep", Scope.SleepRead)]
    public void Each_signal_needs_its_own_read_scope(string name, string scope)
    {
        ReportCatalogueTests.Catalogue(false, Scope.GlucoseRead, Scope.TreatmentsRead).Datasets.Single(d => d.Name == name).Permitted.Should().BeFalse();
        ReportCatalogueTests.Catalogue(false, scope).Datasets.Single(d => d.Name == name).Permitted.Should().BeTrue();
    }
}
