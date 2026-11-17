using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Nocturne.Connectors.Oura.Export;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Oura.Tests.Export;

public class OuraExportArchiveTests
{
    private const string SleepHeader =
        "id;average_breath;average_heart_rate;average_hrv;awake_time;bedtime_end;bedtime_start;day;" +
        "deep_sleep_duration;efficiency;heart_rate;hrv;latency;light_sleep_duration;lowest_heart_rate;" +
        "rem_sleep_duration;restless_periods;sleep_phase_5_min;time_in_bed;total_sleep_duration;type";

    private static readonly DateTime Bedtime = new(2026, 3, 1, 21, 0, 0, DateTimeKind.Utc);

    private static string LongSleep(string id = "night-1", string phases = "4221134") =>
        $"{id};14.5;52.3;61;300;2026-03-02T06:00:00+02:00;2026-03-01T23:00:00+02:00;2026-03-02;" +
        "5400;92;" +
        "\"{\"\"interval\"\":300.0,\"\"items\"\":[55.0,null,51.0],\"\"timestamp\"\":\"\"2026-03-01T23:00:00.000+02:00\"\"}\";" +
        "\"{\"\"interval\"\":300.0,\"\"items\"\":[null,48.0,70.0],\"\"timestamp\"\":\"\"2026-03-01T23:00:00.000+02:00\"\"}\";" +
        $"600;14400;47;3600;12;{phases};25200;23400;long_sleep";

    [Fact]
    public void ALongSleep_BecomesAnOvernightSessionWithTheFiguresOuraRecorded()
    {
        using var export = Open(("sleepmodel.csv", Csv(SleepHeader, LongSleep())));

        var session = export.ReadSleepSessions().Should().ContainSingle().Subject;

        session.StartTime.Should().Be(Bedtime);
        session.EndTime.Should().Be(Bedtime.AddHours(7));
        session.Type.Should().Be(SleepSessionType.Overnight);
        session.IsMainSleep.Should().BeTrue();
        session.DetectionMethod.Should().Be(SleepDetectionMethod.Device);
        session.DurationMs.Should().Be(7 * 3_600_000);
        session.TotalSleepMs.Should().Be(23_400_000);
        session.TotalAwakeMs.Should().Be(300_000);
        session.DeepSleepMs.Should().Be(5_400_000);
        session.LightSleepMs.Should().Be(14_400_000);
        session.RemSleepMs.Should().Be(3_600_000);
        session.SleepLatencyMs.Should().Be(600_000);
        session.Efficiency.Should().Be(92);
        session.RestlessPeriods.Should().Be(12);
        session.AvgHeartRate.Should().Be(52.3f);
        session.MinHeartRate.Should().Be(47);
        session.AvgHrv.Should().Be(61);
        session.AvgBreathRate.Should().Be(14.5f);
        session.Source.Should().Be(SleepSource.Oura);
        session.SourceApp.Should().Be("Oura");
        session.OriginalId.Should().Be("oura:night-1");
    }

    [Fact]
    public void TheHypnogram_IsMergedIntoOneIntervalPerRunOfAStage()
    {
        using var export = Open(("sleepmodel.csv", Csv(SleepHeader, LongSleep(phases: "4221134"))));

        var stages = export.ReadSleepSessions().Single().Stages!;

        stages.Select(stage => (stage.Stage, stage.StartTime, stage.EndTime, stage.Ordinal)).Should().Equal(
            (SleepStageType.Awake, Bedtime, Bedtime.AddMinutes(5), 0),
            (SleepStageType.Light, Bedtime.AddMinutes(5), Bedtime.AddMinutes(15), 1),
            (SleepStageType.Deep, Bedtime.AddMinutes(15), Bedtime.AddMinutes(25), 2),
            (SleepStageType.Rem, Bedtime.AddMinutes(25), Bedtime.AddMinutes(30), 3),
            (SleepStageType.Awake, Bedtime.AddMinutes(30), Bedtime.AddMinutes(35), 4));
    }

    [Fact]
    public void TheThirtySecondHypnogram_IsPreferredAndStopsAtTheBedtimeEnd()
    {
        var header = SleepHeader + ";sleep_phase_30_sec";
        var row = "nap-1;;;;;2026-03-01T14:01:00Z;2026-03-01T14:00:00Z;2026-03-01;;;;;;;;;;44;60;30;sleep;223";
        using var export = Open(("sleepmodel.csv", Csv(header, row)));

        var session = export.ReadSleepSessions().Single();

        session.Type.Should().Be(SleepSessionType.Nap);
        session.IsMainSleep.Should().BeFalse();
        var start = new DateTime(2026, 3, 1, 14, 0, 0, DateTimeKind.Utc);
        session.Stages!.Select(stage => (stage.Stage, stage.StartTime, stage.EndTime)).Should().Equal(
            (SleepStageType.Light, start, start.AddSeconds(60)));
    }

    [Fact]
    public void HeartRateAndHrvSeries_AreMergedBySampleTime()
    {
        using var export = Open(("sleepmodel.csv", Csv(SleepHeader, LongSleep())));

        var samples = export.ReadSleepSessions().Single().BiometricSamples!;

        samples.Select(sample => (sample.Timestamp, sample.HeartRate, sample.Hrv)).Should().Equal(
            (Bedtime, 55f, (float?)null),
            (Bedtime.AddMinutes(5), null, 48f),
            (Bedtime.AddMinutes(10), 51f, 70f));
    }

    [Fact]
    public void TheDailyScoreAndSpo2_GoToTheLongestLongSleepOfTheDay()
    {
        var shorter = LongSleep("night-0").Replace(";23400;long_sleep", ";12000;long_sleep")
            .Replace("2026-03-01T23:00:00+02:00", "2026-03-01T15:00:00+02:00")
            .Replace("2026-03-02T06:00:00+02:00", "2026-03-01T19:00:00+02:00");
        using var export = Open(
            ("sleepmodel.csv", Csv(SleepHeader, shorter, LongSleep())),
            ("dailysleep.csv", Csv("id;contributors;day;score;timestamp", "d1;{};2026-03-02;84;2026-03-02T00:00:00+00:00")),
            ("dailyspo2.csv", Csv("id;breathing_disturbance_index;day;spo2_percentage",
                "s1;3;2026-03-02;\"{\"\"average\"\":96.8}\"")));

        var sessions = export.ReadSleepSessions();

        var main = sessions.Single(session => session.OriginalId == "oura:night-1");
        main.IsMainSleep.Should().BeTrue();
        main.SleepScore.Should().Be(84);
        main.AvgSpo2.Should().Be(96.8f);
        var other = sessions.Single(session => session.OriginalId == "oura:night-0");
        other.IsMainSleep.Should().BeFalse();
        other.SleepScore.Should().BeNull();
        other.AvgSpo2.Should().BeNull();
    }

    [Fact]
    public void DeletedAndUndatedPeriods_AreSkipped()
    {
        var deleted = LongSleep("gone").Replace(";long_sleep", ";deleted");
        var undated = "x;;;;;;;2026-03-02;;;;;;;;;;;;;long_sleep";
        using var export = Open(("sleepmodel.csv", Csv(SleepHeader, deleted, undated, LongSleep())));

        export.ReadSleepSessions().Select(session => session.OriginalId).Should().Equal("oura:night-1");
    }

    [Fact]
    public void HeartRates_CarryASyncKeyFromTheirTimestamp_AndImpossibleValuesAreDropped()
    {
        using var export = Open(("heartrate.csv", Csv("bpm;source;timestamp",
            "61;awake;2026-03-01T10:00:00+00:00",
            "0;awake;2026-03-01T10:05:00+00:00",
            "400;workout;2026-03-01T10:10:00+00:00",
            ";rest;2026-03-01T10:15:00+00:00",
            "58;rest;2026-03-01T12:20:00+02:00")));

        var readings = export.ReadHeartRates().ToList();

        readings.Select(reading => (reading.Timestamp, reading.Bpm, reading.UtcOffset)).Should().Equal(
            (new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), 61, (int?)null),
            (new DateTime(2026, 3, 1, 10, 20, 0, DateTimeKind.Utc), 58, 120));
        readings.Should().OnlyContain(reading => reading.DataSource == DataSources.OuraConnector);
        readings[0].SyncIdentifier.Should().Be($"oura:heart-rate:{readings[0].Mills}");
        readings.Select(reading => reading.SyncIdentifier).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DailySteps_AreOneRecordPerDayAtTheStartOfTheOuraDay()
    {
        using var export = Open(("dailyactivity.csv", Csv("id;day;score;steps;timestamp",
            "a1;2026-03-01;80;9123;2026-03-01T04:00:00+02:00",
            "a2;2026-03-02;10;0;2026-03-02T04:00:00+02:00",
            "a3;2026-03-03;75;4000;")));

        var days = export.ReadDailySteps();

        days.Select(day => (day.Timestamp, day.Metric, day.SyncIdentifier)).Should().Equal(
            (new DateTime(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc), 9123, "oura:steps:2026-03-01"),
            (new DateTime(2026, 3, 3, 12, 0, 0, DateTimeKind.Utc), 4000, "oura:steps:2026-03-03"));
        days.Should().OnlyContain(day => day.DataSource == DataSources.OuraConnector && day.Source == 0);
    }

    [Fact]
    public void ARecordWrappedWholeInQuotes_IsReadAsItsFields()
    {
        var wrapped = "\"" + "61;awake;2026-03-01T10:00:00+00:00" + "\"";
        using var export = Open(("heartrate.csv", Csv("bpm;source;timestamp", wrapped)));

        export.ReadHeartRates().Should().ContainSingle().Which.Bpm.Should().Be(61);
    }

    [Fact]
    public void ARecordWithoutItsLeadingId_IsAlignedToTheEndOfTheHeader()
    {
        using var export = Open(("dailyactivity.csv", Csv("id;day;score;steps;timestamp",
            "2026-03-01;80;9123;2026-03-01T04:00:00+02:00")));

        export.ReadDailySteps().Should().ContainSingle().Which.Metric.Should().Be(9123);
    }

    [Fact]
    public void ARecordThatKeepsItsIdAndLacksItsLastColumns_IsAlignedToTheStartOfTheHeader()
    {
        using var export = Open(("dailyactivity.csv", Csv("id;day;score;steps;timestamp",
            "0b8f6c1e-3d2a-4c5b-9e7f-1a2b3c4d5e6f;2026-03-03;75;4000")));

        var day = export.ReadDailySteps().Should().ContainSingle().Subject;

        day.Metric.Should().Be(4000);
        day.SyncIdentifier.Should().Be("oura:steps:2026-03-03");
    }

    [Fact]
    public void APeriodLongerThanAnySleep_IsSkipped()
    {
        var month = LongSleep("month").Replace("2026-03-02T06:00:00+02:00", "2026-03-31T23:00:00+02:00");
        using var export = Open(("sleepmodel.csv", Csv(SleepHeader, month, LongSleep())));

        export.ReadSleepSessions().Select(session => session.OriginalId).Should().Equal("oura:night-1");
    }

    [Fact]
    public void AHypnogramLongerThanItsPeriod_ProducesNoStagePastTheBedtimeEnd()
    {
        var alternating = string.Concat(Enumerable.Repeat("12", 10_000));
        using var export = Open(("sleepmodel.csv", Csv(SleepHeader, LongSleep(phases: alternating))));

        var stages = export.ReadSleepSessions().Single().Stages!;

        stages.Should().HaveCount(7 * 12);
        stages[^1].EndTime.Should().Be(Bedtime.AddHours(7));
    }

    [Fact]
    public void SamplesOutsideThePeriodOrCloserThanAnEpoch_AreDropped()
    {
        var early = LongSleep().Replace(
            "[55.0,null,51.0],\"\"timestamp\"\":\"\"2026-03-01T23:00:00.000+02:00",
            "[55.0,null,51.0],\"\"timestamp\"\":\"\"2026-03-01T22:55:00.000+02:00");
        var dense = LongSleep("dense").Replace("\"\"interval\"\":300.0", "\"\"interval\"\":0.001");
        using var export = Open(("sleepmodel.csv", Csv(SleepHeader, early, dense)));

        var sessions = export.ReadSleepSessions().ToList();

        sessions[0].BiometricSamples!.Select(sample => (sample.Timestamp, sample.HeartRate)).Should().Equal(
            (Bedtime.AddMinutes(5), 51f), (Bedtime.AddMinutes(10), (float?)null));
        sessions[1].BiometricSamples.Should().BeNull();
    }

    [Fact]
    public void MoreDaysThanARingCouldRecord_AreRefused()
    {
        var rows = Enumerable.Range(0, OuraExportArchive.MaxDailyRows + 1)
            .Select(i => $"a{i};{new DateOnly(1, 1, 1).AddDays(i):yyyy-MM-dd};80;9000;")
            .ToArray();
        using var export = Open(
            ("dailyactivity.csv", Csv("id;day;score;steps;timestamp", rows)),
            ("dailysleep.csv", Csv("id;day;score;steps;timestamp", rows)),
            ("sleepmodel.csv", Csv(SleepHeader, LongSleep())));

        var steps = () => export.ReadDailySteps();
        var sleep = () => export.ReadSleepSessions().ToList();

        steps.Should().Throw<OuraExportException>().Which.Code.Should().Be(OuraExportException.TooMuchData);
        sleep.Should().Throw<OuraExportException>().Which.Code.Should().Be(OuraExportException.TooMuchData);
    }

    [Fact]
    public void MoreStagesAndSamplesThanAnExportHolds_AreRefused()
    {
        var twoDays = string.Concat(Enumerable.Repeat("12", 2 * 24 * 60));
        var perPeriod = twoDays.Length;
        var rows = Enumerable.Range(0, OuraExportArchive.MaxSleepStagesAndSamples / perPeriod + 1)
            .Select(i => LongSleep($"p{i}").Replace("2026-03-02T06:00:00+02:00", "2026-03-03T23:00:00+02:00") + ";" + twoDays)
            .ToArray();
        using var export = Open(("sleepmodel.csv", Csv(SleepHeader + ";sleep_phase_30_sec", rows)));

        var read = () => export.ReadSleepSessions().Count();

        read.Should().Throw<OuraExportException>().Which.Code.Should().Be(OuraExportException.TooMuchData);
    }

    [Fact]
    public void ARecordPastTheLargestAnExportHolds_IsRefused()
    {
        var huge = "61;" + new string('x', OuraCsv.MaxRecordChars) + ";2026-03-01T10:00:00+00:00";
        using var export = Open(("heartrate.csv", Csv("bpm;source;timestamp", huge)));

        var read = () => export.ReadHeartRates().ToList();

        read.Should().Throw<OuraExportException>().Which.Code.Should().Be(OuraExportException.TooMuchData);
    }

    [Fact]
    public void AFileCompressedByAMethodThatCannotBeRead_IsRefusedAsUnreadable()
    {
        var stream = Zip(("heartrate.csv", Csv("bpm;source;timestamp", "61;awake;2026-03-01T10:00:00+00:00")));
        var bytes = stream.GetBuffer().AsSpan(0, (int)stream.Length);
        const byte unknownMethod = 99;
        bytes[bytes.IndexOf("PK\u0003\u0004"u8) + 8] = unknownMethod;
        bytes[bytes.IndexOf("PK\u0001\u0002"u8) + 10] = unknownMethod;
        using var export = OuraExportArchive.Open(stream);

        var read = () => export.ReadHeartRates().ToList();

        read.Should().Throw<OuraExportException>().Which.Code.Should().Be(OuraExportException.Unreadable);
    }

    [Fact]
    public void FilesAreFoundInsideAFolderAndWhateverTheirCase()
    {
        using var export = Open(("oura_export_2026/HeartRate.csv",
            Csv("bpm;source;timestamp", "61;awake;2026-03-01T10:00:00+00:00")));

        export.ReadHeartRates().Should().ContainSingle();
        export.ReadSleepSessions().Should().BeEmpty();
        export.ReadDailySteps().Should().BeEmpty();
    }

    [Fact]
    public void AFileThatIsNotAZip_IsRefused()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("id;day\n1;2026-03-01\n"));

        var open = () => OuraExportArchive.Open(stream);

        open.Should().Throw<OuraExportException>().Which.Code.Should().Be(OuraExportException.NotAZip);
    }

    [Fact]
    public void AZipWithNoneOfTheExportFiles_IsRefused()
    {
        var open = () => Open(("readme.txt", "hello"));

        open.Should().Throw<OuraExportException>().Which.Code.Should().Be(OuraExportException.NotAnOuraExport);
    }

    private static string Csv(string header, params string[] rows) => string.Join("\r\n", [header, .. rows]) + "\r\n";

    private static OuraExportArchive Open(params (string Name, string Content)[] files) =>
        OuraExportArchive.Open(Zip(files));

    private static MemoryStream Zip(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
