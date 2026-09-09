using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Covers <see cref="SensorGlucoseRepository.FindStoredDuplicateAsync"/>, the raw-storage duplicate
/// probe backing the v1 upload duplicate check. The probe must see readings whose copies are linked
/// as non-primary cross-connector duplicates — <c>GetAsync</c> hides those, and checking through it
/// made a second source (e.g. a Share bridge posting alongside the Dexcom connector) re-insert its
/// whole upload window on every cycle because none of its earlier copies were ever visible.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositoryDuplicateProbeTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly SensorGlucoseRepository _repo;

    public SensorGlucoseRepositoryDuplicateProbeTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);

        _context = _db.CreateContext();

        var dedup = new Mock<IDeduplicationService>();
        _repo = new SensorGlucoseRepository(
            new TestTenantDbContextFactory(_context),
            dedup.Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedReading(DateTime timestamp, double mgdl, string device)
    {
        var id = Guid.NewGuid();
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

    private void LinkNonPrimary(Guid recordId, DateTime timestamp)
    {
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            CanonicalId = Guid.NewGuid(),
            RecordType = "sensorglucose",
            RecordId = recordId,
            SourceTimestamp = new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            DataSource = "unknown",
            IsPrimary = false,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task FindStoredDuplicateAsync_NonPrimaryLinkedCopy_IsStillFound()
    {
        var now = DateTime.UtcNow;
        var hiddenId = SeedReading(now, 134, "Dexcom G7 DXCMRf");
        LinkNonPrimary(hiddenId, now);

        // Sanity: the read path hides the non-primary copy…
        var visible = (await _repo.GetAsync(
            from: now.AddMinutes(-5), to: now.AddMinutes(5), device: "Dexcom G7 DXCMRf", source: null,
            limit: 100, offset: 0, descending: true,
            nativeOnly: false, afterTimestamp: null, afterId: null)).ToList();
        visible.Should().BeEmpty();

        // …but the duplicate probe must not: the reading is already stored.
        var match = await _repo.FindStoredDuplicateAsync(
            "Dexcom G7 DXCMRf", 134, now.AddMinutes(-5), now.AddMinutes(5));
        match.Should().NotBeNull();
        match!.Id.Should().Be(hiddenId);
    }

    [Theory]
    [InlineData(134.005, true)]  // inside ±0.01
    [InlineData(134.02, false)]  // just outside
    [InlineData(135, false)]
    public async Task FindStoredDuplicateAsync_MatchesValueWithinTolerance(double probeValue, bool expectMatch)
    {
        var now = DateTime.UtcNow;
        SeedReading(now, 134, "Dexcom G7 DXCMRf");

        var match = await _repo.FindStoredDuplicateAsync(
            "Dexcom G7 DXCMRf", probeValue, now.AddMinutes(-5), now.AddMinutes(5));

        (match != null).Should().Be(expectMatch);
    }

    [Fact]
    public async Task FindStoredDuplicateAsync_SoftDeletedRow_ReturnsNull()
    {
        // Guardrail: the probe skips the LinkedRecords visibility exclusion, but must keep the
        // global query filters — a soft-deleted reading is not a stored duplicate, and widening
        // the probe with IgnoreQueryFilters would also breach tenant isolation.
        var now = DateTime.UtcNow;
        var id = SeedReading(now, 134, "Dexcom G7 DXCMRf");
        var entity = _context.SensorGlucose.IgnoreQueryFilters().Single(e => e.Id == id);
        entity.DeletedAt = now;
        _context.SaveChanges();

        var match = await _repo.FindStoredDuplicateAsync(
            "Dexcom G7 DXCMRf", 134, now.AddMinutes(-5), now.AddMinutes(5));

        match.Should().BeNull();
    }

    [Fact]
    public async Task FindStoredDuplicateAsync_OtherTenantsRow_ReturnsNull()
    {
        var otherTenant = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var now = DateTime.UtcNow;
        _context.Tenants.Add(new TenantEntity { Id = otherTenant, Slug = "other" });
        _context.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenant,
            Timestamp = now,
            Mgdl = 134,
            Device = "Dexcom G7 DXCMRf",
        });
        _context.SaveChanges();

        var match = await _repo.FindStoredDuplicateAsync(
            "Dexcom G7 DXCMRf", 134, now.AddMinutes(-5), now.AddMinutes(5));

        match.Should().BeNull();
    }

    [Fact]
    public async Task FindStoredDuplicateAsync_DifferentDevice_ReturnsNull()
    {
        var now = DateTime.UtcNow;
        SeedReading(now, 134, "dexcom-connector");

        var match = await _repo.FindStoredDuplicateAsync(
            "Dexcom G7 DXCMRf", 134, now.AddMinutes(-5), now.AddMinutes(5));

        match.Should().BeNull();
    }

    [Fact]
    public async Task FindStoredDuplicateAsync_NullDeviceAndValue_MatchesAnyInWindow()
    {
        var now = DateTime.UtcNow;
        var id = SeedReading(now, 134, "Dexcom G7 DXCMRf");

        var match = await _repo.FindStoredDuplicateAsync(
            device: null, mgdl: null, now.AddMinutes(-5), now.AddMinutes(5));

        match.Should().NotBeNull();
        match!.Id.Should().Be(id);
    }

    [Fact]
    public async Task FindStoredDuplicateAsync_OutsideWindow_ReturnsNull()
    {
        var now = DateTime.UtcNow;
        SeedReading(now.AddMinutes(-30), 134, "Dexcom G7 DXCMRf");

        var match = await _repo.FindStoredDuplicateAsync(
            "Dexcom G7 DXCMRf", 134, now.AddMinutes(-5), now.AddMinutes(5));

        match.Should().BeNull();
    }

    [Fact]
    public async Task FindStoredDuplicateCandidatesAsync_ReturnsWindowNewestFirst()
    {
        // The caller reproduces the single-entry probe by taking the first match in this order,
        // so the ordering is the contract, not an incidental detail.
        var now = DateTime.UtcNow;
        SeedReading(now.AddMinutes(-10), 130, "Dexcom G7 DXCMRf");
        SeedReading(now, 134, "Dexcom G7 DXCMRf");
        SeedReading(now.AddMinutes(-5), 132, "Dexcom G7 DXCMRf");
        SeedReading(now.AddMinutes(-40), 120, "Dexcom G7 DXCMRf");

        var candidates = await _repo.FindStoredDuplicateCandidatesAsync(
            ["Dexcom G7 DXCMRf"], now.AddMinutes(-15), now.AddMinutes(5));

        candidates.Select(c => c.Mgdl).Should().Equal(134, 132, 130);
    }

    [Fact]
    public async Task FindStoredDuplicateCandidatesAsync_FiltersToTheNamedDevices()
    {
        var now = DateTime.UtcNow;
        SeedReading(now, 134, "Dexcom G7 DXCMRf");
        SeedReading(now, 135, "xdrip");
        SeedReading(now, 136, "dexcom-connector");

        var candidates = await _repo.FindStoredDuplicateCandidatesAsync(
            ["Dexcom G7 DXCMRf", "xdrip"], now.AddMinutes(-5), now.AddMinutes(5));

        candidates.Select(c => c.Mgdl).Should().BeEquivalentTo(new[] { 134d, 135d });
    }

    [Fact]
    public async Task FindStoredDuplicateCandidatesAsync_NoDevices_ReturnsEveryDevice()
    {
        var now = DateTime.UtcNow;
        SeedReading(now, 134, "Dexcom G7 DXCMRf");
        SeedReading(now, 135, "xdrip");

        var candidates = await _repo.FindStoredDuplicateCandidatesAsync(
            devices: null, now.AddMinutes(-5), now.AddMinutes(5));

        candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task FindStoredDuplicateCandidatesAsync_KeepsNonPrimaryCopiesAndDropsDeletedRows()
    {
        // Same raw-storage semantics as the single-entry probe: hidden duplicate copies count as
        // stored, soft-deleted rows do not.
        var now = DateTime.UtcNow;
        var hiddenId = SeedReading(now, 134, "Dexcom G7 DXCMRf");
        LinkNonPrimary(hiddenId, now);
        var deletedId = SeedReading(now.AddMinutes(-1), 133, "Dexcom G7 DXCMRf");
        var deleted = _context.SensorGlucose.IgnoreQueryFilters().Single(e => e.Id == deletedId);
        deleted.DeletedAt = now;
        _context.SaveChanges();

        var candidates = await _repo.FindStoredDuplicateCandidatesAsync(
            ["Dexcom G7 DXCMRf"], now.AddMinutes(-5), now.AddMinutes(5));

        candidates.Select(c => c.Id).Should().Equal(hiddenId);
    }

    [Fact]
    public async Task FindStoredDuplicateCandidatesAsync_OtherTenantsRows_AreExcluded()
    {
        var otherTenant = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var now = DateTime.UtcNow;
        _context.Tenants.Add(new TenantEntity { Id = otherTenant, Slug = "other" });
        _context.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenant,
            Timestamp = now,
            Mgdl = 134,
            Device = "Dexcom G7 DXCMRf",
        });
        _context.SaveChanges();

        var candidates = await _repo.FindStoredDuplicateCandidatesAsync(
            ["Dexcom G7 DXCMRf"], now.AddMinutes(-5), now.AddMinutes(5));

        candidates.Should().BeEmpty();
    }
}
