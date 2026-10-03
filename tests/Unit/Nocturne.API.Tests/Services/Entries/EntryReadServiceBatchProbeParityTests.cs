using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// Covers <see cref="EntryReadService.CheckDuplicatesAsync"/> against real repositories on SQLite:
/// a v1 upload batch must cost one duplicate query per entry type, and must classify every entry
/// exactly as the per-entry <see cref="EntryReadService.CheckDuplicateAsync"/> probe it replaces.
/// One uploader re-sending its stored backlog in 1,000-entry POSTs made the per-entry probe the
/// most-executed statement in the production database (95 M calls).
/// </summary>
[Trait("Category", "Unit")]
public class EntryReadServiceBatchProbeParityTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Start = new(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly StatementRecorder _statements = new();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly EntryReadService _sut;

    public EntryReadServiceBatchProbeParityTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId, "test", _statements);
        _context = _db.CreateContext();

        var factory = new TestTenantDbContextFactory(_context);
        var audit = new Mock<IAuditContext>().Object;
        var demoMode = new Mock<IDemoModeService>();
        demoMode.Setup(d => d.IsEnabled).Returns(false);

        _sut = new EntryReadService(
            new SensorGlucoseRepository(
                factory, new Mock<IDeduplicationService>().Object, audit,
                NullLogger<SensorGlucoseRepository>.Instance),
            new MeterGlucoseRepository(factory, audit, NullLogger<MeterGlucoseRepository>.Instance),
            new CalibrationRepository(factory, audit, NullLogger<CalibrationRepository>.Instance),
            TestDoubles.CanonicalGlucosePassThrough.Create(),
            demoMode.Object,
            NullLogger<EntryReadService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ContiguousSgvBatch_IssuesOneSensorGlucoseQuery()
    {
        // A day of stored readings, and a 288-entry re-upload of exactly that day.
        for (var i = 0; i < 288; i++)
            SeedSgv(Start.AddMinutes(5 * i), 100 + (i % 60), "Dexcom G6");
        _statements.Clear();

        var probes = Enumerable.Range(0, 288)
            .Select(i => Probe("Dexcom G6", "sgv", Start.AddMinutes(5 * i)))
            .ToArray();

        var results = await _sut.CheckDuplicatesAsync(probes);

        results.Should().HaveCount(288).And.OnlyContain(r => r != null);
        _statements.SelectsAgainst("sensor_glucose").Should().Be(1);
    }

    [Fact]
    public async Task MixedBatch_ClassifiesEveryEntryAsThePerEntryProbeDoes()
    {
        SeedSgv(Start, 120, "Dexcom G6");
        SeedSgv(Start.AddMinutes(5), 121, "Dexcom G6");
        SeedSgv(Start.AddMinutes(10), 122, "xdrip");
        SeedMbg(Start.AddMinutes(15), 150, "Contour");
        SeedCal(Start.AddMinutes(20), "Dexcom G6");

        var probes = new[]
        {
            Probe("Dexcom G6", "sgv", Start),                             // stored
            Probe("Dexcom G6", "sgv", Start.AddMinutes(10)),              // stored, other device
            Probe(null, "sgv", Start.AddMinutes(10)),                     // no device: matches any
            Probe("Dexcom G6", "sgv", Start.AddMinutes(4)),               // between stored readings
            Probe("Dexcom G6", "sgv", Start.AddMinutes(5).AddMilliseconds(1)), // a millisecond late
            Probe("DEXCOM G6", "sgv", Start),                             // device differs by case only
            Probe("Contour", "mbg", Start.AddMinutes(15)),                // stored meter reading
            Probe("Contour", "mbg", Start.AddMinutes(16)),                // a minute later
            Probe("Dexcom G6", "cal", Start.AddMinutes(20)),              // stored calibration
            Probe("Dexcom G6", "cal", Start.AddMinutes(21)),              // a minute later
            Probe("Dexcom G6", "food", Start),                            // not a probed type
        };

        var expected = new List<string?>();
        foreach (var probe in probes)
        {
            var single = await _sut.CheckDuplicateAsync(probe.Device, probe.Type, probe.Mills);
            expected.Add(single?.Id);
        }

        var batch = await _sut.CheckDuplicatesAsync(probes);

        batch.Select(e => e?.Id).Should().Equal(expected);
        // Not a vacuous comparison: the batch really did find duplicates and really did miss some.
        expected.Should().Contain(id => id != null).And.Contain(id => id == null);
    }

    [Theory]
    [InlineData("sgv", 0, "Dexcom G6", true)]
    [InlineData("sgv", 60_000, "Dexcom G6", false)]
    [InlineData("sgv", 300_000, "Dexcom G6", false)]
    [InlineData("sgv", -300_000, "Dexcom G6", false)]
    [InlineData("sgv", 1, "Dexcom G6", false)]
    [InlineData("sgv", -1, "Dexcom G6", false)]
    [InlineData("sgv", 0, "Juggluco", false)]
    [InlineData("mbg", 0, "Dexcom G6", true)]
    [InlineData("mbg", 1, "Dexcom G6", false)]
    [InlineData("mbg", -1, "Dexcom G6", false)]
    [InlineData("mbg", 60_000, "Dexcom G6", false)]
    [InlineData("mbg", 300_000, "Dexcom G6", false)]
    [InlineData("mbg", 0, "Juggluco", false)]
    [InlineData("cal", 0, "Dexcom G6", true)]
    [InlineData("cal", 1, "Dexcom G6", false)]
    [InlineData("cal", -1, "Dexcom G6", false)]
    [InlineData("cal", 60_000, "Dexcom G6", false)]
    [InlineData("cal", 300_000, "Dexcom G6", false)]
    [InlineData("cal", 0, "Juggluco", false)]
    public async Task OnlyTheSameTypeDeviceAndMillisecond_IsADuplicate(
        string type, int offsetMillis, string device, bool expectDuplicate)
    {
        SeedSgv(Start, 120, "Dexcom G6");
        SeedMbg(Start, 120, "Dexcom G6");
        SeedCal(Start, "Dexcom G6");
        var at = Start.AddMilliseconds(offsetMillis);

        var batch = await _sut.CheckDuplicatesAsync([Probe(device, type, at)]);
        var single = await _sut.CheckDuplicateAsync(device, type, ToMills(at));

        (batch.Single() != null).Should().Be(expectDuplicate);
        batch.Single()?.Id.Should().Be(single?.Id);
    }

    [Theory]
    [InlineData("sgv")]
    [InlineData("mbg")]
    [InlineData("cal")]
    public async Task ReadingOneMillisecondLater_IsNotTheEchoedDuplicate(string type)
    {
        foreach (var at in new[] { Start, Start.AddMilliseconds(1) })
        {
            SeedSgv(at, 120, "Dexcom G6");
            SeedMbg(at, 120, "Dexcom G6");
            SeedCal(at, "Dexcom G6");
        }

        var batch = await _sut.CheckDuplicatesAsync([Probe("Dexcom G6", type, Start)]);
        var single = await _sut.CheckDuplicateAsync("Dexcom G6", type, ToMills(Start));

        batch.Single()!.Mills.Should().Be(ToMills(Start));
        single!.Mills.Should().Be(ToMills(Start));
    }

    [Fact]
    public async Task StoredTimestampWithSubMillisecondPrecision_IsADuplicateOfItsMillisecond()
    {
        SeedSgv(Start.AddTicks(5_000), 120, "Dexcom G6");

        var batch = await _sut.CheckDuplicatesAsync([Probe("Dexcom G6", "sgv", Start)]);
        var single = await _sut.CheckDuplicateAsync("Dexcom G6", "sgv", ToMills(Start));

        batch.Single().Should().NotBeNull();
        single.Should().NotBeNull();
    }

    [Fact]
    public async Task TiedTimestamps_EchoTheSameRowThePerEntryProbeReturns()
    {
        // Two stored readings identical but for their id: the probe's `id DESC` tie-break decides
        // which one is echoed, and the batch path must not re-resolve it (a .NET Guid comparison
        // does not order like Postgres, so re-sorting in memory would answer differently).
        var lower = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var higher = Guid.Parse("22222222-2222-2222-2222-222222222222");
        SeedSgv(Start, 120, "Dexcom G6", lower);
        SeedSgv(Start, 120, "Dexcom G6", higher);

        var batch = await _sut.CheckDuplicatesAsync([Probe("Dexcom G6", "sgv", Start)]);
        var single = await _sut.CheckDuplicateAsync("Dexcom G6", "sgv", ToMills(Start));

        batch.Single()!.Id.Should().Be(single!.Id);
        batch.Single()!.Id.Should().Be(higher.ToString());
    }

    [Fact]
    public async Task MeterGlucose_KeepsThePerEntryProbe()
    {
        for (var i = 0; i < 120; i++)
            SeedMbg(Start.AddSeconds(60 + i), 150, "Contour");
        SeedMbg(Start, 150, "Contour");

        var probe = Probe("Contour", "mbg", Start);
        var batch = await _sut.CheckDuplicatesAsync([probe]);
        var single = await _sut.CheckDuplicateAsync("Contour", "mbg", probe.Mills);

        batch.Single()!.Mills.Should().Be(probe.Mills);
        batch.Single()!.Id.Should().Be(single?.Id);
        _statements.SelectsAgainst("sensor_glucose").Should().Be(0);
    }

    [Fact]
    public async Task StoredCopyLinkedAsNonPrimary_IsStillADuplicate()
    {
        // The raw-storage semantics of the single-entry probe: a reading whose only copy is hidden
        // from reads as a non-primary cross-connector duplicate is still stored, and re-inserting
        // it on every upload cycle is the bug FindStoredDuplicateAsync exists to prevent.
        var id = SeedSgv(Start, 120, "Dexcom G6");
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            RecordId = id,
            RecordType = "SensorGlucose",
            SourceTimestamp = new DateTimeOffset(Start, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            IsPrimary = false,
            CanonicalId = Guid.NewGuid(),
        });
        _context.SaveChanges();

        var batch = await _sut.CheckDuplicatesAsync([Probe("Dexcom G6", "sgv", Start)]);

        batch.Single().Should().NotBeNull();
    }

    private static EntryDuplicateProbe Probe(string? device, string type, DateTime at) =>
        new(device, type, ToMills(at));

    private static long ToMills(DateTime at) =>
        new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private Guid SeedSgv(DateTime timestamp, double mgdl, string device, Guid? forcedId = null)
    {
        var id = forcedId ?? Guid.NewGuid();
        _context.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            Mgdl = mgdl,
            Device = device,
        });
        _context.SaveChanges();
        return id;
    }

    private void SeedMbg(DateTime timestamp, double mgdl, string device)
    {
        _context.MeterGlucose.Add(new MeterGlucoseEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            Timestamp = timestamp,
            Mgdl = mgdl,
            Device = device,
        });
        _context.SaveChanges();
    }

    private void SeedCal(DateTime timestamp, string device)
    {
        _context.Calibrations.Add(new CalibrationEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            Timestamp = timestamp,
            Slope = 1000,
            Intercept = 25000,
            Scale = 1,
            Device = device,
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Counts the statements the context issues, so a test can assert how many queries a batch
    /// cost — the property this change exists to alter, and the one row counts cannot show.
    /// </summary>
    private sealed class StatementRecorder : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        public void Clear() => _statements.Clear();

        public int SelectsAgainst(string table) => _statements.Count(s =>
            s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && s.Contains(table, StringComparison.OrdinalIgnoreCase));

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command) => _statements.Add(command.CommandText.Trim());
    }
}
