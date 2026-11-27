using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The reads a derived glucose trend pairs a reading with: the previous reading of its stream,
/// hidden copies included, and replay's page of the rows the canonical read hides; and the recent
/// readings of a stream the windowed alert conditions read, chosen the same way. Written through
/// the repository with the real duplicate linker, so the copies of one reading are linked as they
/// are in production: whichever copy is written first is shown.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseStreamPredecessorTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Start = new(2026, 11, 17, 2, 0, 0, DateTimeKind.Utc);
    private const string Dexcom = "dexcom-connector";
    private const string Xdrip = "xdrip";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly SensorGlucoseRepository _glucose;

    public SensorGlucoseStreamPredecessorTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _context.TenantId = TestTenantId;

        var dedupContext = _db.CreateContext();
        dedupContext.TenantId = TestTenantId;
        _glucose = new SensorGlucoseRepository(
            new TestNocturneDbContextFactory(_context),
            DeduplicationModules.Linker(dedupContext.AsFactory()),
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedDevice()
    {
        var id = Guid.NewGuid();
        _context.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = id,
            TenantId = TestTenantId,
            DeviceCategory = "CGM",
            Manufacturer = "Dexcom",
            Model = "G7",
            IsCurrent = true,
        });
        _context.SaveChanges();
        return id;
    }

    private async Task<SensorGlucose> WriteAsync(
        string? source, DateTime at, double mgdl, Guid? patientDeviceId = null)
    {
        var written = await _context.SensorGlucose.AsNoTracking()
            .Where(e => e.DataSource == source && e.Timestamp == at)
            .Select(e => e.Id)
            .ToListAsync();
        await _glucose.BulkCreateAsync(
            [new SensorGlucose { Timestamp = at, Mgdl = mgdl, DataSource = source, Device = "g7", PatientDeviceId = patientDeviceId }],
            WriteOrigin.Live);
        var id = await _context.SensorGlucose.AsNoTracking()
            .Where(e => e.DataSource == source && e.Timestamp == at && !written.Contains(e.Id))
            .Select(e => e.Id)
            .SingleAsync();
        return (await _glucose.GetByIdAsync(id))!;
    }

    private async Task<bool> IsDuplicateAsync(SensorGlucose reading) =>
        await _context.SensorGlucose.AsNoTracking().Where(e => e.Id == reading.Id).Select(e => e.IsDuplicate).SingleAsync();

    /// <summary>
    /// Two uploaders of one CGM, each a pseudo-device stream, every reading at minute
    /// <c>5i - 1</c> with a dexcom copy on the minute and an xdrip copy three seconds later. The
    /// dexcom copy is written first, so shown, for even <c>i</c>; the xdrip copy for odd <c>i</c>.
    /// </summary>
    private async Task<(List<SensorGlucose> Dexcom, List<SensorGlucose> Xdrip)> TakingTurnsAsync(Guid? device = null)
    {
        var dexcom = new List<SensorGlucose>();
        var xdrip = new List<SensorGlucose>();
        for (var i = 0; i < 3; i++)
        {
            var at = Start.AddMinutes(5 * i - 1);
            var mgdl = 200 - 20 * i;
            if (i % 2 == 0)
            {
                dexcom.Add(await WriteAsync(Dexcom, at, mgdl, device));
                xdrip.Add(await WriteAsync(Xdrip, at.AddSeconds(3), mgdl, device));
            }
            else
            {
                xdrip.Add(await WriteAsync(Xdrip, at.AddSeconds(3), mgdl, device));
                dexcom.Add(await WriteAsync(Dexcom, at, mgdl, device));
            }
        }
        return (dexcom, xdrip);
    }

    [Fact]
    public async Task Two_uploaders_taking_turns_pair_with_the_hidden_copy_of_their_own_stream()
    {
        var (_, xdrip) = await TakingTurnsAsync();
        (await IsDuplicateAsync(xdrip[0])).Should().BeTrue("the dexcom copy at 01:59 was written first");
        (await IsDuplicateAsync(xdrip[1])).Should().BeFalse("the xdrip copy at 02:04:03 was written first");

        var previous = await _glucose.GetStreamPredecessorAsync(xdrip[1], xdrip[1].Timestamp.AddMinutes(-15));

        previous!.Id.Should().Be(xdrip[0].Id, "the dexcom copy at 02:04:00 is another stream");
    }

    [Fact]
    public async Task A_registered_device_skips_the_other_uploaders_copy_of_the_same_reading()
    {
        var device = SeedDevice();
        var (dexcom, xdrip) = await TakingTurnsAsync(device);
        (await IsDuplicateAsync(dexcom[1])).Should().BeTrue("the xdrip copy at 02:04:03 was written first");

        var previous = await _glucose.GetStreamPredecessorAsync(xdrip[1], xdrip[1].Timestamp.AddMinutes(-15));

        previous!.Id.Should().Be(xdrip[0].Id,
            "the dexcom copy at 02:04:00 is another uploader's; the newest earlier xdrip one is the copy at 01:59:03");
    }

    /// <summary>
    /// Two apps upload one registered CGM and the xdrip copy of each reading lands 40 seconds after
    /// the dexcom one, too far apart to link: both copies are shown, as one stream.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Another_uploaders_unlinked_copy_of_a_registered_device_is_never_returned(int xdripOffset)
    {
        var device = SeedDevice();
        await WriteAsync(Dexcom, Start.AddMinutes(-5), 120, device);
        var previous = await WriteAsync(Xdrip, Start.AddMinutes(-5).AddSeconds(40), 120 + xdripOffset, device);
        var dexcom = await WriteAsync(Dexcom, Start, 100, device);
        var reading = await WriteAsync(Xdrip, Start.AddSeconds(40), 100 + xdripOffset, device);
        (await IsDuplicateAsync(dexcom)).Should().BeFalse();
        (await IsDuplicateAsync(reading)).Should().BeFalse();

        (await _glucose.GetStreamPredecessorAsync(reading, reading.Timestamp.AddMinutes(-15)))!.Id
            .Should().Be(previous.Id, "the dexcom copy 40 seconds older is another uploader's");
    }

    /// <summary>
    /// One uploader sends a reading twice under two ids at one timestamp. Deduplication links the
    /// copies and shows the first; the second is the same reading, not the one before it.
    /// </summary>
    [Fact]
    public async Task A_copy_of_the_reading_from_its_own_uploader_is_never_returned()
    {
        var previous = await WriteAsync(Dexcom, Start.AddMinutes(-5), 110);
        var reading = await WriteAsync(Dexcom, Start, 100);
        var copy = await WriteAsync(Dexcom, Start, 100);
        (await IsDuplicateAsync(reading)).Should().BeFalse();
        (await IsDuplicateAsync(copy)).Should().BeTrue();

        (await _glucose.GetStreamPredecessorAsync(reading, Start.AddMinutes(-15)))!.Id.Should().Be(previous.Id);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "", true)]
    [InlineData("", null, true)]
    [InlineData(null, Dexcom, false)]
    [InlineData(Dexcom, null, false)]
    public async Task A_null_and_an_empty_data_source_are_one_uploader_and_match_no_other(
        string? source, string? earlierSource, bool found)
    {
        var device = SeedDevice();
        var previous = await WriteAsync(earlierSource, Start.AddMinutes(-5), 110, device);
        var reading = await WriteAsync(source, Start, 100, device);

        var result = await _glucose.GetStreamPredecessorAsync(reading, Start.AddMinutes(-15));

        (result?.Id).Should().Be(found ? previous.Id : null);
    }

    [Fact]
    public async Task The_window_starts_at_since_inclusive()
    {
        var reading = await WriteAsync(Dexcom, Start, 100);
        var edge = await WriteAsync(Dexcom, Start.AddMinutes(-15), 130);

        (await _glucose.GetStreamPredecessorAsync(reading, Start.AddMinutes(-15)))!.Id.Should().Be(edge.Id);
        (await _glucose.GetStreamPredecessorAsync(reading, Start.AddMinutes(-15).AddSeconds(1))).Should().BeNull();
    }

    [Fact]
    public async Task Another_stream_a_later_reading_and_a_reading_with_no_value_are_never_returned()
    {
        var reading = await WriteAsync(Dexcom, Start, 100);
        await WriteAsync(Xdrip, Start.AddMinutes(-1), 180);
        await WriteAsync(Dexcom, Start.AddMinutes(1), 90);
        await WriteAsync(Dexcom, Start.AddMinutes(-2), 0);
        var previous = await WriteAsync(Dexcom, Start.AddMinutes(-5), 110);

        (await _glucose.GetStreamPredecessorAsync(reading, Start.AddMinutes(-15)))!.Id.Should().Be(previous.Id);
    }

    [Fact]
    public async Task A_soft_deleted_reading_is_never_returned()
    {
        var reading = await WriteAsync(Dexcom, Start, 100);
        var older = await WriteAsync(Dexcom, Start.AddMinutes(-10), 120);
        var deleted = await WriteAsync(Dexcom, Start.AddMinutes(-5), 110);
        await _glucose.DeleteAsync(deleted.Id, WriteOrigin.Live);

        (await _glucose.GetStreamPredecessorAsync(reading, Start.AddMinutes(-15)))!.Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task The_hidden_rows_carry_the_primary_of_their_duplicate_group_and_page_by_time()
    {
        var (dexcom, xdrip) = await TakingTurnsAsync();

        var firstPage = await _glucose.GetHiddenAsync(Start.AddMinutes(-15), Start.AddMinutes(15), 2, null, null);
        var last = firstPage[^1].Reading;
        var secondPage = await _glucose.GetHiddenAsync(
            Start.AddMinutes(-15), Start.AddMinutes(15), 2, last.Timestamp, last.Id);

        firstPage.Concat(secondPage).Select(h => (h.Reading.Id, h.DuplicateOf)).Should().Equal(
            (xdrip[0].Id, dexcom[0].Id),
            (dexcom[1].Id, xdrip[1].Id),
            (xdrip[2].Id, dexcom[2].Id));
    }

    private async Task<bool> IsSecondaryStreamAsync(SensorGlucose reading) =>
        await _context.SensorGlucose.AsNoTracking().Where(e => e.Id == reading.Id).Select(e => e.IsSecondaryStream).SingleAsync();

    private async Task<IEnumerable<Guid>> SeriesOfAsync(SensorGlucose newest, DateTime since) =>
        (await _glucose.GetStreamSeriesAsync(newest, since)).Select(r => r.Reading.Id);

    /// <summary>
    /// With the primary alternating between two uploaders, a stream's recent readings are every one
    /// of its own copies, hidden or shown, oldest first, and never the other uploader's.
    /// </summary>
    [Fact]
    public async Task The_series_of_two_uploaders_taking_turns_is_each_ones_own_copies()
    {
        var (dexcom, xdrip) = await TakingTurnsAsync();
        (await IsDuplicateAsync(dexcom[1])).Should().BeTrue("the xdrip copy at 02:04:03 was written first");
        (await IsDuplicateAsync(xdrip[0])).Should().BeTrue("the dexcom copy at 01:59 was written first");

        (await SeriesOfAsync(dexcom[2], Start.AddMinutes(-180)))
            .Should().Equal(dexcom[0].Id, dexcom[1].Id, dexcom[2].Id);
        (await SeriesOfAsync(xdrip[1], Start.AddMinutes(-180)))
            .Should().Equal(xdrip[0].Id, xdrip[1].Id);
        (await _glucose.GetStreamSeriesAsync(dexcom[2], Start.AddMinutes(-180)))
            .Select(r => r.DuplicateOf).Should().Equal(null, xdrip[1].Id, null);
    }

    /// <summary>
    /// An uploader that sends one reading twice, five seconds apart, writes one duplicate group. The
    /// series read returns both copies, the later one with the earlier as its group's primary, so the
    /// reading counts once (<c>RecentReadings.Of</c>).
    /// </summary>
    [Fact]
    public async Task The_series_names_the_primary_of_a_reading_its_uploader_sent_twice()
    {
        var first = await WriteAsync(Dexcom, Start.AddMinutes(-5), 110);
        var again = await WriteAsync(Dexcom, Start.AddMinutes(-5).AddSeconds(5), 110);
        var newest = await WriteAsync(Dexcom, Start, 100);
        (await IsDuplicateAsync(again)).Should().BeTrue();

        (await _glucose.GetStreamSeriesAsync(newest, Start.AddMinutes(-180)))
            .Select(r => (r.Reading.Id, r.DuplicateOf))
            .Should().Equal((first.Id, null), (again.Id, first.Id), (newest.Id, null));
    }

    /// <summary>
    /// Two uploaders 40 seconds apart are not linked, and their pseudo-device streams compete for each
    /// bucket: xdrip's readings lose to dexcom's and are hidden, but stay in xdrip's own series.
    /// </summary>
    [Fact]
    public async Task The_series_holds_the_readings_of_buckets_its_stream_lost()
    {
        await WriteAsync(Dexcom, Start.AddMinutes(-5), 120);
        var lost = await WriteAsync(Xdrip, Start.AddMinutes(-5).AddSeconds(40), 118);
        await WriteAsync(Dexcom, Start, 100);
        var newest = await WriteAsync(Xdrip, Start.AddSeconds(40), 98);
        (await IsDuplicateAsync(lost)).Should().BeFalse();
        (await IsSecondaryStreamAsync(lost)).Should().BeTrue();

        (await SeriesOfAsync(newest, Start.AddMinutes(-180))).Should().Equal(lost.Id, newest.Id);
    }

    /// <summary>A copy in the newest reading's own duplicate group is the same reading, not an earlier one.</summary>
    [Fact]
    public async Task The_series_leaves_out_the_copies_of_the_newest_reading()
    {
        var previous = await WriteAsync(Dexcom, Start.AddMinutes(-5), 110);
        var newest = await WriteAsync(Dexcom, Start, 100);
        var copy = await WriteAsync(Dexcom, Start, 100);
        (await IsDuplicateAsync(copy)).Should().BeTrue();

        (await SeriesOfAsync(newest, Start.AddMinutes(-180))).Should().Equal(previous.Id, newest.Id);
    }

    /// <summary>
    /// The series runs from <c>since</c> up to and including the newest reading, with only the readings
    /// of its stream that carry a value and are not deleted.
    /// </summary>
    [Fact]
    public async Task The_series_runs_from_since_to_the_newest_reading_with_its_streams_valued_readings()
    {
        var newest = await WriteAsync(Dexcom, Start, 100);
        await WriteAsync(Xdrip, Start.AddMinutes(-1), 180);
        await WriteAsync(Dexcom, Start.AddMinutes(1), 90);
        await WriteAsync(Dexcom, Start.AddMinutes(-2), 0);
        var deleted = await WriteAsync(Dexcom, Start.AddMinutes(-10), 120);
        await _glucose.DeleteAsync(deleted.Id, WriteOrigin.Live);
        var edge = await WriteAsync(Dexcom, Start.AddMinutes(-180), 130);
        await WriteAsync(Dexcom, Start.AddMinutes(-180).AddSeconds(-1), 131);

        (await SeriesOfAsync(newest, Start.AddMinutes(-180))).Should().Equal(edge.Id, newest.Id);
    }
}
