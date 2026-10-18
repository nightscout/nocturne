using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Core.Contracts.V4;

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
        new(_sgRepo.Object, _mgRepo.Object, _calRepo.Object, new Mock<IGlucoseProcessingResolver>().Object, Mock.Of<IPatientDeviceStamper>(), Mock.Of<IAuditContext>(), _logger.Object);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_WithTimeRange_DeletesAcrossAllRepos()
    {
        var find = "{\"mills\":{\"$gte\":1704067200000,\"$lte\":1704153600000}}";
        _sgRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(10);
        _mgRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);
        _calRepo.Setup(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync(find, WriteOrigin.Live);

        count.Should().Be(13);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData(" { } ")]
    [InlineData("token=x")]
    [InlineData("count=5&token=x")]
    [InlineData("not a find")]
    [InlineData("{\"mills\":")]
    public async Task BulkDeleteAsync_WithAFindThatHasNoConditions_DeletesNothing(string? find)
    {
        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync(find, WriteOrigin.Live);

        count.Should().Be(0);
        _sgRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _mgRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _calRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_WithATypeEquality_DeletesOnlyThatType()
    {
        _sgRepo.Setup(r => r.DeleteByTimeRangeAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(7);

        var decomposer = CreateDecomposer();
        var count = await decomposer.BulkDeleteAsync("find[type]=sgv", WriteOrigin.Live);

        count.Should().Be(7);
        _mgRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _calRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("find[]=")]
    [InlineData("{\"$and\":[]}")]
    [InlineData("{\"date\":{}}")]
    [InlineData("{}")]
    public async Task BulkDeleteAsync_WhenTheFindHasNoConditions_RefusesAtTheEmptyGuard(string find)
    {
        var count = await CreateDecomposer().BulkDeleteAsync(find, WriteOrigin.Live);

        count.Should().Be(0);
        _logger.Invocations
            .Where(i => i.Method.Name == nameof(ILogger.Log))
            .Select(i => i.Arguments[2].ToString())
            .Should().Contain(m => m!.Contains("has no conditions"));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("find[type]=other")]
    [InlineData("find[type]=sgv%0D%0Aforged-line")]
    [InlineData("find[type]=SGV&find[date][$gte]=1704067200000")]
    public async Task BulkDeleteAsync_WithAnUnknownType_DeletesNothingAndLogsNoClientText(string find)
    {
        var count = await CreateDecomposer().BulkDeleteAsync(find, WriteOrigin.Live);

        count.Should().Be(0);
        _sgRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _mgRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _calRepo.Verify(r => r.DeleteByTimeRangeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _logger.Invocations
            .Where(i => i.Method.Name == nameof(ILogger.Log))
            .Select(i => i.Arguments[2].ToString())
            .Should().NotContain(m => m!.Contains("forged-line") || m.Contains('\r') || m.Contains('\n') || m.Contains("other"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BulkDeleteAsync_NeverLogsTheRawFind()
    {
        var find = "find[type]=sgv&token=secret-value&find[date][$gte]=1704067200000";

        await CreateDecomposer().BulkDeleteAsync(find, WriteOrigin.Live);
        await CreateDecomposer().BulkDeleteAsync("token=secret-value", WriteOrigin.Live);
        await CreateDecomposer().BulkDeleteAsync("find[sgv][$gte]=180&token=secret-value", WriteOrigin.Live);

        _logger.Invocations
            .Where(i => i.Method.Name == nameof(ILogger.Log))
            .Select(i => i.Arguments[2].ToString())
            .Should().NotContain(m => m!.Contains("secret-value"));
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
        var count = await decomposer.BulkDeleteAsync(find, WriteOrigin.Live);

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
        var count = await decomposer.BulkDeleteAsync(find, WriteOrigin.Live);
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
        var count = await decomposer.BulkDeleteAsync(find, WriteOrigin.Live);

        count.Should().Be(5);
    }
}
