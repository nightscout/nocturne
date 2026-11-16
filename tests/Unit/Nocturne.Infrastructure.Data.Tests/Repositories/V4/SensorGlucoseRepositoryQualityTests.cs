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
/// The canonical and analytics reads leave out readings the sensor marked invalid, in SQL, so a long
/// sensor-error episode can't fill a page; a read of every stream still returns them.
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
            new Mock<IDuplicateLinker>().Object,
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

    [Theory]
    [InlineData(GlucoseStreams.Canonical)]
    [InlineData(GlucoseStreams.CanonicalExcludingEphemeral)]
    public async Task ACanonicalRead_LeavesOutInvalidReadingsBeforeTheLimit(GlucoseStreams streams)
    {
        await SeedEpisodeAsync();

        var newest = await _repo.GetAsync(null, null, null, null, limit: 2, streams: streams);

        newest.Select(r => r.Mgdl).Should().Equal(112, 110);
    }

    [Fact]
    public async Task AReadOfEveryStream_ReturnsInvalidReadingsMarked()
    {
        await SeedEpisodeAsync();

        var newest = (await _repo.GetAsync(null, null, null, null, limit: 1, streams: GlucoseStreams.All)).Single();

        newest.Quality.Should().Be(SensorGlucoseQuality.Invalid);
    }

    [Fact]
    public async Task AnAnalyticsRead_LeavesOutInvalidReadingsForEveryStreamSelection()
    {
        await SeedEpisodeAsync();

        var read = await _repo.GetForAnalyticsAsync(null, null, null, null, limit: 100, streams: GlucoseStreams.All);

        read.Select(r => r.Mgdl).Should().Equal(112, 110);
    }

    [Fact]
    public async Task AnEntriesRead_LeavesOutInvalidReadings()
    {
        await SeedEpisodeAsync();

        var read = await _repo.GetForEntriesAsync(null, null, null, null, limit: 100, streams: GlucoseStreams.Canonical);

        read.Select(r => r.Mgdl).Should().Equal(112, 110);
    }

    [Fact]
    public async Task ADisplayRead_KeepsInvalidReadingsMarked()
    {
        await SeedEpisodeAsync();

        var read = (await _repo.GetCanonicalWithUncountedAsync(null, null, limit: 100, descending: false)).ToList();

        read.Should().HaveCount(13);
        read.Select(r => r.Quality).Should().StartWith([null, SensorGlucoseQuality.Uncertain, SensorGlucoseQuality.Invalid]);
    }

    [Fact]
    public async Task History_DeliversAnInvalidReadingAsDeleted()
    {
        await _repo.CreateAsync(Reading(0, 110, null), WriteOrigin.Live);
        await _repo.CreateAsync(Reading(5, 50, SensorGlucoseQuality.Invalid), WriteOrigin.Live);

        var page = await _repo.GetCanonicalModifiedSinceAsync(0, 10);

        page.Select(r => (r.Record.Mgdl, r.Deleted)).Should().BeEquivalentTo([(110d, false), (50d, true)]);
    }

    private static SensorGlucose Reading(int minute, double mgdl, SensorGlucoseQuality? quality) => new()
    {
        Timestamp = Start.AddMinutes(minute),
        Mgdl = mgdl,
        DataSource = "test-source",
        Quality = quality,
    };
}
