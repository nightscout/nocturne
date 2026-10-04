using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// The v3 entries <c>history/{lastModified}</c> read pages on <c>srvModified</c> (the server write
/// stamp), as Nightscout's <c>lib/api3/generic/history</c> does, not on the reading's clinical time:
/// a reading edited or backfilled after the client's cursor must reach it however old it is.
/// </summary>
[Trait("Category", "Unit")]
public class EntryReadServiceHistoryTests
{
    private static readonly DateTime Cursor = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long CursorMills = Mills(Cursor);

    private readonly Mock<ISensorGlucoseRepository> _sgRepo = new();
    private readonly Mock<IMeterGlucoseRepository> _mgRepo = new();
    private readonly Mock<ICalibrationRepository> _calRepo = new();
    private readonly Mock<IDemoModeService> _demoMode = new();

    public EntryReadServiceHistoryTests()
    {
        _demoMode.Setup(d => d.IsEnabled).Returns(false);
        _sgRepo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mgRepo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _calRepo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    [Fact]
    public async Task ReadingsOlderThanTheCursor_ButWrittenAfterIt_AreDelivered_OldestWriteFirst()
    {
        var backfilled = Sg(Cursor.AddDays(-2), written: Cursor.AddMinutes(1), source: "cgm");
        var edited = Mg(Cursor.AddHours(-6), written: Cursor.AddMinutes(2));
        _sgRepo.Setup(r => r.GetModifiedSinceAsync(CursorMills, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([backfilled]);
        _mgRepo.Setup(r => r.GetModifiedSinceAsync(CursorMills, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([edited]);

        var page = await CreateSut(TestDoubles.CanonicalGlucosePassThrough.Create())
            .GetModifiedSinceAsync(CursorMills, 1000);

        page.Records.Select(e => (e.Type, e.Mills)).Should().Equal(
            ("sgv", Mills(backfilled.Timestamp)),
            ("mbg", Mills(edited.Timestamp)));
        page.Records.Select(e => e.SrvModified).Should().Equal(Mills(backfilled.ModifiedAt), Mills(edited.ModifiedAt));
        page.CursorMills.Should().Be(Mills(edited.ModifiedAt));
    }

    [Fact]
    public async Task NothingWrittenAfterTheCursor_ReturnsAnEmptyPageWithoutACursor()
    {
        var page = await CreateSut(TestDoubles.CanonicalGlucosePassThrough.Create())
            .GetModifiedSinceAsync(CursorMills, 1000);

        page.Records.Should().BeEmpty();
        page.CursorMills.Should().BeNull();
    }

    [Fact]
    public async Task LateBackfillFromALosingStream_IsWithheld_ButAdvancesTheCursor()
    {
        // With no registered devices, pseudo-streams rank by key, so "a-cgm" wins every bucket it
        // reported into. Its reading was delivered on an earlier page; only the losing stream's
        // backfill into the same bucket was written after the cursor.
        var at = Cursor.AddHours(-3);
        var winner = Sg(at, written: Cursor.AddHours(-3), source: "a-cgm");
        var loser = Sg(at.AddMinutes(1), written: Cursor.AddMinutes(5), source: "b-cgm");

        _sgRepo.Setup(r => r.GetModifiedSinceAsync(CursorMills, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([loser]);
        _sgRepo.Setup(r => r.GetForEntriesAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, bool _,
                DateTime? _, Guid? _, CancellationToken _, Guid? _) =>
                new[] { winner, loser }.Where(r => r.Timestamp >= from && r.Timestamp <= to).ToList());

        var devices = new Mock<IPatientDeviceRepository>();
        devices.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var canonical = new CanonicalGlucoseService(_sgRepo.Object, devices.Object, _demoMode.Object);

        var page = await CreateSut(canonical).GetModifiedSinceAsync(CursorMills, 1000);

        page.Records.Should().BeEmpty();
        page.CursorMills.Should().Be(Mills(loser.ModifiedAt));
    }

    [Fact]
    public async Task FullPageEntirelyWithheld_IsSkipped_ForTheNextPage_AndTheCursorAdvances()
    {
        var demo1 = Sg(Cursor.AddDays(-1), written: Cursor.AddMinutes(1), source: DataSources.DemoService);
        var demo2 = Sg(Cursor.AddDays(-1).AddMinutes(5), written: Cursor.AddMinutes(2), source: DataSources.DemoService);
        var real = Mg(Cursor.AddHours(-1), written: Cursor.AddMinutes(3));
        var cursors = new List<long>();
        ServeHistory(_sgRepo, new[] { demo1, demo2 }, cursors);
        ServeHistory(_mgRepo, new[] { real });

        var page = await CreateSut(TestDoubles.CanonicalGlucosePassThrough.Create())
            .GetModifiedSinceAsync(CursorMills, 2);

        page.Records.Select(e => e.SrvModified).Should().Equal(Mills(real.ModifiedAt));
        page.CursorMills.Should().Be(Mills(real.ModifiedAt));
        cursors.Should().Equal(CursorMills, Mills(demo2.ModifiedAt));
    }

    [Fact]
    public async Task TwoTypesFillingTheirLimit_AcrossSharedMilliseconds_PageThroughEveryRowExactlyOnce()
    {
        DateTime At(int ms) => Cursor.AddMilliseconds(ms);
        var meter = new[] { 1, 2, 2, 4, 5, 5, 5, 7 }
            .Select((ms, i) => Mg(Cursor.AddHours(-i), written: At(ms))).ToList();
        var calibrations = new[] { 2, 2, 3, 3, 5, 6, 7, 7 }
            .Select((ms, i) => Cal(Cursor.AddHours(-i), written: At(ms))).ToList();
        ServeHistory(_mgRepo, meter);
        ServeHistory(_calRepo, calibrations);
        var sut = CreateSut(TestDoubles.CanonicalGlucosePassThrough.Create());

        var delivered = new List<string>();
        var pageCursors = new List<long>();
        var cursor = CursorMills;
        for (var guard = 0; guard < 50; guard++)
        {
            var page = await sut.GetModifiedSinceAsync(cursor, 3);
            if (page.CursorMills is null)
                break;

            delivered.AddRange(page.Records.Select(e => e.Id!));
            pageCursors.Add(page.CursorMills.Value);
            cursor = page.CursorMills.Value;
        }

        var expected = meter.Select(r => r.Id.ToString()).Concat(calibrations.Select(r => r.Id.ToString()));
        delivered.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(expected);
        pageCursors.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Serves <paramref name="rows"/> the way <c>HistoryPage</c> pages them: written after the
    /// cursor's millisecond, oldest first, <c>limit</c> rows extended to the end of the last row's
    /// millisecond.
    /// </summary>
    private static void ServeHistory<TRecord, TRepo>(
        Mock<TRepo> repo, IReadOnlyList<TRecord> rows, List<long>? cursors = null)
        where TRecord : class, IV4Record
        where TRepo : class, ILegacyKeyedRepository<TRecord>
    {
        repo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long cursor, int limit, CancellationToken _) =>
            {
                cursors?.Add(cursor);
                var after = rows.Where(r => Mills(r.ModifiedAt) > cursor)
                    .OrderBy(r => r.ModifiedAt).ThenBy(r => r.Id).ToList();
                if (after.Count <= limit)
                    return after;

                var lastMills = Mills(after[limit - 1].ModifiedAt);
                return after.Where((r, i) => i < limit || Mills(r.ModifiedAt) == lastMills).ToList();
            });
    }

    private EntryReadService CreateSut(Core.Contracts.Glucose.ICanonicalGlucoseService canonical) =>
        new(_sgRepo.Object, _mgRepo.Object, _calRepo.Object, canonical, _demoMode.Object,
            NullLogger<EntryReadService>.Instance);

    private static long Mills(DateTime value) => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static SensorGlucose Sg(DateTime at, DateTime written, string source) => new()
    {
        Id = Guid.CreateVersion7(),
        Timestamp = at,
        Mgdl = 120,
        Device = "sensor",
        DataSource = source,
        CreatedAt = written,
        ModifiedAt = written,
    };

    private static Calibration Cal(DateTime at, DateTime written) => new()
    {
        Id = Guid.CreateVersion7(),
        Timestamp = at,
        Device = "sensor",
        CreatedAt = written,
        ModifiedAt = written,
    };

    private static MeterGlucose Mg(DateTime at, DateTime written) => new()
    {
        Id = Guid.CreateVersion7(),
        Timestamp = at,
        Mgdl = 140,
        Device = "meter",
        CreatedAt = written,
        ModifiedAt = written,
    };
}
