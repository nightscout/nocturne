using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Services.V4;

public class ActivityDecomposerBatchTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<IStateSpanRepository> _stateSpanRepoMock;
    private readonly ActivityDecomposer _decomposer;

    public ActivityDecomposerBatchTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        _stateSpanRepoMock = new Mock<IStateSpanRepository>();
        _stateSpanRepoMock
            .Setup(x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<StateSpan> spans, CancellationToken _) => spans);

        _decomposer = new ActivityDecomposer(
            _context,
            _stateSpanRepoMock.Object,
            NullLogger<ActivityDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DecomposeBatchAsync_RoutesHeartRateToRepo()
    {
        // Arrange
        var activities = new List<Activity>
        {
            CreateHeartRateActivity("hr1", 72),
            CreateHeartRateActivity("hr2", 85),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(activities);

        // Assert - heart rates stored via DbContext
        _context.HeartRates.Should().HaveCount(2);
        result.CreatedRecords.Should().HaveCount(2);
        result.CorrelationId.Should().NotBeNull();

        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DecomposeBatchAsync_RoutesStepCountToRepo()
    {
        // Arrange
        var activities = new List<Activity>
        {
            CreateStepCountActivity("sc1", 1500),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(activities);

        // Assert - step counts stored via DbContext
        _context.StepCounts.Should().HaveCount(1);
        result.CreatedRecords.Should().HaveCount(1);

        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DecomposeBatchAsync_RoutesRegularActivityToStateSpans()
    {
        // Arrange
        var activities = new List<Activity>
        {
            CreateRegularActivity("ex1", "exercise"),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(activities);

        // Assert
        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.Is<IEnumerable<StateSpan>>(spans => spans.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _context.HeartRates.Should().BeEmpty();
        _context.StepCounts.Should().BeEmpty();
        result.CreatedRecords.Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeBatchAsync_EmptyBatch_NoRepositoryCalls()
    {
        // Act
        var result = await _decomposer.DecomposeBatchAsync([]);

        // Assert
        _context.HeartRates.Should().BeEmpty();
        _context.StepCounts.Should().BeEmpty();
        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_MixedTypes()
    {
        // Arrange - one of each type
        var activities = new List<Activity>
        {
            CreateHeartRateActivity("hr1", 72),
            CreateStepCountActivity("sc1", 3000),
            CreateRegularActivity("ex1", "exercise"),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(activities);

        // Assert - correct routing
        _context.HeartRates.Should().HaveCount(1);
        _context.StepCounts.Should().HaveCount(1);

        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.Is<IEnumerable<StateSpan>>(spans => spans.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(3);
        result.CorrelationId.Should().NotBeNull();

        // Verify decomposition batch was persisted
        var batch = _context.DecompositionBatches.SingleOrDefault(b => b.Id == result.CorrelationId);
        batch.Should().NotBeNull();
        batch!.Source.Should().Be("activity_decomposer_batch");
    }

    #region Helpers

    private static Activity CreateHeartRateActivity(string id, int bpm)
    {
        return new Activity
        {
            Id = id,
            Mills = 1700000000000,
            EnteredBy = "test",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["bpm"] = bpm,
                ["accuracy"] = 1,
            },
        };
    }

    private static Activity CreateStepCountActivity(string id, int metric)
    {
        return new Activity
        {
            Id = id,
            Mills = 1700000000000,
            EnteredBy = "test",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["metric"] = metric,
                ["source"] = 1,
            },
        };
    }

    private static Activity CreateRegularActivity(string id, string type)
    {
        return new Activity
        {
            Id = id,
            Mills = 1700000000000,
            Type = type,
            EnteredBy = "test",
        };
    }

    #endregion
}
