using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Oura.Export;

/// <summary>
/// The archive Oura produces at membership.ouraring.com/data-export: one semicolon-separated file
/// per data set, with the columns of Oura's API v2 documents. Reads the three data sets Nocturne
/// has models for: sleep periods, heart rate and daily steps.
/// </summary>
/// <remarks>
/// Every record carries a key derived from Oura's own identity for it, so importing a newer
/// export over an older one updates the records both contain.
/// </remarks>
public sealed class OuraExportArchive : IDisposable
{
    public const string SourceApp = "Oura";

    private const string SleepFile = "sleepmodel.csv";
    private const string DailySleepFile = "dailysleep.csv";
    private const string DailySpo2File = "dailyspo2.csv";
    private const string HeartRateFile = "heartrate.csv";
    private const string DailyActivityFile = "dailyactivity.csv";

    /// <summary>
    /// A year of five-minute heart rate is about 4 MB, and a sleep period row a few kilobytes. A file
    /// past these is not an export, and every heart rate row past the cap would still be a database write.
    /// </summary>
    private const long MaxSummaryFileBytes = 64L * 1024 * 1024;
    private const long MaxHeartRateFileBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Row and item budgets, each several times what a ring worn since Oura's first one could have
    /// recorded. The byte caps bound what is read; these bound what is held and what is written,
    /// which a file of short rows would otherwise set.
    /// </summary>
    internal const int MaxDailyRows = 50_000;
    internal const int MaxHeartRateRows = 2_000_000;
    internal const int MaxSleepStagesAndSamples = 2_000_000;

    /// <summary>Oura stores no heart rate outside this range; a value beyond it is a corrupt row.</summary>
    private const int MinBpm = 20, MaxBpm = 250;

    private readonly ZipArchive archive;
    private readonly Dictionary<string, ZipArchiveEntry> files = new(StringComparer.OrdinalIgnoreCase);

    private OuraExportArchive(ZipArchive archive)
    {
        this.archive = archive;
        foreach (var entry in archive.Entries.Where(entry => entry.Name.Length > 0))
            files.TryAdd(entry.Name, entry);
    }

    /// <summary>Opens a seekable stream as an export. The caller keeps ownership of the stream.</summary>
    /// <exception cref="OuraExportException">The stream is not a ZIP, or holds none of the files read here.</exception>
    public static OuraExportArchive Open(Stream zip)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new OuraExportException(OuraExportException.NotAZip, ex);
        }

        var export = new OuraExportArchive(archive);
        try
        {
            if (!export.files.ContainsKey(SleepFile) && !export.files.ContainsKey(HeartRateFile) &&
                !export.files.ContainsKey(DailyActivityFile))
                throw new OuraExportException(OuraExportException.NotAnOuraExport);

            // Refused here, before a caller has written anything from an earlier file.
            foreach (var (file, maxBytes) in FileLimits)
                if (export.files.TryGetValue(file, out var entry) && entry.Length > maxBytes)
                    throw new OuraExportException(OuraExportException.TooMuchData);
        }
        catch
        {
            export.Dispose();
            throw;
        }

        return export;
    }

    private static readonly (string File, long MaxBytes)[] FileLimits =
    [
        (SleepFile, MaxSummaryFileBytes), (DailySleepFile, MaxSummaryFileBytes), (DailySpo2File, MaxSummaryFileBytes),
        (DailyActivityFile, MaxSummaryFileBytes), (HeartRateFile, MaxHeartRateFileBytes),
    ];

    /// <summary>
    /// Streams the sleep periods, one session in memory at a time: a period's stages and samples are
    /// the bulk of an export, and the file is read twice so that none of them is held while the
    /// day's main sleep is chosen.
    /// </summary>
    public IEnumerable<SleepSession> ReadSleepSessions()
    {
        // Oura scores a day, not a period: the score and SpO2 belong to the day's longest long sleep.
        var mainRowByDay = new Dictionary<string, (int Row, long TotalSleepMs)>(StringComparer.Ordinal);
        var rowIndex = 0;
        foreach (var row in Rows(SleepFile, MaxSummaryFileBytes))
        {
            if (++rowIndex > MaxDailyRows) throw new OuraExportException(OuraExportException.TooMuchData);
            if (SleepPeriod.From(row) is not { Kind: "long_sleep", Day: { } day } period) continue;
            if (!mainRowByDay.TryGetValue(day, out var main) || period.Session.TotalSleepMs > main.TotalSleepMs)
                mainRowByDay[day] = (rowIndex, period.Session.TotalSleepMs);
        }

        var scores = DailyValues(DailySleepFile, row => Number(row["score"]));
        var spo2 = DailyValues(DailySpo2File, row => JsonNumber(row["spo2_percentage"], "average"));

        rowIndex = 0;
        var stagesAndSamples = 0;
        foreach (var row in Rows(SleepFile, MaxSummaryFileBytes))
        {
            rowIndex++;
            if (SleepPeriod.From(row) is not { } period) continue;

            var session = period.Session;
            stagesAndSamples += (session.Stages?.Count ?? 0) + (session.BiometricSamples?.Count ?? 0);
            if (stagesAndSamples > MaxSleepStagesAndSamples)
                throw new OuraExportException(OuraExportException.TooMuchData);
            var isMain = period.Day is not null && mainRowByDay.TryGetValue(period.Day, out var main) && main.Row == rowIndex;
            session.IsMainSleep = isMain;
            if (isMain)
            {
                if (scores.TryGetValue(period.Day!, out var score) && score is >= 0 and <= 100)
                    session.SleepScore = (short)Math.Round(score);
                if (spo2.TryGetValue(period.Day!, out var oxygen) && oxygen is > 0 and <= 100)
                    session.AvgSpo2 = (float)oxygen;
            }

            yield return session;
        }
    }

    /// <summary>
    /// Streams the heart rate file; it holds a reading every five minutes for the ring's whole life.
    /// A reading is keyed by its timestamp alone, so two rows for one instant are one reading and
    /// the later row wins.
    /// </summary>
    public IEnumerable<HeartRate> ReadHeartRates()
    {
        var rows = 0;
        foreach (var row in Rows(HeartRateFile, MaxHeartRateFileBytes))
        {
            if (++rows > MaxHeartRateRows) throw new OuraExportException(OuraExportException.TooMuchData);
            if (Timestamp(row["timestamp"]) is not { } at || Number(row["bpm"]) is not { } bpm) continue;
            if (bpm is < MinBpm or > MaxBpm) continue;

            var mills = at.ToUnixTimeMilliseconds();
            yield return new HeartRate
            {
                Mills = mills,
                UtcOffset = OffsetMinutes(at),
                Bpm = (int)Math.Round(bpm),
                Device = SourceApp,
                EnteredBy = SourceApp,
                DataSource = DataSources.OuraConnector,
                SyncIdentifier = $"oura:heart-rate:{mills}",
            };
        }
    }

    /// <summary>
    /// One record per day, at the start of Oura's day: the export holds a day's step total and no
    /// finer step series.
    /// </summary>
    public IReadOnlyList<StepCount> ReadDailySteps()
    {
        var days = new Dictionary<string, StepCount>(StringComparer.Ordinal);
        foreach (var row in DailyRows(DailyActivityFile))
        {
            var at = Timestamp(row["timestamp"]);
            var day = Day(row["day"]) ?? at?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var steps = Number(row["steps"]) ?? 0;
            if (day is null || steps is <= 0 or > int.MaxValue) continue;

            at ??= DateTimeOffset.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).AddHours(12);
            days[day] = new StepCount
            {
                Mills = at.Value.ToUnixTimeMilliseconds(),
                UtcOffset = OffsetMinutes(at.Value),
                Metric = (int)Math.Round(steps),
                Source = 0,
                Device = SourceApp,
                EnteredBy = SourceApp,
                DataSource = DataSources.OuraConnector,
                SyncIdentifier = $"oura:steps:{day}",
            };
        }

        return [.. days.Values];
    }

    public void Dispose() => archive.Dispose();

    private IEnumerable<OuraCsvRow> Rows(string file, long maxBytes)
    {
        if (!files.TryGetValue(file, out var entry)) yield break;

        using var stream = new BoundedStream(Unreadable(entry.Open), maxBytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var rows = OuraCsv.Read(reader).GetEnumerator();
        while (Unreadable(rows.MoveNext))
            yield return rows.Current;
    }

    /// <summary>An entry that is encrypted, damaged or compressed by a method .NET cannot read fails only when it is read.</summary>
    private static T Unreadable<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new OuraExportException(OuraExportException.Unreadable, ex);
        }
    }

    private Dictionary<string, double> DailyValues(string file, Func<OuraCsvRow, double?> value)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in DailyRows(file))
            if (Day(row["day"]) is { } day && value(row) is { } number)
                values[day] = number;
        return values;
    }

    private IEnumerable<OuraCsvRow> DailyRows(string file)
    {
        var rows = 0;
        foreach (var row in Rows(file, MaxSummaryFileBytes))
        {
            if (++rows > MaxDailyRows) throw new OuraExportException(OuraExportException.TooMuchData);
            yield return row;
        }
    }

    private static int? OffsetMinutes(DateTimeOffset at) =>
        at.Offset == TimeSpan.Zero ? null : (int)at.Offset.TotalMinutes;

    private static string? Day(string? value) =>
        value is not null && DateOnly.TryParse(value.AsSpan(0, Math.Min(value.Length, 10)),
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private static DateTimeOffset? Timestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    private static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
        double.IsFinite(number)
            ? number
            : null;

    private static double? JsonNumber(string? json, string property)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(property, out var value) &&
                   value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SleepPeriod(SleepSession Session, string? Day, string Kind)
    {
        private const int MaxSeriesItems = 20_000;

        /// <summary>
        /// No sleep period is this long. With <see cref="MinEpoch"/> it bounds the stages and samples
        /// one row can produce, whatever its hypnogram and series claim.
        /// </summary>
        private static readonly TimeSpan MaxPeriod = TimeSpan.FromHours(48);

        private static readonly TimeSpan MinEpoch = TimeSpan.FromSeconds(30);

        public static SleepPeriod? From(OuraCsvRow row)
        {
            var kind = row["type"]?.ToLowerInvariant() ?? string.Empty;
            if (kind == "deleted") return null;
            if (Timestamp(row["bedtime_start"]) is not { } start || Timestamp(row["bedtime_end"]) is not { } end) return null;
            if (end <= start || end - start > MaxPeriod) return null;

            var startUtc = start.UtcDateTime;
            var endUtc = end.UtcDateTime;
            var stages = Stages(row, startUtc, endUtc);
            var durationMs = (long)(endUtc - startUtc).TotalMilliseconds;
            var totalSleepMs = Milliseconds(row["total_sleep_duration"]) ?? stages
                .Where(stage => stage.Stage is SleepStageType.Deep or SleepStageType.Light or SleepStageType.Rem)
                .Sum(stage => (long)(stage.EndTime - stage.StartTime).TotalMilliseconds);

            var session = new SleepSession
            {
                StartTime = startUtc,
                EndTime = endUtc,
                Type = kind switch
                {
                    "long_sleep" => SleepSessionType.Overnight,
                    "sleep" or "late_nap" => SleepSessionType.Nap,
                    "rest" => SleepSessionType.Rest,
                    _ => SleepSessionType.Unknown,
                },
                DetectionMethod = SleepDetectionMethod.Device,
                DurationMs = durationMs,
                TotalSleepMs = totalSleepMs,
                TotalAwakeMs = Milliseconds(row["awake_time"]),
                DeepSleepMs = Milliseconds(row["deep_sleep_duration"]),
                LightSleepMs = Milliseconds(row["light_sleep_duration"]),
                RemSleepMs = Milliseconds(row["rem_sleep_duration"]),
                SleepLatencyMs = Milliseconds(row["latency"]),
                Efficiency = Number(row["efficiency"]) is >= 0 and <= 100 and var efficiency
                    ? (float)efficiency
                    : (float)(totalSleepMs * 100d / durationMs),
                RestlessPeriods = Number(row["restless_periods"]) is >= 0 and <= int.MaxValue and var restless
                    ? (int)restless
                    : null,
                AvgHeartRate = Positive(row["average_heart_rate"]),
                MinHeartRate = Positive(row["lowest_heart_rate"]),
                AvgHrv = Positive(row["average_hrv"]),
                AvgBreathRate = Positive(row["average_breath"]),
                Source = SleepSource.Oura,
                SourceApp = SourceApp,
                OriginalId = $"oura:{row["id"] ?? start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}",
                Stages = stages.Count > 0 ? stages : null,
                BiometricSamples = Samples(row, startUtc, endUtc),
            };

            return new SleepPeriod(session, OuraExportArchive.Day(row["day"]), kind);
        }

        private static long? Milliseconds(string? seconds) =>
            Number(seconds) is >= 0 and < 7 * 86_400 and var value ? (long)(value * 1000) : null;

        private static float? Positive(string? value) =>
            Number(value) is > 0 and < 1000 and var number ? (float)number : null;

        /// <summary>
        /// Oura's hypnogram is one digit per epoch from the bedtime start: 1 deep, 2 light, 3 REM,
        /// 4 awake. The 30-second series is preferred over the 5-minute one it refines.
        /// </summary>
        private static List<SleepStageInterval> Stages(OuraCsvRow row, DateTime start, DateTime end)
        {
            var (phases, epoch) = row["sleep_phase_30_sec"] is { } fine
                ? (fine, TimeSpan.FromSeconds(30))
                : (row["sleep_phase_5_min"] ?? string.Empty, TimeSpan.FromMinutes(5));

            var stages = new List<SleepStageInterval>();
            var index = 0;
            foreach (var phase in phases.Where(char.IsAsciiDigit).Take(MaxSeriesItems))
            {
                var from = start + epoch * index++;
                if (from >= end) break;
                var to = from + epoch < end ? from + epoch : end;
                var stage = phase switch
                {
                    '1' => SleepStageType.Deep,
                    '2' => SleepStageType.Light,
                    '3' => SleepStageType.Rem,
                    '4' => SleepStageType.Awake,
                    _ => SleepStageType.Unknown,
                };

                if (stages.Count > 0 && stages[^1].Stage == stage) stages[^1].EndTime = to;
                else stages.Add(new SleepStageInterval { StartTime = from, EndTime = to, Stage = stage, Ordinal = stages.Count });
            }

            return stages;
        }

        /// <summary>
        /// The period's <c>heart_rate</c> and <c>hrv</c> columns are <c>{interval, items, timestamp}</c>
        /// series, in seconds, with a null item where the ring had no reading.
        /// </summary>
        private static List<SleepBiometricSample>? Samples(OuraCsvRow row, DateTime periodStart, DateTime end)
        {
            var samples = new SortedDictionary<DateTime, SleepBiometricSample>();
            Merge(row["heart_rate"], (sample, value) => sample.HeartRate = value);
            Merge(row["hrv"], (sample, value) => sample.Hrv = value);
            return samples.Count > 0 ? [.. samples.Values] : null;

            void Merge(string? json, Action<SleepBiometricSample, float> set)
            {
                if (json is null) return;
                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                        !root.TryGetProperty("interval", out var interval) || interval.ValueKind != JsonValueKind.Number ||
                        !root.TryGetProperty("timestamp", out var first) ||
                        Timestamp(first.GetString()) is not { } start)
                        return;

                    var step = TimeSpan.FromSeconds(interval.GetDouble());
                    if (step < MinEpoch) return;

                    var index = 0;
                    foreach (var item in items.EnumerateArray().Take(MaxSeriesItems))
                    {
                        var at = start.UtcDateTime + step * index++;
                        if (at > end) break;
                        if (at < periodStart) continue;
                        if (item.ValueKind != JsonValueKind.Number) continue;
                        var value = item.GetDouble();
                        if (value is <= 0 or >= 1000) continue;
                        if (!samples.TryGetValue(at, out var sample))
                            samples[at] = sample = new SleepBiometricSample { Timestamp = at };
                        set(sample, (float)value);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                }
            }
        }
    }

    /// <summary>Fails a read past <paramref name="maxBytes"/>: an entry's declared length is the archive's own claim.</summary>
    private sealed class BoundedStream(Stream inner, long maxBytes) : Stream
    {
        private long read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytes = inner.Read(buffer, offset, count);
            if ((read += bytes) > maxBytes) throw new OuraExportException(OuraExportException.TooMuchData);
            return bytes;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
