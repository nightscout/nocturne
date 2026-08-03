using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Covers the tenant LastReadingAt maintenance in <see cref="SensorGlucoseRepository"/>. The
/// column is the alert sweep's and tenant overview's cheap "when did CGM data last arrive"
/// signal, and it used to be written only by the connector publish path — tenants whose glucose
/// arrived via direct uploader POSTs (Trio, xDrip) kept a NULL forever, reading as "no data
/// ever" to the staleness evaluators. Maintaining it at the repository chokepoint covers every
/// write path; advance-only semantics keep a backfill of old history from moving it backwards
/// or a re-upload from regressing it.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositoryLastReadingTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private readonly DbConnection _connection;
    private readonly NocturneDbContext _context;
    private readonly SensorGlucoseRepository _repo;

    public SensorGlucoseRepositoryLastReadingTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seedContext = new NocturneDbContext(options))
        {
            seedContext.TenantId = TestTenantId;
            seedContext.Database.EnsureCreated();
            seedContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
            seedContext.SaveChanges();
        }

        _context = new NocturneDbContext(options) { TenantId = TestTenantId };

        _repo = new SensorGlucoseRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SensorGlucose Reading(DateTime timestamp, double mgdl = 120) => new()
    {
        Mgdl = mgdl,
        Timestamp = timestamp,
        DataSource = "test-source",
    };

    private DateTime? TenantLastReading()
    {
        _context.ChangeTracker.Clear();
        return _context.Tenants.AsNoTracking().Single(t => t.Id == TestTenantId).LastReadingAt;
    }

    [Fact]
    public async Task BulkCreateAsync_AdvancesLastReadingAt_ToTheNewestReadingTimestamp()
    {
        var newest = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        await _repo.BulkCreateAsync(
            [Reading(newest.AddMinutes(-10)), Reading(newest), Reading(newest.AddMinutes(-5))],
            WriteOrigin.Live);

        TenantLastReading().Should().Be(newest);
    }

    [Fact]
    public async Task BulkCreateAsync_NeverMovesLastReadingAtBackwards()
    {
        // A backfill of old history (or a late-arriving out-of-order batch) must not make a
        // currently-silent sensor look like it stopped even earlier — or a live one look stale.
        var current = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        await _repo.BulkCreateAsync([Reading(current)], WriteOrigin.Live);

        await _repo.BulkCreateAsync(
            [Reading(current.AddYears(-1)), Reading(current.AddMonths(-6))],
            WriteOrigin.Backfill);

        TenantLastReading().Should().Be(current);
    }

    [Fact]
    public async Task CreateAsync_AdvancesLastReadingAt()
    {
        var timestamp = new DateTime(2026, 8, 1, 12, 5, 0, DateTimeKind.Utc);

        await _repo.CreateAsync(Reading(timestamp), WriteOrigin.Live);

        TenantLastReading().Should().Be(timestamp);
    }

    [Fact]
    public async Task CreateAsync_SyncIdUpsertOfTheNewestReading_AdvancesLastReadingAt()
    {
        // A connector replay of the newest reading is still an arrival.
        var timestamp = new DateTime(2026, 8, 1, 12, 10, 0, DateTimeKind.Utc);
        var first = Reading(timestamp);
        first.SyncIdentifier = "sync-1";
        await _repo.CreateAsync(first, WriteOrigin.Live);
        _context.ChangeTracker.Clear();

        var replay = Reading(timestamp);
        replay.SyncIdentifier = "sync-1";
        replay.Mgdl = 121;
        await _repo.CreateAsync(replay, WriteOrigin.Live);

        TenantLastReading().Should().Be(timestamp);
    }

    [Fact]
    public async Task FutureDatedReadings_ClampToWallClock()
    {
        // Advance-only would let one future-dated reading (device clock skew, a double-applied
        // timezone offset) pin the column ahead of now for good — silently disabling the
        // staleness and signal-loss evaluators that compare "now - LastReadingAt".
        var before = DateTime.UtcNow;
        await _repo.BulkCreateAsync([Reading(before.AddYears(1))], WriteOrigin.Live);
        var after = DateTime.UtcNow;

        var lastReading = TenantLastReading();
        lastReading.Should().NotBeNull();
        lastReading!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after,
            "a future reading timestamp must clamp to the wall clock");
    }
}
