using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class ErroneousReadingEpisodesTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long From = T0.ToMills();
    private static readonly long To = T0.AddDays(1).ToMills();

    private static SensorGlucose At(double minutes, double mgdl, SensorGlucoseQuality? quality = null, string? status = null) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = T0.AddMinutes(minutes),
        Mgdl = mgdl,
        Quality = quality,
        SensorStatus = status,
        DataSource = "xDrip",
    };

    /// <summary>Every five minutes from 0 to 300, with the given ones replaced.</summary>
    private static List<SensorGlucose> Series(params SensorGlucose[] replacing)
    {
        var byMinute = Enumerable.Range(0, 61).ToDictionary(i => i * 5.0, i => At(i * 5, 120));
        foreach (var r in replacing)
            byMinute[(r.Timestamp - T0).TotalMinutes] = r;
        return [.. byMinute.Values];
    }

    private static ErroneousReadingsReport Build(
        IEnumerable<SensorGlucose> readings,
        IEnumerable<StateSpan>? spans = null,
        IEnumerable<Note>? notes = null,
        IEnumerable<ErroneousEpisodeMeterReading>? meters = null,
        TimeZoneInfo? zone = null) =>
        ErroneousReadingEpisodes.Build(From, To, readings, spans ?? [], notes ?? [], meters ?? [], zone);

    private static List<ErroneousReadingEpisode> Episodes(ErroneousReadingsReport report) =>
        [.. report.Days.SelectMany(d => d.Episodes)];

    private static List<long> Isolated(ErroneousReadingsReport report) =>
        [.. report.Days.SelectMany(d => d.IsolatedReadingMills)];

    [Fact]
    public void InvalidReadings_WithAnUncertainOneBetween_FormOneEpisode()
    {
        var report = Build(Series(
            At(100, 60, SensorGlucoseQuality.Invalid, "G7:Errors/info2"),
            At(105, 70, SensorGlucoseQuality.Uncertain, "Noisy"),
            At(110, 50, SensorGlucoseQuality.Invalid, "G7:Errors/info2")));

        var episode = Episodes(report).Should().ContainSingle().Subject;
        episode.Kind.Should().Be(ErroneousEpisodeKind.Readings);
        episode.StartMills.Should().Be(T0.AddMinutes(100).ToMills());
        episode.EndMills.Should().Be(T0.AddMinutes(110).ToMills());
        episode.InvalidCount.Should().Be(2);
        episode.UncertainCount.Should().Be(1);
        episode.MinMgdl.Should().Be(50);
        episode.MaxMgdl.Should().Be(70);
        episode.ExcludedMinutes.Should().Be(10);
        episode.SensorStatuses.Should().BeEquivalentTo("G7:Errors/info2", "Noisy");
    }

    [Fact]
    public void FlaggedReadingsMoreThanFifteenMinutesApart_AreSeparateEpisodes()
    {
        var report = Build(Series(
            At(100, 60, SensorGlucoseQuality.Invalid),
            At(105, 60, SensorGlucoseQuality.Invalid),
            At(125, 60, SensorGlucoseQuality.Invalid),
            At(130, 60, SensorGlucoseQuality.Invalid)));

        var episodes = report.Days.Should().ContainSingle().Subject.Episodes;
        episodes.Should().HaveCount(2);
        episodes[0].StartMills.Should().BeLessThan(episodes[1].StartMills, "oldest first, along the chart");
    }

    [Fact]
    public void UncertainReadingsAlone_AreCountedButNotListed()
    {
        var report = Build(Series(
            At(100, 120, SensorGlucoseQuality.Uncertain),
            At(105, 120, SensorGlucoseQuality.Uncertain)));

        Episodes(report).Should().BeEmpty();
        report.Summary.UncertainReadings.Should().Be(2);
        report.Summary.InvalidReadings.Should().Be(0);
    }

    [Fact]
    public void UncertainReadingsOutsideTheInvalidOnes_StayOutOfTheEpisode()
    {
        var report = Build(Series(
            At(95, 120, SensorGlucoseQuality.Uncertain),
            At(100, 60, SensorGlucoseQuality.Invalid),
            At(105, 65, SensorGlucoseQuality.Invalid),
            At(110, 120, SensorGlucoseQuality.Uncertain)));

        var episode = Episodes(report).Should().ContainSingle().Subject;
        episode.UncertainCount.Should().Be(0);
        episode.StartMills.Should().Be(T0.AddMinutes(100).ToMills());
        episode.EndMills.Should().Be(T0.AddMinutes(105).ToMills());
    }

    [Fact]
    public void ALoneInvalidReading_IsCountedAsIsolated_NotListed()
    {
        var report = Build(Series(
            At(100, 60, SensorGlucoseQuality.Invalid),
            At(200, 65, SensorGlucoseQuality.Invalid),
            At(205, 70, SensorGlucoseQuality.Invalid)));

        Episodes(report).Should().ContainSingle().Which.StartMills.Should().Be(T0.AddMinutes(200).ToMills());
        report.Summary.InvalidReadings.Should().Be(3);
        report.Summary.IsolatedReadings.Should().Be(1);
        Isolated(report).Should().Equal(T0.AddMinutes(100).ToMills());
        report.Summary.ExcludedMinutes.Should().Be(15, "an isolated reading is still left out of statistics");
    }

    [Fact]
    public void Episode_CarriesNearbyFingerSticks()
    {
        var stick = new ErroneousEpisodeMeterReading { Mills = T0.AddMinutes(125).ToMills(), Mgdl = 112 };
        var farStick = new ErroneousEpisodeMeterReading { Mills = T0.AddMinutes(200).ToMills(), Mgdl = 140 };

        var report = Build(
            Series(At(100, 60, SensorGlucoseQuality.Invalid), At(105, 64, SensorGlucoseQuality.Invalid)),
            meters: [stick, farStick]);

        var episode = Episodes(report).Should().ContainSingle().Subject;
        episode.MeterReadings.Should().ContainSingle().Which.Mgdl.Should().Be(112);
    }

    [Fact]
    public void SensorErrorSpan_IsAnEpisodeWithoutReadings()
    {
        var span = new StateSpan
        {
            Category = StateSpanCategory.DataExclusion,
            State = ErroneousReadingEpisodes.SensorErrorState,
            StartTimestamp = T0.AddMinutes(60),
            EndTimestamp = T0.AddMinutes(90),
            Source = "Trio",
        };
        var compressionLow = new StateSpan
        {
            Category = StateSpanCategory.DataExclusion,
            State = "CompressionLow",
            StartTimestamp = T0.AddMinutes(200),
            EndTimestamp = T0.AddMinutes(230),
        };

        var report = Build(Series(), spans: [span, compressionLow]);

        var episode = Episodes(report).Should().ContainSingle().Subject;
        episode.Kind.Should().Be(ErroneousEpisodeKind.Span);
        episode.InvalidCount.Should().Be(0);
        episode.Source.Should().Be("Trio");
    }

    [Fact]
    public void TrioNote_RunsToTheNextReading()
    {
        var readings = Series().Where(r => r.Timestamp < T0.AddMinutes(60) || r.Timestamp > T0.AddMinutes(90)).ToList();
        var note = new Note { Timestamp = T0.AddMinutes(62), Text = "CGM: temporarySensorIssue", Device = "Trio" };
        var userNote = new Note { Timestamp = T0.AddMinutes(70), Text = "CGM: changed", Device = "xDrip" };

        var report = Build(readings, notes: [note, userNote]);

        var episode = Episodes(report).Should().ContainSingle().Subject;
        episode.Kind.Should().Be(ErroneousEpisodeKind.Note);
        episode.EndMills.Should().Be(T0.AddMinutes(95).ToMills());
        episode.SensorStatuses.Should().Equal("temporarySensorIssue");
    }

    [Fact]
    public void EpisodesStartingOutsideTheWindow_AreLeftOut()
    {
        var readings = Series()
            .Append(At(-35, 60, SensorGlucoseQuality.Invalid))
            .Append(At(-30, 60, SensorGlucoseQuality.Invalid))
            .ToList();

        var report = Build(readings);

        Episodes(report).Should().BeEmpty();
        report.Summary.InvalidReadings.Should().Be(0);
    }

    [Fact]
    public void Days_AreCutInThePatientsZone_NewestFirst_AndStretchToAnEpisodeThatRunsPastMidnight()
    {
        // Madrid is UTC+2 on 22 Oct 2026, so its midnight falls at 22:00 UTC, 600 minutes after T0.
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        static long Madrid(int day) => new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.FromHours(2)).ToUnixTimeMilliseconds();
        var readings = Series(
            At(105, 60, SensorGlucoseQuality.Invalid),
            At(110, 60, SensorGlucoseQuality.Invalid),
            At(200, 60, SensorGlucoseQuality.Invalid));
        readings.AddRange([
            At(590, 300, SensorGlucoseQuality.Invalid), // 23:50 in Madrid
            At(595, 300, SensorGlucoseQuality.Invalid),
            At(605, 300, SensorGlucoseQuality.Invalid),
            At(610, 300, SensorGlucoseQuality.Invalid), // 00:10 on the 23rd
            At(700, 50, SensorGlucoseQuality.Invalid),  // 01:40 on the 23rd
            At(705, 50, SensorGlucoseQuality.Invalid),
        ]);

        var report = Build(readings, zone: madrid);

        report.Days.Select(d => d.Date).Should().Equal("2026-10-23", "2026-10-22");
        var first = report.Days[1];
        first.FromMills.Should().Be(Madrid(22));
        first.Episodes.Select(e => e.StartMills).Should().Equal(T0.AddMinutes(105).ToMills(), T0.AddMinutes(590).ToMills());
        first.IsolatedReadingMills.Should().Equal(T0.AddMinutes(200).ToMills());
        first.InvalidReadings.Should().Be(7);
        first.ToMills.Should().Be(T0.AddMinutes(610).ToMills(), "the late episode runs past midnight");
        report.Days[0].FromMills.Should().Be(Madrid(23));
        report.Days[0].ToMills.Should().Be(Madrid(24));
    }

    [Fact]
    public void WithNoZone_DaysAreCutInUtc()
    {
        var report = Build(Series(At(100, 60, SensorGlucoseQuality.Invalid), At(105, 60, SensorGlucoseQuality.Invalid)));

        var day = report.Days.Should().ContainSingle().Subject;
        day.Date.Should().Be("2026-10-22");
        day.FromMills.Should().Be(new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc).ToMills());
    }
}
