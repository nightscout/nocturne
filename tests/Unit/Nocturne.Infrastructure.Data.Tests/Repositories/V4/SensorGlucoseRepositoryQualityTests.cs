using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The reads that statistics, alerts and v1/v3 clients use leave out readings the sensor marked
/// invalid, in SQL, so a long sensor-error episode can't fill a page.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositoryQualityTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTime Start = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly SensorGlucoseRepository _repo;

    public SensorGlucoseRepositoryQualityTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _repo = new SensorGlucoseRepository(
            new TestNocturneDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Two trusted readings, then an episode of invalid ones longer than the page.</summary>
    private async Task SeedEpisodeAsync()
    {
        await _repo.CreateAsync(Reading(0, 110, null), WriteOrigin.Live);
        await _repo.CreateAsync(Reading(5, 112, SensorGlucoseQuality.Uncertain), WriteOrigin.Live);
        for (var minute = 10; minute <= 60; minute += 5)
            await _repo.CreateAsync(Reading(minute, 50, SensorGlucoseQuality.Invalid), WriteOrigin.Live);
    }

    [Fact]
    public async Task GetCountedAsync_LeavesOutInvalidReadingsBeforeTheLimit()
    {
        await SeedEpisodeAsync();

        var newest = await _repo.GetCountedAsync(null, null, null, limit: 2, descending: true);

        newest.Select(r => r.Mgdl).Should().Equal(112, 110);
    }

    [Fact]
    public async Task GetForAnalyticsAsync_LeavesOutInvalidReadings()
    {
        await SeedEpisodeAsync();

        var read = await _repo.GetForAnalyticsAsync(null, null, null, null, limit: 100);

        read.Select(r => r.Mgdl).Should().Equal(112, 110);
    }

    [Fact]
    public async Task GetForEntriesAsync_LeavesOutInvalidReadings()
    {
        await SeedEpisodeAsync();

        var read = await _repo.GetForEntriesAsync(null, null, null, null, limit: 100);

        read.Select(r => r.Mgdl).Should().Equal(112, 110);
    }

    private static SensorGlucose Reading(int minute, double mgdl, SensorGlucoseQuality? quality) => new()
    {
        Timestamp = Start.AddMinutes(minute),
        Mgdl = mgdl,
        DataSource = "test-source",
        Quality = quality,
    };
}
