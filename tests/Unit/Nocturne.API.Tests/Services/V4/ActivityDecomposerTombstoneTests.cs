using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A heart rate or step count the user deleted stays deleted: the v1 activity delete leaves a
/// tombstone, and a later decomposition of the same activity id does not write the record again.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityDecomposerTombstoneTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly string _database = $"activity_tombstones_{Guid.NewGuid()}";
    private readonly NocturneDbContext _context;
    private readonly NocturneDbContext _otherTenantContext;
    private readonly ActivityDecomposer _decomposer;

    public ActivityDecomposerTombstoneTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext(_database);
        _context.TenantId = TenantId;
        _decomposer = DecomposerFor(_context);
        _otherTenantContext = TestDbContextFactory.CreateInMemoryContext(_database);
        _otherTenantContext.TenantId = OtherTenantId;
    }

    private static ActivityDecomposer DecomposerFor(NocturneDbContext context) => new(
        new ActivityRecordRepository(context.AsFactory(), TimeProvider.System), Mock.Of<IStateSpanRepository>(), Mock.Of<ISleepService>(), NullLogger<ActivityDecomposer>.Instance);

    public void Dispose()
    {
        _context.Dispose();
        _otherTenantContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DeleteByLegacyIdAsync_SoftDeletesHeartRateAndStepCount()
    {
        _context.HeartRates.Add(new HeartRateEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = "a0000000000000000000000a", Timestamp = DateTime.UtcNow, Bpm = 70 });
        _context.StepCounts.Add(new StepCountEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = "a0000000000000000000000f", Timestamp = DateTime.UtcNow, Metric = 300 });
        await _context.SaveChangesAsync();

        var deleted = await _decomposer.DeleteByLegacyIdAsync("a0000000000000000000000a", WriteOrigin.Live)
                      + await _decomposer.DeleteByLegacyIdAsync("a0000000000000000000000f", WriteOrigin.Live);

        deleted.Should().Be(2);
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().BeEmpty();
        _context.StepCounts.Should().BeEmpty();
        _context.HeartRates.IgnoreQueryFilters().Should().ContainSingle().Which.DeletedAt.Should().NotBeNull();
        _context.StepCounts.IgnoreQueryFilters().Should().ContainSingle().Which.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_DoesNotRecreateAHeartRateTheUserDeleted()
    {
        await SeedTombstoneAsync("a0000000000000000000000a", byUser: true);

        var result = await _decomposer.DecomposeBatchAsync([HeartRate("a0000000000000000000000a")], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().BeEmpty();
        _context.HeartRates.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task DecomposeBatchAsync_RevivesAHeartRateTheSystemSweptInPlace()
    {
        await SeedTombstoneAsync("a0000000000000000000000a", byUser: false);

        await _decomposer.DecomposeBatchAsync([HeartRate("a0000000000000000000000a")], WriteOrigin.Live);

        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().ContainSingle().Which.OriginalId.Should().Be("a0000000000000000000000a");
        _context.HeartRates.IgnoreQueryFilters().Should().ContainSingle(
            "the primary key is derived from the id, so the swept row is revived rather than duplicated");
    }

    [Fact]
    public async Task DecomposeBatchAsync_SkipsOnlyWhatTheUserDeleted()
    {
        await SeedTombstoneAsync("a0000000000000000000000b", byUser: true);
        _context.StepCounts.Add(new StepCountEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = "a0000000000000000000000d", Timestamp = DateTime.UtcNow, Metric = 300, DeletedAt = DateTime.UtcNow });
        await _context.SaveChangesAsync();
        var steps = _context.StepCounts.IgnoreQueryFilters().Single(s => s.OriginalId == "a0000000000000000000000d");
        _context.Entry(steps).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _decomposer.DecomposeBatchAsync(
            [HeartRate("a0000000000000000000000b"), HeartRate("a0000000000000000000000c"), StepCount("a0000000000000000000000d"), StepCount("a0000000000000000000000e")],
            WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(2);
        result.CreatedRecords.Should().HaveCount(2);

        _context.ChangeTracker.Clear();
        _context.HeartRates.Select(h => h.OriginalId).Should().Equal("a0000000000000000000000c");
        _context.StepCounts.Select(s => s.OriginalId).Should().Equal("a0000000000000000000000e");
    }

    [Fact]
    public async Task DecomposeBatchAsync_IgnoresATombstoneAnotherTenantHoldsForTheSameKey()
    {
        await TombstoneForOtherTenantAsync(XDripSteps());
        await TombstoneForOtherTenantAsync(HeartRate("a0000000000000000000000a"));

        var result = await _decomposer.DecomposeBatchAsync(
            [XDripSteps(), HeartRate("a0000000000000000000000a")], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(0);
        result.CreatedRecords.Should().HaveCount(2);
        (await _decomposer.IsDeletedByUserAsync("a0000000000000000000000a")).Should().BeFalse();
    }

    [Fact]
    public async Task DecomposeBatchAsync_DoesNotRecreateAnXDripStepCountTheUserDeleted()
    {
        await SeedXDripStepsTombstoneAsync(byUser: true);

        var result = await _decomposer.DecomposeBatchAsync([XDripSteps()], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.StepCounts.Should().BeEmpty();
        _context.StepCounts.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task DecomposeBatchAsync_WritesAnXDripStepCountBesideASystemSweep()
    {
        await SeedXDripStepsTombstoneAsync(byUser: false);

        var result = await _decomposer.DecomposeBatchAsync([XDripSteps()], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(0);
        result.CreatedRecords.Should().ContainSingle();
        _context.ChangeTracker.Clear();
        _context.StepCounts.Should().ContainSingle().Which.SyncIdentifier.Should().Be(XDripSyncKey);
        _context.StepCounts.IgnoreQueryFilters().Should().HaveCount(2);
    }

    [Fact]
    public async Task DecomposeBatchAsync_DoesNotRecreateAnXDripHeartRateTheUserDeleted()
    {
        await SeedXDripHeartRateTombstoneAsync(byUser: true);

        var result = await _decomposer.DecomposeBatchAsync([XDripHeartRate()], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().BeEmpty();
        _context.HeartRates.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task DecomposeBatchAsync_WritesAnXDripHeartRateBesideASystemSweep()
    {
        await SeedXDripHeartRateTombstoneAsync(byUser: false);

        var result = await _decomposer.DecomposeBatchAsync([XDripHeartRate()], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(0);
        result.CreatedRecords.Should().ContainSingle();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().ContainSingle().Which.SyncIdentifier.Should().Be(XDripHeartRateSyncKey);
        _context.HeartRates.IgnoreQueryFilters().Should().HaveCount(2);
    }

    public static TheoryData<string> NonMongoIds => new()
    {
        "6f1b1f0e-3c5a-4e0a-9d52-1b7a0c1f2e33",
        "uploader-hr-0001",
    };

    [Theory]
    [MemberData(nameof(NonMongoIds))]
    public async Task DecomposeBatchAsync_UpdatesAHeartRateReSentUnderANonMongoId(string id)
    {
        await _decomposer.DecomposeBatchAsync([HeartRate(id)], WriteOrigin.Live);
        _context.ChangeTracker.Clear();

        var resend = HeartRate(id);
        resend.AdditionalProperties!["bpm"] = 90;
        var result = await _decomposer.DecomposeBatchAsync([resend], WriteOrigin.Live);

        result.UpdatedRecords.Should().ContainSingle();
        result.CreatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().ContainSingle().Which.Bpm.Should().Be(90);
    }

    [Theory]
    [MemberData(nameof(NonMongoIds))]
    public async Task DecomposeBatchAsync_UpdatesAStepCountReSentUnderANonMongoIdInABatch(string id)
    {
        await _decomposer.DecomposeBatchAsync([StepCount(id), HeartRate(id + "-b")], WriteOrigin.Live);
        _context.ChangeTracker.Clear();

        var resend = StepCount(id);
        resend.AdditionalProperties!["metric"] = 900;
        var result = await _decomposer.DecomposeBatchAsync([resend, HeartRate(id + "-b")], WriteOrigin.Live);

        result.UpdatedRecords.Should().ContainSingle();
        result.UnchangedRecords.Should().ContainSingle();
        _context.ChangeTracker.Clear();
        _context.StepCounts.Should().ContainSingle().Which.Metric.Should().Be(900);
        _context.HeartRates.Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(NonMongoIds))]
    public async Task DecomposeBatchAsync_DoesNotRecreateAHeartRateTheUserDeletedUnderANonMongoId(string id)
    {
        await _decomposer.DecomposeBatchAsync([HeartRate(id)], WriteOrigin.Live);
        await TombstoneHeartRateAsync(byUser: true);

        var result = await _decomposer.DecomposeBatchAsync([HeartRate(id)], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(NonMongoIds))]
    public async Task DecomposeBatchAsync_RevivesAHeartRateTheSystemSweptUnderANonMongoId(string id)
    {
        await _decomposer.DecomposeBatchAsync([HeartRate(id)], WriteOrigin.Live);
        await TombstoneHeartRateAsync(byUser: false);

        var result = await _decomposer.DecomposeBatchAsync([HeartRate(id)], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(0);
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().ContainSingle();
        _context.HeartRates.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task DecomposeBatchAsync_CollapsesIdsThatDifferOnlyInCaseInOneBatch()
    {
        const string lower = "6f1b1f0e-3c5a-4e0a-9d52-1b7a0c1f2e33";
        var first = HeartRate(lower);
        var second = HeartRate(lower.ToUpperInvariant());
        second.AdditionalProperties!["bpm"] = 90;

        var result = await _decomposer.DecomposeBatchAsync([first, second], WriteOrigin.Live);

        result.CreatedRecords.Should().ContainSingle();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().ContainSingle().Which.Bpm.Should().Be(90);

        var again = await _decomposer.DecomposeBatchAsync([HeartRate(lower), HeartRate(lower.ToUpperInvariant())], WriteOrigin.Live);

        again.UpdatedRecords.Should().ContainSingle();
        again.CreatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeBatchAsync_StoresAHeartRateWithAnEmptyId()
    {
        var result = await _decomposer.DecomposeBatchAsync([HeartRate("")], WriteOrigin.Live);

        result.CreatedRecords.Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(NonMongoIds))]
    public async Task DeleteByLegacyIdAsync_SoftDeletesRecordsStoredUnderANonMongoId(string id)
    {
        await _decomposer.DecomposeBatchAsync([HeartRate(id), StepCount(id)], WriteOrigin.Live);
        _context.ChangeTracker.Clear();

        var deleted = await _decomposer.DeleteByLegacyIdAsync(id, WriteOrigin.Live);

        deleted.Should().Be(2);
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().BeEmpty();
        _context.StepCounts.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(NonMongoIds))]
    public async Task IsDeletedByUserAsync_ReportsAHeartRateTheUserDeletedUnderANonMongoId(string id)
    {
        await _decomposer.DecomposeBatchAsync([HeartRate(id)], WriteOrigin.Live);
        await TombstoneHeartRateAsync(byUser: true);

        (await _decomposer.IsDeletedByUserAsync(id)).Should().BeTrue();
    }

    private async Task TombstoneForOtherTenantAsync(Activity activity)
    {
        await DecomposerFor(_otherTenantContext).DecomposeBatchAsync([activity], WriteOrigin.Live);
        foreach (var row in await _otherTenantContext.HeartRates.ToListAsync())
            Tombstone(row);
        foreach (var row in await _otherTenantContext.StepCounts.ToListAsync())
            Tombstone(row);
        await _otherTenantContext.SaveChangesAsync();
        _otherTenantContext.ChangeTracker.Clear();
    }

    private void Tombstone(ISoftDeletable row)
    {
        row.DeletedAt = DateTime.UtcNow;
        _otherTenantContext.Entry(row).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = true;
    }

    private async Task TombstoneHeartRateAsync(bool byUser)
    {
        var row = _context.HeartRates.Single();
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = byUser;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private const long XDripMills = 1_700_000_000_000;
    private const string XDripSyncKey = "steps-total:1700000000000";
    private const string XDripHeartRateSyncKey = "hr-bpm:1700000000000";

    private async Task SeedXDripHeartRateTombstoneAsync(bool byUser)
    {
        await _decomposer.DecomposeBatchAsync([XDripHeartRate()], WriteOrigin.Live);
        var row = _context.HeartRates.Single(h => h.SyncIdentifier == XDripHeartRateSyncKey);
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = byUser;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Activity XDripHeartRate() => new()
    {
        Type = "hr-bpm",
        Mills = XDripMills,
        AdditionalProperties = new Dictionary<string, object> { ["bpm"] = 72 },
    };

    private async Task SeedXDripStepsTombstoneAsync(bool byUser)
    {
        await _decomposer.DecomposeBatchAsync([XDripSteps()], WriteOrigin.Live);
        var row = _context.StepCounts.Single(s => s.SyncIdentifier == XDripSyncKey);
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = byUser;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Activity XDripSteps() => new()
    {
        Type = "steps-total",
        Mills = XDripMills,
        AdditionalProperties = new Dictionary<string, object> { ["steps"] = 1200 },
    };

    /// <summary>Stores the heart rate through the decomposer, so it carries the primary key a re-upload derives, then soft-deletes it.</summary>
    private async Task SeedTombstoneAsync(string originalId, bool byUser)
    {
        await _decomposer.DecomposeBatchAsync([HeartRate(originalId)], WriteOrigin.Live);
        var row = _context.HeartRates.Single(h => h.OriginalId == originalId);
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = byUser;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Activity HeartRate(string id) => new()
    {
        Id = id,
        Mills = 1_700_000_000_000,
        AdditionalProperties = new Dictionary<string, object> { ["bpm"] = 72 },
    };

    private static Activity StepCount(string id) => new()
    {
        Id = id,
        Mills = 1_700_000_000_000,
        AdditionalProperties = new Dictionary<string, object> { ["metric"] = 400 },
    };
}
