using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A heart rate or step count the user deleted stays deleted: the v1 activity delete leaves a
/// tombstone, and a later decomposition of the same activity id does not write the record again.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityDecomposerTombstoneTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly ActivityDecomposer _decomposer;

    public ActivityDecomposerTombstoneTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TenantId;
        _decomposer = new ActivityDecomposer(
            _context, Mock.Of<IStateSpanRepository>(), NullLogger<ActivityDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
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
    public async Task DecomposeAsync_DoesNotRecreateAHeartRateTheUserDeleted()
    {
        await SeedTombstoneAsync("a0000000000000000000000a", byUser: true);

        var result = await _decomposer.DecomposeAsync(HeartRate("a0000000000000000000000a"), WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().BeEmpty();
        _context.HeartRates.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task DecomposeAsync_RevivesAHeartRateTheSystemSweptInPlace()
    {
        await SeedTombstoneAsync("a0000000000000000000000a", byUser: false);

        await _decomposer.DecomposeAsync(HeartRate("a0000000000000000000000a"), WriteOrigin.Live);

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
        _context.Entry(steps).Property("DeletedByUser").CurrentValue = true;
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
    public async Task DecomposeAsync_DoesNotRecreateAnXDripStepCountTheUserDeleted()
    {
        await SeedXDripStepsTombstoneAsync(byUser: true);

        var result = await _decomposer.DecomposeAsync(XDripSteps(), WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.StepCounts.Should().BeEmpty();
        _context.StepCounts.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task DecomposeAsync_WritesAnXDripStepCountBesideASystemSweep()
    {
        await SeedXDripStepsTombstoneAsync(byUser: false);

        var result = await _decomposer.DecomposeAsync(XDripSteps(), WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(0);
        result.CreatedRecords.Should().ContainSingle();
        _context.ChangeTracker.Clear();
        _context.StepCounts.Should().ContainSingle().Which.SyncIdentifier.Should().Be(XDripSyncKey);
        _context.StepCounts.IgnoreQueryFilters().Should().HaveCount(2);
    }

    [Fact]
    public async Task DecomposeBatchAsync_SkipsAnXDripStepCountTheUserDeleted()
    {
        await SeedXDripStepsTombstoneAsync(byUser: true);

        var result = await _decomposer.DecomposeBatchAsync([XDripSteps()], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.StepCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeBatchAsync_WritesAnXDripStepCountBesideASystemSweep()
    {
        await SeedXDripStepsTombstoneAsync(byUser: false);

        var result = await _decomposer.DecomposeBatchAsync([XDripSteps()], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(0);
        result.CreatedRecords.Should().ContainSingle();
        _context.ChangeTracker.Clear();
        _context.StepCounts.Should().ContainSingle();
        _context.StepCounts.IgnoreQueryFilters().Should().HaveCount(2);
    }

    [Fact]
    public async Task DecomposeAsync_DoesNotRecreateAnXDripHeartRateTheUserDeleted()
    {
        await SeedXDripHeartRateTombstoneAsync(byUser: true);

        var result = await _decomposer.DecomposeAsync(XDripHeartRate(), WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().BeEmpty();
        _context.HeartRates.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task DecomposeAsync_WritesAnXDripHeartRateBesideASystemSweep()
    {
        await SeedXDripHeartRateTombstoneAsync(byUser: false);

        var result = await _decomposer.DecomposeAsync(XDripHeartRate(), WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(0);
        result.CreatedRecords.Should().ContainSingle();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().ContainSingle().Which.SyncIdentifier.Should().Be(XDripHeartRateSyncKey);
        _context.HeartRates.IgnoreQueryFilters().Should().HaveCount(2);
    }

    [Fact]
    public async Task DecomposeBatchAsync_SkipsAnXDripHeartRateTheUserDeleted()
    {
        await SeedXDripHeartRateTombstoneAsync(byUser: true);

        var result = await _decomposer.DecomposeBatchAsync([XDripHeartRate()], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeBatchAsync_WritesAnXDripHeartRateBesideASystemSweep()
    {
        await SeedXDripHeartRateTombstoneAsync(byUser: false);

        var result = await _decomposer.DecomposeBatchAsync([XDripHeartRate()], WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(0);
        result.CreatedRecords.Should().ContainSingle();
        _context.ChangeTracker.Clear();
        _context.HeartRates.Should().ContainSingle();
        _context.HeartRates.IgnoreQueryFilters().Should().HaveCount(2);
    }

    private const long XDripMills = 1_700_000_000_000;
    private const string XDripSyncKey = "steps-total:1700000000000";
    private const string XDripHeartRateSyncKey = "hr-bpm:1700000000000";

    private async Task SeedXDripHeartRateTombstoneAsync(bool byUser)
    {
        await _decomposer.DecomposeAsync(XDripHeartRate(), WriteOrigin.Live);
        var row = _context.HeartRates.Single(h => h.SyncIdentifier == XDripHeartRateSyncKey);
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property("DeletedByUser").CurrentValue = byUser;
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
        await _decomposer.DecomposeAsync(XDripSteps(), WriteOrigin.Live);
        var row = _context.StepCounts.Single(s => s.SyncIdentifier == XDripSyncKey);
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property("DeletedByUser").CurrentValue = byUser;
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
        await _decomposer.DecomposeAsync(HeartRate(originalId), WriteOrigin.Live);
        var row = _context.HeartRates.Single(h => h.OriginalId == originalId);
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property("DeletedByUser").CurrentValue = byUser;
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
