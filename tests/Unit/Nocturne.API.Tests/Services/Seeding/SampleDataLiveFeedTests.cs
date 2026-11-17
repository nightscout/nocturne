using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

public class SampleDataLiveFeedTests : IDisposable
{
    private const string Zone = "Australia/Sydney";
    private const string Source = SampleDataSeeder.DevSampleDataSource;

    private static readonly DateTimeOffset SeedEnd = new(2026, 10, 20, 3, 41, 12, TimeSpan.Zero);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly NocturneDbContext _db;
    private readonly FakeTimeProvider _time = new(SeedEnd);

    private readonly List<(Entry Entry, WriteOrigin Origin)> _entries = [];
    private readonly List<(Treatment Treatment, WriteOrigin Origin)> _treatments = [];
    private readonly List<DeviceStatus> _statuses = [];
    private readonly List<HeartRate> _heartRates = [];
    private readonly List<SleepSession> _sleep = [];
    private int _stamps;

    public SampleDataLiveFeedTests()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new NocturneDbContext(options) { TenantId = _tenantId };
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private SampleDataTimeline Seeded => new(
        new DateTime(2026, 10, 17), SeedEnd.UtcDateTime, SeedEnd.UtcDateTime, Zone, Source);

    private async Task StoreAsync(SampleDataTimeline timeline)
    {
        await using var db = await Factory.CreateAsync();
        await SampleDataTimeline.WriteAsync(db, timeline, _time.GetUtcNow(), CancellationToken.None);
    }

    private async Task<SampleDataTimeline?> StoredAsync()
    {
        await using var db = await Factory.CreateAsync();
        return await SampleDataTimeline.ReadAsync(db, CancellationToken.None);
    }

    private TestNocturneDbContextFactory Factory => new(_db);

    private SampleDataLiveFeed Feed()
    {
        var entries = new Mock<IEntryService>();
        entries.Setup(s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Entry>, WriteOrigin, CancellationToken>((batch, origin, _) =>
                _entries.AddRange(batch.Select(e => (e, origin))));

        var treatments = new Mock<ITreatmentService>();
        treatments.Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Treatment>, WriteOrigin, CancellationToken>((batch, origin, _) =>
                _treatments.AddRange(batch.Select(t => (t, origin))));

        var statuses = new Mock<IDeviceStatusService>();
        statuses.Setup(s => s.CreateDeviceStatusesAsync(
                It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<DeviceStatus>, string?, WriteOrigin, CancellationToken>((batch, _, _, _) =>
                _statuses.AddRange(batch));

        var heartRates = new Mock<IHeartRateService>();
        heartRates.Setup(s => s.CreateHeartRatesAsync(It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<HeartRate>, CancellationToken>((batch, _) => _heartRates.AddRange(batch));

        var sleep = new Mock<ISleepService>();
        sleep.Setup(s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .Callback<SleepSession, CancellationToken>((session, _) => _sleep.Add(session))
            .ReturnsAsync((SleepSession session, CancellationToken _) => session);

        var tempBasals = new Mock<ITempBasalRepository>();
        tempBasals.Setup(r => r.StampScheduledRatesAsync(It.IsAny<string>(), It.IsAny<Func<DateTime, double>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _stamps++)
            .ReturnsAsync(0);

        return new SampleDataLiveFeed(
            MockTenantAccessor.Create(_tenantId).Object,
            Factory,
            entries.Object,
            treatments.Object,
            statuses.Object,
            heartRates.Object,
            Mock.Of<IStepCountService>(),
            sleep.Object,
            tempBasals.Object,
            _time,
            NullLoggerFactory.Instance,
            NullLogger<SampleDataLiveFeed>.Instance);
    }

    private static long Mills(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    [Fact]
    public async Task A_tenant_with_no_timeline_is_left_alone()
    {
        _time.Advance(TimeSpan.FromMinutes(20));

        var cursor = await Feed().AdvanceAsync(null, CancellationToken.None);

        cursor.Should().BeNull();
        _entries.Should().BeEmpty();
        _treatments.Should().BeEmpty();
    }

    [Fact]
    public async Task An_advance_writes_what_the_timeline_generated_since_the_seed_and_moves_the_mark()
    {
        await StoreAsync(Seeded);
        _time.Advance(TimeSpan.FromMinutes(22));
        var now = _time.GetUtcNow();

        var cursor = await Feed().AdvanceAsync(null, CancellationToken.None);

        cursor.Should().NotBeNull();
        _entries.Should().NotBeEmpty();
        _entries.Should().OnlyContain(e => e.Entry.Mills >= Mills(SeedEnd) && e.Entry.Mills < Mills(now));
        _entries.Should().OnlyContain(e => e.Origin == WriteOrigin.Live && e.Entry.DataSource == Source);
        _entries.Select(e => e.Entry.Id).Should().OnlyHaveUniqueItems().And.NotContainNulls();
        _statuses.Should().NotBeEmpty().And.OnlyContain(s => s.Mills >= Mills(SeedEnd) && s.Mills < Mills(now));
        (await StoredAsync())!.WrittenThrough.Should().Be(now.UtcDateTime);
    }

    [Fact]
    public async Task The_readings_are_the_ones_the_continued_simulation_generates()
    {
        await StoreAsync(Seeded);
        _time.Advance(TimeSpan.FromMinutes(40));
        var now = _time.GetUtcNow();

        await Feed().AdvanceAsync(null, CancellationToken.None);

        var expected = new DemoDataGenerator(
                Options.Create(new DemoModeConfiguration { BackfillDays = 1, RandomSeed = 1 }),
                new DemoClock(_time, TimeZoneInfo.FindSystemTimeZoneById(Zone)),
                NullLogger<DemoDataGenerator>.Instance,
                NullLoggerFactory.Instance)
            .ContinueTimeline(Seeded.FirstDay, SeedEnd.UtcDateTime)
            .TakeWhile(s => s.Utc < now.UtcDateTime)
            .SelectMany(DemoCoverageSeeds.CgmReadings)
            // A step before the seed's end can carry a comparison reading after it.
            .Where(e => e.Mills >= Mills(SeedEnd) && e.Mills < Mills(now))
            .Select(e => (e.Mills, e.Sgv))
            .OrderBy(e => e.Mills)
            .ToList();

        expected.Should().NotBeEmpty();
        _entries.Where(e => e.Entry.Type == "sgv")
            .Select(e => (e.Entry.Mills, e.Entry.Sgv))
            .OrderBy(e => e.Mills)
            .Should().Equal(expected);
    }

    [Fact]
    public async Task Advancing_in_ticks_writes_what_one_advance_over_the_whole_span_writes()
    {
        await StoreAsync(Seeded);
        SampleDataLiveCursor? cursor = null;
        for (var tick = 0; tick < 12; tick++)
        {
            _time.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(7));
            cursor = await Feed().AdvanceAsync(cursor, CancellationToken.None);
        }
        var ticked = Snapshot();

        Clear();
        await StoreAsync(Seeded);
        await Feed().AdvanceAsync(null, CancellationToken.None);

        ticked.Should().BeEquivalentTo(Snapshot());
    }

    [Fact]
    public async Task A_restart_continues_from_the_stored_mark_without_rewriting()
    {
        await StoreAsync(Seeded);
        _time.Advance(TimeSpan.FromMinutes(12));
        await Feed().AdvanceAsync(null, CancellationToken.None);
        var first = Snapshot();

        Clear();
        _time.Advance(TimeSpan.FromMinutes(12));
        await Feed().AdvanceAsync(null, CancellationToken.None);

        Snapshot().Intersect(first).Should().BeEmpty();
        _entries.Should().OnlyContain(e => e.Entry.Mills >= Mills(SeedEnd.AddMinutes(12)));
    }

    [Fact]
    public async Task A_gap_longer_than_half_an_hour_is_written_as_a_backfill()
    {
        await StoreAsync(Seeded);
        _time.Advance(TimeSpan.FromHours(3));

        await Feed().AdvanceAsync(null, CancellationToken.None);

        _entries.Should().NotBeEmpty().And.OnlyContain(e => e.Origin == WriteOrigin.Backfill);
        _treatments.Should().NotBeEmpty().And.OnlyContain(t => t.Origin == WriteOrigin.Backfill);
        _stamps.Should().Be(1);
    }

    [Fact]
    public async Task A_day_of_catch_up_writes_the_heart_rate_and_the_night_of_sleep_it_spans()
    {
        await StoreAsync(Seeded);
        _time.Advance(TimeSpan.FromDays(1));
        var now = _time.GetUtcNow().UtcDateTime;

        await Feed().AdvanceAsync(null, CancellationToken.None);

        _heartRates.Should().NotBeEmpty()
            .And.OnlyContain(h => h.Timestamp >= SeedEnd.UtcDateTime && h.Timestamp < now);
        _sleep.Should().ContainSingle().Which.EndTime.Should().BeBefore(now);
    }

    [Fact]
    public async Task A_seed_in_progress_holds_the_feed_off_its_tenant()
    {
        await StoreAsync(Seeded);
        _time.Advance(TimeSpan.FromMinutes(20));
        var gate = SampleDataTimeline.GateFor(_tenantId);
        await gate.WaitAsync();
        try
        {
            await Feed().AdvanceAsync(null, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }

        _entries.Should().BeEmpty();
        (await StoredAsync())!.WrittenThrough.Should().Be(SeedEnd.UtcDateTime);
    }

    [Fact]
    public async Task A_new_seed_replaces_the_cursor_of_the_old_one()
    {
        await StoreAsync(Seeded);
        _time.Advance(TimeSpan.FromMinutes(10));
        var old = await Feed().AdvanceAsync(null, CancellationToken.None);

        var reseeded = Seeded with
        {
            FirstDay = new DateTime(2026, 10, 18),
            SeedEnd = _time.GetUtcNow().UtcDateTime,
            WrittenThrough = _time.GetUtcNow().UtcDateTime,
        };
        await StoreAsync(reseeded);
        _time.Advance(TimeSpan.FromMinutes(10));
        var next = await Feed().AdvanceAsync(old, CancellationToken.None);

        next.Should().NotBeSameAs(old);
        next!.Continues(reseeded).Should().BeTrue();
    }

    [Fact]
    public async Task The_timeline_round_trips_and_an_unreadable_row_reads_as_none()
    {
        await StoreAsync(Seeded);
        (await StoredAsync()).Should().Be(Seeded);

        await using (var db = await Factory.CreateAsync())
        {
            var row = await db.Settings.SingleAsync(s => s.Key == SampleDataTimeline.SettingsKey);
            row.Value = "not json";
            await db.SaveChangesAsync();
        }

        (await StoredAsync()).Should().BeNull();
    }

    [Fact]
    public void The_timeline_row_is_reserved_from_the_settings_collection()
    {
        SettingsKeys.IsReserved(SampleDataTimeline.SettingsKey).Should().BeTrue();
    }

    [Fact]
    public void The_feed_is_registered_only_when_configured()
    {
        static IConfiguration Config(string? value) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SampleDataLiveFeed.EnabledKey] = value })
            .Build();

        SampleDataLiveFeed.IsEnabled(Config(null)).Should().BeFalse();
        SampleDataLiveFeed.IsEnabled(Config("false")).Should().BeFalse();
        SampleDataLiveFeed.IsEnabled(Config("true")).Should().BeTrue();
    }

    private List<string> Snapshot() =>
    [
        .. _entries.Select(e => $"e {e.Entry.Id} {e.Entry.Mills} {e.Entry.Sgv}"),
        .. _treatments.Select(t => $"t {t.Treatment.EventType} {t.Treatment.Mills} {t.Treatment.Insulin} {t.Treatment.Carbs}"),
        .. _statuses.Select(s => $"s {s.Id} {s.Mills}"),
    ];

    private void Clear()
    {
        _entries.Clear();
        _treatments.Clear();
        _statuses.Clear();
        _heartRates.Clear();
        _sleep.Clear();
    }
}
