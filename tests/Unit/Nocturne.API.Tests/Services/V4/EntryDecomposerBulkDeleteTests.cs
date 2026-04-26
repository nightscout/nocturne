using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.V4;

public class EntryDecomposerBulkDeleteTests : IDisposable
{
    private readonly Mock<ISensorGlucoseRepository> _sgRepo = new();
    private readonly Mock<IMeterGlucoseRepository> _mgRepo = new();
    private readonly Mock<ICalibrationRepository> _calRepo = new();
    private readonly Mock<ILogger<EntryDecomposer>> _logger = new();
    private readonly NocturneDbContext _dbContext;

    public EntryDecomposerBulkDeleteTests()
    {
        _dbContext = TestDbContextFactory.CreateInMemoryContext();
        _dbContext.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private EntryDecomposer CreateDecomposer() =>
        new(_dbContext, _sgRepo.Object, _mgRepo.Object, _calRepo.Object, new Mock<IGlucoseProcessingResolver>().Object, _logger.Object);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_WithTimeRange_DeletesAcrossAllRepos()
    {
        var find = "{\"mills\":{\"$gte\":1704067200000,\"$lte\":1704153600000}}";
        _sgRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(10);
        _mgRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);
        _calRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync(find);

        count.Should().Be(13);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_WithNullFind_DeletesAll()
    {
        _sgRepo.Setup(r => r.DeleteByTimeRangeAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(50);
        _mgRepo.Setup(r => r.DeleteByTimeRangeAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(5);
        _calRepo.Setup(r => r.DeleteByTimeRangeAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync(null);

        count.Should().Be(55);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_WithEmptyFind_DeletesAll()
    {
        _sgRepo.Setup(r => r.DeleteByTimeRangeAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(100);
        _mgRepo.Setup(r => r.DeleteByTimeRangeAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _calRepo.Setup(r => r.DeleteByTimeRangeAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync("{}");

        count.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_WithNonTimeFilter_ReturnsZero()
    {
        // {"sgv":{"$gte":180}} is the real-world dangerous case: ParseTimeRangeFromFind
        // extracts $gte from any field, yielding from=180 (nonsensical as a timestamp).
        // The plausibility guard rejects values below year 2000 in millis.
        var find = "{\"sgv\":{\"$gte\":180}}";

        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync(find);

        count.Should().Be(0);
        _sgRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_WithNonTimeFieldOperator_ReturnsZero()
    {
        // {"sgv":{"$gte":180}} would parse from=180 (not a real timestamp)
        // The plausibility guard should reject this
        var find = "{\"sgv\":{\"$gte\":180}}";
        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync(find);
        count.Should().Be(0);
        _sgRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_WithPartialTimeRange_OnlyGte_Deletes()
    {
        var find = "{\"mills\":{\"$gte\":1704067200000}}";
        _sgRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(5);
        _mgRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _calRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync(find);

        count.Should().Be(5);
    }
}
