using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// Tests for dedup reconciliation in <see cref="DeduplicationService"/>: the reusable
/// per-record criteria + deleted-status loader (<see cref="MatchCriteria"/> plus soft-deleted
/// status keyed by record id), merging duplicate canonical groups (full and candidate-bounded,
/// including transitive chains), the per-tenant reconciliation watermark round-trip, and the
/// watermark-bounded chunked reconcile pass over newly-created links.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
public class DeduplicationReconcileTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly NocturneDbContext _context;
    private readonly DeduplicationService _service;

    public DeduplicationReconcileTests()
    {
        // In-memory SQLite database for testing — mirrors CarbIntakeRepositoryTests.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        // Create the database schema and seed the tenant.
        using (var seedContext = new NocturneDbContext(_contextOptions))
        {
            seedContext.TenantId = TestTenantId;
            seedContext.Database.EnsureCreated();
            seedContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
            seedContext.SaveChanges();
        }

        _context = new NocturneDbContext(_contextOptions);
        _context.TenantId = TestTenantId;

        _service = new DeduplicationService(
            _context,
            new Mock<IServiceScopeFactory>().Object,
            NullLogger<DeduplicationService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadRecordInfoAsync_CarbIntake_ReturnsCriteriaAndDeletedFlag()
    {
        var live = Guid.CreateVersion7();
        var gone = Guid.CreateVersion7();
        _context.CarbIntakes.Add(new CarbIntakeEntity { Id = live, TenantId = TestTenantId, Carbs = 42, Timestamp = DateTime.UtcNow });
        _context.CarbIntakes.Add(new CarbIntakeEntity { Id = gone, TenantId = TestTenantId, Carbs = 42, Timestamp = DateTime.UtcNow, DeletedAt = DateTime.UtcNow });
        await _context.SaveChangesAsync();

        var info = await _service.LoadRecordInfoForTestAsync(RecordType.CarbIntake, new HashSet<Guid> { live, gone });

        info[live].Criteria.Carbs.Should().Be(42);
        info[live].IsDeleted.Should().BeFalse();
        info[gone].IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task LoadRecordInfoAsync_CarbIntake_ExcludesOtherTenants()
    {
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();
        var otherTenant = Guid.Parse("00000000-0000-0000-0000-000000000002");

        // The cross-tenant row references a real tenant, so seed it (bypassing the
        // query filter) to satisfy the FK constraint without it leaking into _context.
        using (var seedContext = new NocturneDbContext(_contextOptions))
        {
            seedContext.TenantId = otherTenant;
            seedContext.Tenants.Add(new TenantEntity { Id = otherTenant, Slug = "other" });
            seedContext.SaveChanges();
        }

        // Seed a record for the current tenant and one belonging to a different tenant.
        // IgnoreQueryFilters bypasses the soft-delete filter, so tenant scoping must be
        // re-applied explicitly; otherwise the cross-tenant row leaks into the result.
        _context.CarbIntakes.Add(new CarbIntakeEntity { Id = mine, TenantId = TestTenantId, Carbs = 10, Timestamp = DateTime.UtcNow });
        _context.CarbIntakes.Add(new CarbIntakeEntity { Id = theirs, TenantId = otherTenant, Carbs = 99, Timestamp = DateTime.UtcNow });
        await _context.SaveChangesAsync();

        var info = await _service.LoadRecordInfoForTestAsync(RecordType.CarbIntake, new HashSet<Guid> { mine, theirs });

        info.Should().ContainKey(mine);
        info.Should().NotContainKey(theirs);
    }

    [Fact]
    public async Task MergeDuplicateGroupsAsync_MergesTwoPrimaryGroupsForSameMeal()
    {
        var t = DateTime.UtcNow;
        var mylife = await AddCarb(t, "mylife-connector", 50);
        var glooko = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, mylife, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, glooko, ToMills(t.AddSeconds(20)), "glooko-connector");
        await _context.SaveChangesAsync();

        var merged = await _service.MergeDuplicateGroupsAsync(RecordType.CarbIntake, null, CancellationToken.None);

        merged.Should().Be(1);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(l => l.IsPrimary).Should().Be(1);
        links.Single(l => l.IsPrimary).RecordId.Should().Be(mylife); // earliest, non-deleted
    }

    [Fact]
    public async Task MergeDuplicateGroupsAsync_DoesNotMergeDifferentValues()
    {
        var t = DateTime.UtcNow;
        var mylife = await AddCarb(t, "mylife-connector", 50);
        var glooko = await AddCarb(t.AddSeconds(20), "glooko-connector", 80);
        AddPrimaryLink(RecordType.CarbIntake, mylife, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, glooko, ToMills(t.AddSeconds(20)), "glooko-connector");
        await _context.SaveChangesAsync();

        var merged = await _service.MergeDuplicateGroupsAsync(RecordType.CarbIntake, null, CancellationToken.None);

        merged.Should().Be(0);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task MergeDuplicateGroupsAsync_DoesNotMergeOutsideWindow()
    {
        var t = DateTime.UtcNow;
        var mylife = await AddCarb(t, "mylife-connector", 50);
        var glooko = await AddCarb(t.AddSeconds(90), "glooko-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, mylife, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, glooko, ToMills(t.AddSeconds(90)), "glooko-connector");
        await _context.SaveChangesAsync();

        var merged = await _service.MergeDuplicateGroupsAsync(RecordType.CarbIntake, null, CancellationToken.None);

        merged.Should().Be(0);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task MergeDuplicateGroupsAsync_PrefersNonDeletedAsPrimary()
    {
        var t = DateTime.UtcNow;
        // Earliest record is soft-deleted; the later non-deleted record must become the surviving primary.
        var deleted = await AddCarb(t, "mylife-connector", 50, deletedAt: DateTime.UtcNow);
        var live = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, deleted, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, live, ToMills(t.AddSeconds(20)), "glooko-connector");
        await _context.SaveChangesAsync();

        var merged = await _service.MergeDuplicateGroupsAsync(RecordType.CarbIntake, null, CancellationToken.None);

        merged.Should().Be(1);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(l => l.IsPrimary).Should().Be(1);
        links.Single(l => l.IsPrimary).RecordId.Should().Be(live); // earliest non-deleted
    }

    [Fact]
    public async Task MergeDuplicateGroupsAsync_MergesTransitiveThreeGroupChain()
    {
        var t = DateTime.UtcNow;
        // Three groups 20s apart: endpoints are 40s apart (> the 30s window) but connect
        // transitively through the middle group, so all three must collapse to one.
        var a = await AddCarb(t, "mylife-connector", 50);
        var b = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        var c = await AddCarb(t.AddSeconds(40), "libre-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, a, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, b, ToMills(t.AddSeconds(20)), "glooko-connector");
        AddPrimaryLink(RecordType.CarbIntake, c, ToMills(t.AddSeconds(40)), "libre-connector");
        await _context.SaveChangesAsync();

        var merged = await _service.MergeDuplicateGroupsAsync(RecordType.CarbIntake, null, CancellationToken.None);

        merged.Should().Be(2);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(l => l.IsPrimary).Should().Be(1);
    }

    [Fact]
    public async Task MergeDuplicateGroupsAsync_CandidatePath_MergesCandidateWithNeighbour()
    {
        var t = DateTime.UtcNow;
        var mylife = await AddCarb(t, "mylife-connector", 50);
        var glooko = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, mylife, ToMills(t), "mylife-connector");
        var glookoCanonical = AddPrimaryLink(RecordType.CarbIntake, glooko, ToMills(t.AddSeconds(20)), "glooko-connector");
        await _context.SaveChangesAsync();

        var merged = await _service.MergeDuplicateGroupsAsync(
            RecordType.CarbIntake,
            new HashSet<Guid> { glookoCanonical },
            CancellationToken.None);

        merged.Should().Be(1);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task MergeDuplicateGroupsAsync_CandidatePath_LeavesUnrelatedPairAlone()
    {
        var t = DateTime.UtcNow;
        // Candidate group plus its in-window neighbour (these should merge).
        var candA = await AddCarb(t, "mylife-connector", 50);
        var candB = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        var candCanonical = AddPrimaryLink(RecordType.CarbIntake, candA, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, candB, ToMills(t.AddSeconds(20)), "glooko-connector");

        // A SEPARATE unrelated matching pair far away in time — must NOT be touched.
        var farT = t.AddHours(5);
        var farA = await AddCarb(farT, "mylife-connector", 80);
        var farB = await AddCarb(farT.AddSeconds(20), "glooko-connector", 80);
        AddPrimaryLink(RecordType.CarbIntake, farA, ToMills(farT), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, farB, ToMills(farT.AddSeconds(20)), "glooko-connector");
        await _context.SaveChangesAsync();

        var merged = await _service.MergeDuplicateGroupsAsync(
            RecordType.CarbIntake,
            new HashSet<Guid> { candCanonical },
            CancellationToken.None);

        // Only the candidate region was reconciled: 1 merge, and the far pair is untouched.
        merged.Should().Be(1);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        // candidate pair collapsed to 1 canonical; far pair still 2 distinct canonicals => 3 total.
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(3);
        // The far pair still has its two separate primaries.
        var farLinks = links.Where(l => l.RecordId == farA || l.RecordId == farB).ToList();
        farLinks.Select(l => l.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task ReconcileNewLinksAsync_MergesGroupsWithRecentLinks()
    {
        var now = DateTime.UtcNow;
        await _service.SetWatermarkAsync(now.AddHours(-1), CancellationToken.None);

        var t = now.AddMinutes(-5);
        var mylife = await AddCarb(t, "mylife-connector", 50);
        var glooko = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, mylife, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, glooko, ToMills(t.AddSeconds(20)), "glooko-connector");
        await _context.SaveChangesAsync();
        await SetAllLinkSysCreatedAt(now);

        var result = await _service.ReconcileNewLinksAsync(5000, 10, CancellationToken.None);

        result.GroupsMerged.Should().Be(1);
        result.CaughtUp.Should().BeTrue();
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(1);

        var watermark = await _service.GetWatermarkAsync(CancellationToken.None);
        watermark.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReconcileNewLinksAsync_IgnoresLinksBeforeWatermark()
    {
        var now = DateTime.UtcNow;
        await _service.SetWatermarkAsync(now, CancellationToken.None);

        // Links created an hour ago — well before (watermark - overlap), so out of scope.
        var t = now.AddHours(-1);
        var mylife = await AddCarb(t, "mylife-connector", 50);
        var glooko = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, mylife, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, glooko, ToMills(t.AddSeconds(20)), "glooko-connector");
        await _context.SaveChangesAsync();
        await SetAllLinkSysCreatedAt(now.AddHours(-1));

        var result = await _service.ReconcileNewLinksAsync(5000, 10, CancellationToken.None);

        result.GroupsMerged.Should().Be(0);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task ReconcileNewLinksAsync_SkipsUnknownRecordType()
    {
        var now = DateTime.UtcNow;
        await _service.SetWatermarkAsync(now.AddHours(-1), CancellationToken.None);

        // A valid mergeable pair that should still collapse despite a bogus row in the batch.
        var t = now.AddMinutes(-5);
        var mylife = await AddCarb(t, "mylife-connector", 50);
        var glooko = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, mylife, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, glooko, ToMills(t.AddSeconds(20)), "glooko-connector");

        // A linked_records row whose free-form RecordType is not a member of the RecordType enum.
        // Enum.Parse on this string would throw and take down the whole reconcile batch loop.
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            CanonicalId = Guid.CreateVersion7(),
            RecordType = "bogustype",
            RecordId = Guid.CreateVersion7(),
            SourceTimestamp = ToMills(t),
            DataSource = "mylife-connector",
            IsPrimary = true,
            SysCreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        await SetAllLinkSysCreatedAt(now);

        var act = async () => await _service.ReconcileNewLinksAsync(5000, 10, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        // The valid pair still merges, and the call returns normally.
        result.Subject.GroupsMerged.Should().Be(1);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task ReconcileNewLinksAsync_FreshTenant_DefaultWatermark_ReconcilesWithoutThrowing()
    {
        // Fresh tenant: no watermark seeded, so GetWatermarkAsync returns DateTime.MinValue.
        // cutoff = watermark - ReconcileOverlap would underflow (ArgumentOutOfRangeException).
        (await _service.GetWatermarkAsync(CancellationToken.None)).Should().Be(DateTime.MinValue);

        var now = DateTime.UtcNow;
        var t = now.AddMinutes(-5);
        var mylife = await AddCarb(t, "mylife-connector", 50);
        var glooko = await AddCarb(t.AddSeconds(20), "glooko-connector", 50);
        AddPrimaryLink(RecordType.CarbIntake, mylife, ToMills(t), "mylife-connector");
        AddPrimaryLink(RecordType.CarbIntake, glooko, ToMills(t.AddSeconds(20)), "glooko-connector");
        await _context.SaveChangesAsync();
        await SetAllLinkSysCreatedAt(now);

        var act = async () => await _service.ReconcileNewLinksAsync(5000, 10, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.GroupsMerged.Should().Be(1);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().Where(l => l.RecordType == "carbintake").ToListAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(1);

        // The watermark advances past MinValue.
        var watermark = await _service.GetWatermarkAsync(CancellationToken.None);
        watermark.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Watermark_RoundTrips_DefaultsToMinValue()
    {
        (await _service.GetWatermarkAsync(CancellationToken.None)).Should().Be(DateTime.MinValue);

        var t = new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc);
        await _service.SetWatermarkAsync(t, CancellationToken.None);

        (await _service.GetWatermarkAsync(CancellationToken.None)).Should().Be(t);
    }

    [Fact]
    public async Task Watermark_SetTwice_Updates()
    {
        var t1 = new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 5, 31, 9, 30, 0, DateTimeKind.Utc);

        await _service.SetWatermarkAsync(t1, CancellationToken.None);
        await _service.SetWatermarkAsync(t2, CancellationToken.None);

        (await _service.GetWatermarkAsync(CancellationToken.None)).Should().Be(t2);

        // Upsert must update the single row, not create a duplicate.
        var rows = await _context.DedupReconcileState.IgnoreQueryFilters()
            .Where(s => s.TenantId == TestTenantId).CountAsync();
        rows.Should().Be(1);
    }

    /// <summary>
    /// Inserts a <see cref="CarbIntakeEntity"/> for the test tenant and returns its id.
    /// </summary>
    private async Task<Guid> AddCarb(DateTime timestamp, string dataSource, double carbs, DateTime? deletedAt = null)
    {
        var id = Guid.CreateVersion7();
        _context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = id,
            TenantId = TestTenantId,
            Carbs = carbs,
            Timestamp = timestamp,
            DataSource = dataSource,
            DeletedAt = deletedAt
        });
        await _context.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// Adds a primary <see cref="LinkedRecordEntity"/> with a fresh canonical id for the test tenant
    /// and returns that canonical id (handy for candidate-bounded reconcile tests).
    /// </summary>
    private Guid AddPrimaryLink(RecordType recordType, Guid recordId, long mills, string source)
    {
        var canonicalId = Guid.CreateVersion7();
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            CanonicalId = canonicalId,
            RecordType = recordType.ToString().ToLowerInvariant(),
            RecordId = recordId,
            SourceTimestamp = mills,
            DataSource = source,
            IsPrimary = true,
            SysCreatedAt = DateTime.UtcNow
        });
        return canonicalId;
    }

    /// <summary>
    /// Overrides <see cref="LinkedRecordEntity.SysCreatedAt"/> on all of the tenant's links.
    /// The SaveChanges interceptor stamps SysCreatedAt = now only on <c>Added</c> rows, so the
    /// initializer value is ignored on insert; updating already-persisted rows lets tests pin a
    /// deterministic ingestion time for the watermark-bounded reconcile pass.
    /// </summary>
    private async Task SetAllLinkSysCreatedAt(DateTime sysCreatedAt)
    {
        var utc = DateTime.SpecifyKind(sysCreatedAt, DateTimeKind.Utc);
        var links = await _context.LinkedRecords.IgnoreQueryFilters().ToListAsync();
        foreach (var link in links)
        {
            link.SysCreatedAt = utc;
        }
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Converts a UTC <see cref="DateTime"/> to Unix milliseconds, matching the link source timestamp.
    /// </summary>
    private static long ToMills(DateTime d) =>
        new DateTimeOffset(d, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
