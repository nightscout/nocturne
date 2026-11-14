using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;

namespace Nocturne.API.Services.Seeding;

/// <summary>
/// Where a tenant's continued timeline has got to. The timeline runs from the seed's first day, so
/// rebuilding it replays the whole seed; the feed keeps one per tenant between ticks.
/// </summary>
internal sealed class SampleDataLiveCursor(SampleDataTimeline timeline, IEnumerator<DemoTimeStep> steps)
{
    private DemoTimeStep? _next;

    /// <summary>The timeline as it was when the cursor was built, less how far it is written.</summary>
    public SampleDataTimeline Origin { get; } = timeline with { WrittenThrough = default };

    /// <summary>Records generated at or after the end of the last advance, held until their time comes.</summary>
    public List<Entry> HeldEntries { get; } = [];

    public List<Treatment> HeldTreatments { get; } = [];

    public List<DeviceStatus> HeldStatuses { get; } = [];

    public bool Continues(SampleDataTimeline timeline) => Origin == timeline with { WrittenThrough = default };

    /// <summary>The steps that start before <paramref name="end"/> and were not yet taken.</summary>
    public IEnumerable<DemoTimeStep> StepsBefore(DateTime end)
    {
        while (true)
        {
            if (_next is null)
            {
                if (!steps.MoveNext())
                    yield break;
                _next = steps.Current;
            }

            if (_next.Utc >= end)
                yield break;

            yield return _next;
            _next = null;
        }
    }
}

/// <summary>
/// Carries a seeded tenant's sample data on to the present, so a dev or demo stack stays live after
/// it is seeded: CGM readings, treatments and loop status every five minutes, with the heart rate,
/// steps, sleep and device changes of the same days. It continues the simulation the seed ran (see
/// <see cref="SampleDataTimeline"/>), so the history joins the seed without a jump.
/// </summary>
/// <remarks>
/// Run by <see cref="SampleDataLiveFeedService"/>, which is registered only when
/// <see cref="EnabledKey"/> is set, since a test that seeds sample data expects it to stay as seeded.
/// </remarks>
public sealed class SampleDataLiveFeed
{
    /// <summary>The configuration key that registers the feed.</summary>
    public const string EnabledKey = "SampleData:Live";

    private const int BatchSize = 500;

    /// <summary>
    /// A gap longer than this (the API was stopped) is written as a backfill, so a day of catch-up
    /// does not reach clients and alerts as a day of new readings.
    /// </summary>
    private static readonly TimeSpan LiveGap = TimeSpan.FromMinutes(30);

    private readonly ITenantAccessor _tenantAccessor;
    private readonly INocturneDbContextFactory _contexts;
    private readonly IEntryService _entries;
    private readonly ITreatmentService _treatments;
    private readonly IDeviceStatusService _deviceStatuses;
    private readonly IHeartRateService _heartRates;
    private readonly IStepCountService _stepCounts;
    private readonly ISleepService _sleep;
    private readonly ITempBasalRepository _tempBasals;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SampleDataLiveFeed> _logger;

    public SampleDataLiveFeed(
        ITenantAccessor tenantAccessor,
        INocturneDbContextFactory contexts,
        IEntryService entries,
        ITreatmentService treatments,
        IDeviceStatusService deviceStatuses,
        IHeartRateService heartRates,
        IStepCountService stepCounts,
        ISleepService sleep,
        ITempBasalRepository tempBasals,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        ILogger<SampleDataLiveFeed> logger)
    {
        _tenantAccessor = tenantAccessor;
        _contexts = contexts;
        _entries = entries;
        _treatments = treatments;
        _deviceStatuses = deviceStatuses;
        _heartRates = heartRates;
        _stepCounts = stepCounts;
        _sleep = sleep;
        _tempBasals = tempBasals;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>(EnabledKey);

    /// <summary>
    /// Writes what the scope's tenant's timeline generated since it was last written, up to now.
    /// </summary>
    /// <param name="cursor">The tenant's cursor from the last advance, or <c>null</c>.</param>
    /// <param name="ct">Cancels the advance.</param>
    /// <returns>
    /// The cursor to pass next time, or <c>null</c> when the tenant holds no sample timeline. A caller
    /// whose advance throws drops the cursor: its steps may be taken without being written.
    /// </returns>
    internal async Task<SampleDataLiveCursor?> AdvanceAsync(SampleDataLiveCursor? cursor, CancellationToken ct)
    {
        // A seed holds the gate for minutes; its tenant is skipped until the next tick.
        var gate = SampleDataTimeline.GateFor(_tenantAccessor.TenantId);
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
            return cursor;
        try
        {
            return await AdvanceUngatedAsync(cursor, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SampleDataLiveCursor?> AdvanceUngatedAsync(SampleDataLiveCursor? cursor, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var timeline = await SampleDataTimeline.ReadAsync(db, ct);
        if (timeline is null)
            return null;

        var clock = SeedingClock.Resolve(timeline.ZoneId, _time);
        var from = timeline.WrittenThrough;
        var now = clock.UtcNow;
        if (now <= from)
            return cursor;

        var config = SampleDataSeeder.ConfigurationFor(1);
        if (cursor is null || !cursor.Continues(timeline))
            cursor = new SampleDataLiveCursor(timeline, Steps(timeline, clock, config).GetEnumerator());

        var entries = Take(cursor.HeldEntries, e => e.Mills, from, now);
        var treatments = Take(cursor.HeldTreatments, t => t.Mills, from, now);
        var statuses = Take(cursor.HeldStatuses, s => s.Mills, from, now);
        foreach (var step in cursor.StepsBefore(now))
        {
            var records = SampleDataSeeder.RecordsOf(step, clock, config, timeline.DataSource);
            entries.AddRange(Take([.. records.Entries], e => e.Mills, from, now, cursor.HeldEntries));
            treatments.AddRange(Take([.. records.Treatments], t => t.Mills, from, now, cursor.HeldTreatments));
            if (records.Status is { } status)
                statuses.AddRange(Take([status], s => s.Mills, from, now, cursor.HeldStatuses));
        }

        treatments.AddRange(DeviceChanges(clock, timeline.DataSource, from, now));

        var origin = now - from > LiveGap ? WriteOrigin.Backfill : WriteOrigin.Live;
        foreach (var batch in entries.Chunk(BatchSize))
            await _entries.CreateEntriesAsync(batch, origin, ct);
        foreach (var batch in treatments.OrderBy(t => t.Mills).Chunk(BatchSize))
            await _treatments.CreateTreatmentsAsync(batch, origin, ct);
        foreach (var batch in statuses.Chunk(BatchSize))
            await _deviceStatuses.CreateDeviceStatusesAsync(batch, timeline.DataSource, origin, ct);
        if (treatments.Count > 0)
        {
            await _tempBasals.StampScheduledRatesAsync(
                timeline.DataSource,
                start => DemoTherapyProfile.ScheduledRateAt(clock.Demo.ToLocal(start), config.BasalRate),
                ct);
        }

        await WriteLifestyleAsync(clock, timeline.DataSource, from, now, ct);

        await SampleDataTimeline.WriteAsync(db, timeline with { WrittenThrough = now }, _time.GetUtcNow(), ct);

        if (entries.Count > 0 || treatments.Count > 0)
        {
            _logger.LogDebug(
                "Continued sample data to {Now}: {Entries} entries, {Treatments} treatments, {Statuses} device statuses",
                now, entries.Count, treatments.Count, statuses.Count);
        }

        return cursor;
    }

    private IEnumerable<DemoTimeStep> Steps(
        SampleDataTimeline timeline, SeedingClock clock, DemoModeConfiguration config) =>
        new DemoDataGenerator(
                Options.Create(config),
                clock.Demo,
                _loggerFactory.CreateLogger<DemoDataGenerator>(),
                _loggerFactory)
            .ContinueTimeline(timeline.FirstDay, timeline.SeedEnd);

    /// <summary>
    /// The records of <paramref name="records"/> timestamped in [<paramref name="from"/>,
    /// <paramref name="now"/>). Those at or after <paramref name="now"/> go to <paramref name="held"/>,
    /// or back to <paramref name="records"/> when it is the held list itself.
    /// </summary>
    private static List<T> Take<T>(
        List<T> records, Func<T, long> mills, DateTime from, DateTime now, List<T>? held = null)
    {
        var fromMills = new DateTimeOffset(from).ToUnixTimeMilliseconds();
        var nowMills = new DateTimeOffset(now).ToUnixTimeMilliseconds();

        var due = records.Where(r => mills(r) >= fromMills && mills(r) < nowMills).ToList();
        var later = records.Where(r => mills(r) >= nowMills).ToList();
        if (held is null)
        {
            records.Clear();
            records.AddRange(later);
        }
        else
        {
            held.AddRange(later);
        }

        return due;
    }

    private static IEnumerable<Treatment> DeviceChanges(SeedingClock clock, string dataSource, DateTime from, DateTime now)
    {
        var days = (int)Math.Ceiling((now - from).TotalDays) + 1;
        return DemoDeviceLifecycle.GenerateSchedule(clock.Demo.ToLocal(now), days, clock.Demo)
            .Where(change => change.TimestampUtc >= from && change.TimestampUtc < now)
            .Select(change => DemoDeviceLifecycle.ToTreatment(change, dataSource));
    }

    /// <summary>
    /// Heart rate, steps and sleep for [<paramref name="from"/>, <paramref name="now"/>). Each carries a
    /// deterministic sync id, so a record the seed or an earlier advance wrote is updated in place.
    /// </summary>
    private async Task WriteLifestyleAsync(
        SeedingClock clock, string dataSource, DateTime from, DateTime now, CancellationToken ct)
    {
        var heartRates = new List<HeartRate>();
        var stepCounts = new List<StepCount>();
        var lastDay = clock.Demo.ToLocal(now).Date;
        for (var day = clock.Demo.ToLocal(from).Date; day <= lastDay; day = day.AddDays(1))
        {
            var (dayHeartRates, daySteps) = DemoHealthDataGenerator.GenerateDailyActivity(day, dataSource, clock.Demo);
            heartRates.AddRange(dayHeartRates.Where(h => h.Timestamp >= from && h.Timestamp < now));
            stepCounts.AddRange(daySteps.Where(s => s.Timestamp >= from && s.Timestamp < now));

            var sleep = DemoHealthDataGenerator.GenerateSleepSession(day, dataSource, clock.Demo);
            if (sleep.EndTime >= from && sleep.EndTime < now)
                await _sleep.UpsertSessionAsync(sleep, ct);
        }

        foreach (var batch in heartRates.Chunk(BatchSize))
            await _heartRates.CreateHeartRatesAsync(batch, ct);
        foreach (var batch in stepCounts.Chunk(BatchSize))
            await _stepCounts.CreateStepCountsAsync(batch, ct);
    }
}
