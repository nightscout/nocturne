using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Connectors.MyLife.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Services;

/// <summary>
/// What a <see cref="SyncRequest"/>'s window means to the MyLife connector. MyLife streams the
/// source a calendar month at a time and filters each month by a per-family bound, so both the
/// fetched window and the family bounds have to answer the request.
/// </summary>
public class MyLifeConnectorServiceSyncWindowTests
{
    private static readonly DateTime GlucoseWatermark = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TreatmentWatermark = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Overlap the shared catch-up calculation subtracts to absorb clock drift.</summary>
    private static readonly TimeSpan CatchUpOverlap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A background cycle carries the glucose watermark as its <c>from</c>. Treatments that fell
    /// behind — one cycle's publish failed while glucose succeeded — sit below that bound, and
    /// answering the run from it strands them there: every later cycle carries an even newer
    /// glucose watermark, so the gap is never crawled again.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTreatmentsFellBehindGlucose_ResumesFromTheTreatmentWatermark()
    {
        var strandedBolus = new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc);
        var fixture = new Fixture(BolusAt(strandedBolus));

        var result = await fixture.RunAsync(new SyncRequest
        {
            From = GlucoseWatermark,
            To = null,
            DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
        });

        result.Success.Should().BeTrue();
        fixture.Sync.Since.Should().Be(TreatmentWatermark - CatchUpOverlap,
            "the month stream has to reach back to the family that fell behind");
        fixture.Boluses.Should().ContainSingle().Which.Timestamp.Should().Be(strandedBolus,
            "a treatment below the glucose bound is exactly the record the run owes the tenant");
    }

    /// <summary>
    /// A caller's lower bound is never narrowed by a family's resume point: an explicit <c>from</c>
    /// with no <c>to</c> is the shape an admin repairing a months-old gap sends, and answering it
    /// from the watermark fetches nothing and reports the run as a success with a zero count.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenGivenALowerBoundBelowTheResumePoint_HonoursTheCallersBound()
    {
        var askedFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var repairedBolus = new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);
        var fixture = new Fixture(BolusAt(repairedBolus));

        var result = await fixture.RunAsync(new SyncRequest
        {
            From = askedFrom,
            To = null,
            DataTypes = [SyncDataType.Boluses],
        });

        result.Success.Should().BeTrue();
        fixture.Sync.Since.Should().Be(askedFrom, "the caller asked for this lower bound");
        fixture.Boluses.Should().ContainSingle().Which.Timestamp.Should().Be(repairedBolus);
    }

    /// <summary>
    /// An explicit range is answered as asked, resume points and all: it is the shape a manual
    /// re-import of one window sends, and a bound widened back to a watermark below it re-streams
    /// every month in between.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenGivenAnExplicitRange_AsksForItAsGiven()
    {
        var askedFrom = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var askedTo = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        var fixture = new Fixture();

        await fixture.RunAsync(new SyncRequest
        {
            From = askedFrom,
            To = askedTo,
            DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
        });

        fixture.Sync.Since.Should().Be(askedFrom);
        fixture.Sync.Until.Should().Be(askedTo, "the caller bounded the range at both ends");
    }

    /// <summary>
    /// A caller supplying no lower bound is not asking for everything: the tenant's own sync button
    /// sends that shape, and each family still stands on its own resume point.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheCallerSuppliesNoLowerBound_ResumesFromTheEarliestWatermark()
    {
        var fixture = new Fixture();

        await fixture.RunAsync(new SyncRequest
        {
            From = null,
            To = null,
            DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
        });

        fixture.Sync.Since.Should().Be(TreatmentWatermark - CatchUpOverlap);
    }

    /// <summary>
    /// A range naming no lower bound is the reset-cursor shape, and it asks for everything the
    /// source still holds — which for MyLife is its initial-sync floor. Answering it from the
    /// resume point resets nothing: the resume point is what it is asking to reset.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAnExplicitRangeNamesNoLowerBound_CrawlsFromTheHistoryFloor()
    {
        // Both watermarks sit well inside the floor, so resuming from either is a different answer.
        var fixture = new Fixture(
            glucoseWatermark: DateTime.UtcNow.AddDays(-3),
            treatmentWatermark: DateTime.UtcNow.AddDays(-5));

        await fixture.RunAsync(new SyncRequest
        {
            From = null,
            To = DateTime.UtcNow,
            DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
        });

        fixture.Sync.Since.Should().BeCloseTo(DateTime.UtcNow.AddMonths(-6), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// With no upper bound the range ends at the clock instant, and with no lower bound it reaches
    /// back from that same instant to the history floor.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheCallerNamesNoUpperBound_EndsAtTheClockInstant()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var fixture = new Fixture(
            GlucoseWatermark,
            TreatmentWatermark,
            clock);

        await fixture.RunAsync(new SyncRequest
        {
            From = null,
            To = null,
            DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
        });

        fixture.Sync.Until.Should().Be(clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task SyncDataAsync_WhenAnExplicitRangeNamesNoLowerBound_CrawlsFromSixMonthsBeforeTheClockInstant()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var fixture = new Fixture(
            clock.GetUtcNow().UtcDateTime.AddDays(-3),
            clock.GetUtcNow().UtcDateTime.AddDays(-5),
            clock);

        await fixture.RunAsync(new SyncRequest
        {
            From = null,
            To = clock.GetUtcNow().UtcDateTime,
            DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
        });

        fixture.Sync.Since.Should().Be(clock.GetUtcNow().UtcDateTime.AddMonths(-6));
    }

    /// <summary>
    /// MyLife gates its temp basals as state spans, so the sync card counts them there and a tenant
    /// who switched state spans off gets none published.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_CountsTempBasalsUnderStateSpans()
    {
        var fixture = new Fixture(TempBasalAt(new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc)));

        var result = await fixture.RunAsync(new SyncRequest
        {
            From = TreatmentWatermark,
            DataTypes = [SyncDataType.StateSpans],
        });

        fixture.TempBasals.Should().ContainSingle();
        result.ItemsSynced.Should().Equal(new Dictionary<SyncDataType, int> { [SyncDataType.StateSpans] = 1 });
    }

    [Fact]
    public async Task SyncDataAsync_WithStateSpansSwitchedOff_PublishesNoTempBasals()
    {
        var fixture = new Fixture(TempBasalAt(new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc)));

        var result = await fixture.RunAsync(new SyncRequest
        {
            From = TreatmentWatermark,
            DataTypes = [SyncDataType.TempBasals],
        });

        fixture.TempBasals.Should().BeEmpty();
        result.ItemsSynced.Should().NotContainKey(SyncDataType.TempBasals);
    }

    /// <summary>
    /// A scheduled run streams from the family furthest behind. Once the first run has published
    /// the stranded bolus, the second streams only from it, less the catch-up overlap.
    /// </summary>
    [Fact]
    public async Task ScheduledSyncTwice_SecondRunStreamsOnlyFromTheFirstRunsNewestTreatment()
    {
        var strandedBolus = new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc);
        var fixture = new Fixture(BolusAt(strandedBolus));

        (await fixture.RunScheduledAsync()).Success.Should().BeTrue();
        var firstSince = fixture.Sync.Since;
        (await fixture.RunScheduledAsync()).Success.Should().BeTrue();

        firstSince.Should().Be(TreatmentWatermark - CatchUpOverlap);
        fixture.Sync.Since.Should().Be(strandedBolus - CatchUpOverlap);
    }

    /// <summary>
    /// On a scheduled run each family filters the one month stream at its own resume point. The
    /// stream starts at the earlier of the two, so glucose behind treatments widens it, but a
    /// treatment between the two points is older than the newest stored treatment and is not
    /// published again.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_WhenGlucoseIsBehindTreatments_FiltersTreatmentsAtTheirOwnPoint()
    {
        var glucoseWatermark = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var treatmentWatermark = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var betweenThePoints = new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc);
        var afterBoth = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var fixture = new Fixture(
            glucoseWatermark, treatmentWatermark, null, null, BolusAt(betweenThePoints), BolusAt(afterBoth));

        (await fixture.RunScheduledAsync()).Success.Should().BeTrue();

        fixture.Sync.Since.Should().Be(glucoseWatermark - CatchUpOverlap,
            "the stream still reaches back to the family furthest behind");
        fixture.Boluses.Should().ContainSingle().Which.Timestamp.Should().Be(afterBoth);
    }

    [Fact]
    public async Task SyncDataAsync_SecondSyncOfTheSameEvents_StoresNoNewRow()
    {
        var at = new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc);
        var store = new StoringConnectorPublisher();
        var fixture = new Fixture(
            GlucoseWatermark, TreatmentWatermark, null, store,
            new MyLifeEvent
            {
                EventTypeId = MyLifeEventType.Glucose,
                EventDateTime = new DateTimeOffset(at).ToUnixTimeMilliseconds() * 10_000,
                Value = "120",
                PatientId = "patient-1",
                DeviceId = "device-1",
            },
            BolusAt(at.AddMinutes(10)),
            new MyLifeEvent
            {
                EventTypeId = MyLifeEventType.NeedlePriming,
                EventDateTime = new DateTimeOffset(at.AddMinutes(20)).ToUnixTimeMilliseconds() * 10_000,
                PatientId = "patient-1",
                DeviceId = "device-1",
            },
            TempBasalAt(at.AddMinutes(30)),
            EventAt(MyLifeEventType.CarbCorrection, at.AddMinutes(40), "30"),
            EventAt(MyLifeEventType.ManualGlucose, at.AddMinutes(50), "95"),
            EventAt(MyLifeEventType.Alert, at.AddMinutes(60), info: "Low reservoir"),
            EventAt(
                MyLifeEventType.BolusNormal, at.AddMinutes(70), "3",
                "{\"AmountOfBolus\":3,\"BolusIsCalculated\":true,\"CalcCarbs\":40,\"CalcBG\":150,"
                + "\"CalcIOB\":0.5,\"SuggestedMealBolus\":3}"));
        fixture.Readouts.Add(new MyLifePumpSettingsReadout
        {
            Id = "readout-1",
            DeviceSerialNumber = "SN-1",
            ActiveBasalProgramName = "Day",
            UploadDateTime = new DateTimeOffset(at).ToUnixTimeMilliseconds() * 10_000,
            BasalPrograms =
            [
                new MyLifeBasalProgram
                {
                    BasalProgramName = "Day",
                    BasalProgramEntries = [new MyLifeBasalProgramEntry { Hour = 0, Minute = 0, BasalRateUnitsPerHour = "0.8" }],
                },
            ],
        });
        var request = new SyncRequest
        {
            From = TreatmentWatermark,
            DataTypes =
            [
                SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.DeviceEvents, SyncDataType.StateSpans,
                SyncDataType.CarbIntake, SyncDataType.ManualBG, SyncDataType.BolusCalculations,
                SyncDataType.Notes, SyncDataType.Profiles,
            ],
        };

        (await fixture.RunAsync(request)).Success.Should().BeTrue();
        var stored = store.Stored.Keys.ToList();
        var offered = store.Offered;
        (await fixture.RunAsync(request)).Success.Should().BeTrue();

        stored.Select(k => k.Type).Distinct().Should().Contain(
            [
                nameof(SensorGlucose), nameof(Bolus), nameof(DeviceEvent), nameof(TempBasal), nameof(CarbIntake),
                nameof(BGCheck), nameof(BolusCalculation), nameof(Note), nameof(ProfileActivation),
            ]);
        store.Offered.Should().BeEquivalentTo(offered.ToDictionary(o => o.Key, o => o.Value * 2));
        store.Unkeyed.Should().BeEmpty();
        store.Stored.Keys.Should().BeEquivalentTo(stored);
    }

    private static MyLifeEvent TempBasalAt(DateTime at) => new()
    {
        EventTypeId = MyLifeEventType.TempBasal,
        EventDateTime = new DateTimeOffset(at).ToUnixTimeMilliseconds() * 10_000,
        InformationFromDevice = "{\"ValueInUperH\": 1.5, \"Minutes\": 30}",
        PatientId = "patient-1",
        DeviceId = "device-1",
    };

    private static MyLifeEvent EventAt(MyLifeEventType type, DateTime at, string? value = null, string? info = null) => new()
    {
        EventTypeId = type,
        EventDateTime = new DateTimeOffset(at).ToUnixTimeMilliseconds() * 10_000,
        InformationFromDevice = info,
        Value = value,
        PatientId = "patient-1",
        DeviceId = "device-1",
    };

    private static MyLifeEvent BolusAt(DateTime at) => new()
    {
        EventTypeId = MyLifeEventType.BolusNormal,
        EventDateTime = new DateTimeOffset(at).ToUnixTimeMilliseconds() * 10_000,
        InformationFromDevice = "{\"AmountOfBolus\":2.5}",
        Value = "2.5",
        PatientId = "patient-1",
        DeviceId = "device-1",
    };

    /// <summary>
    /// A MyLife sync whose month stream is stubbed: it records the window it was asked for and
    /// serves the events under test whatever that window is, so the bounds the connector applies
    /// to each family are what the assertions read.
    /// </summary>
    private sealed class Fixture
    {
        private readonly MyLifeConnectorService _service;

        public RecordingSyncService Sync { get; private set; } = null!;

        public List<Bolus> Boluses { get; } = [];

        public List<MyLifePumpSettingsReadout> Readouts { get; } = [];

        public List<TempBasal> TempBasals { get; } = [];

        public Fixture(params MyLifeEvent[] events)
            : this(GlucoseWatermark, TreatmentWatermark, null, null, events)
        {
        }

        public Fixture(
            DateTime glucoseWatermark,
            DateTime treatmentWatermark,
            TimeProvider? clock = null,
            IConnectorPublisher? store = null,
            params MyLifeEvent[] events)
        {
            var treatments = new Mock<ITreatmentPublisher>();
            treatments
                .Setup(p => p.GetLatestTreatmentTimestampAsync(
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Boluses.Count > 0 ? Boluses.Max(b => b.Timestamp) : treatmentWatermark);
            treatments
                .Setup(p => p.PublishBolusesAsync(
                    It.IsAny<IEnumerable<Bolus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Bolus>, string, WriteOrigin, CancellationToken>(
                    (records, _, _, _) => Boluses.AddRange(records))
                .ReturnsAsync(true);
            treatments
                .Setup(p => p.PublishTempBasalsAsync(
                    It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<TempBasal>, string, WriteOrigin, CancellationToken>(
                    (records, _, _, _) => TempBasals.AddRange(records))
                .ReturnsAsync(true);

            var glucose = new Mock<IGlucosePublisher>();
            glucose
                .Setup(p => p.GetLatestEntryTimestampAsync(
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(glucoseWatermark);

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Glucose).Returns(glucose.Object);
            publisher.Setup(p => p.Treatments).Returns(treatments.Object);
            publisher.Setup(p => p.Device).Returns(Mock.Of<IDevicePublisher>());
            publisher.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());

            var http = new HttpClient(new SoapStubHandler(loginSucceeds: true));
            (_service, _, _) = MyLifeSyncHarness.BuildService(
                http,
                Guid.NewGuid(),
                soapClient => Sync = new RecordingSyncService(soapClient, events, Readouts),
                store ?? publisher.Object,
                clock);
        }

        private static MyLifeConnectorConfiguration Config() => new()
        {
            Username = "user@example.com",
            Password = "secret",
            PatientId = "patient-1",
        };

        public Task<SyncResult> RunAsync(SyncRequest request) =>
            _service.SyncDataAsync(request, Config(), CancellationToken.None);

        public Task<SyncResult> RunScheduledAsync() => _service.SyncDataAsync(Config(), CancellationToken.None);
    }

    private sealed class RecordingSyncService(
        MyLifeSoapClient soapClient, IReadOnlyList<MyLifeEvent> events,
        IReadOnlyList<MyLifePumpSettingsReadout> readouts)
        : MyLifeSyncService(soapClient, NullLogger<MyLifeSyncService>.Instance)
    {
        public override Task<IReadOnlyList<MyLifePumpSettingsReadout>> FetchPumpSettingsAsync(
            string serviceUrl, string authToken, string patientId, CancellationToken cancellationToken)
            => Task.FromResult(readouts);

        public DateTime Since { get; private set; }
        public DateTime Until { get; private set; }

        public override async IAsyncEnumerable<MyLifeMonthBatch> FetchEventsPerMonthAsync(
            string serviceUrl, string authToken, string patientId, DateTime since, DateTime until,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Since = since;
            Until = until;
            await Task.CompletedTask;
            yield return new MyLifeMonthBatch("2026-01", [.. events]);
        }
    }
}
