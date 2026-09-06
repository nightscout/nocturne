using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data;
using Nocturne.Core.Contracts.V4;

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
        var result = await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);

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
        var result = await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);

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
        var result = await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);

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
        var result = await _decomposer.DecomposeBatchAsync([], WriteOrigin.Live);

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
        var result = await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);

        // Assert - correct routing
        _context.HeartRates.Should().HaveCount(1);
        _context.StepCounts.Should().HaveCount(1);

        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.Is<IEnumerable<StateSpan>>(spans => spans.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(3);

        // All records produced in one decompose share a single non-empty correlation id
        result.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    #region RequiredWriteScope

    [Fact]
    public void RequiredWriteScope_HeartRate_ReturnsHeartRateReadWrite()
    {
        _decomposer.RequiredWriteScope(CreateHeartRateActivity("hr", 72))
            .Should().Be(Scope.HeartRateReadWrite);
    }

    [Fact]
    public void RequiredWriteScope_StepCount_ReturnsStepCountReadWrite()
    {
        _decomposer.RequiredWriteScope(CreateStepCountActivity("sc", 1500))
            .Should().Be(Scope.StepCountReadWrite);
    }

    [Theory]
    [InlineData("sleep")]
    [InlineData("nap")]
    [InlineData("Sleep")]
    public void RequiredWriteScope_SleepType_ReturnsSleepReadWrite(string type)
    {
        _decomposer.RequiredWriteScope(CreateRegularActivity("s", type))
            .Should().Be(Scope.SleepReadWrite);
    }

    [Theory]
    [InlineData("exercise")]
    [InlineData("running")]
    [InlineData("illness")]
    [InlineData("travel")]
    [InlineData("restaurant")] // contains "rest" but is not an exact sleep type
    public void RequiredWriteScope_RegularActivity_ReturnsNull(string type)
    {
        _decomposer.RequiredWriteScope(CreateRegularActivity("r", type))
            .Should().BeNull();
    }

    #endregion

    #region RequiredReadScope

    [Fact]
    public void RequiredReadScope_HeartRate_ReturnsHeartRateRead()
    {
        _decomposer.RequiredReadScope(CreateHeartRateActivity("hr", 72))
            .Should().Be(Scope.HeartRateRead);
    }

    [Fact]
    public void RequiredReadScope_StepCount_ReturnsStepCountRead()
    {
        _decomposer.RequiredReadScope(CreateStepCountActivity("sc", 1500))
            .Should().Be(Scope.StepCountRead);
    }

    [Theory]
    [InlineData("sleep")]
    [InlineData("nap")]
    [InlineData("Sleep")]
    public void RequiredReadScope_SleepType_ReturnsSleepRead(string type)
    {
        _decomposer.RequiredReadScope(CreateRegularActivity("s", type))
            .Should().Be(Scope.SleepRead);
    }

    /// <summary>
    /// Regular activities route to StateSpans, which the merged read serves under treatments. Unlike
    /// the write scope this is never null: every record in the merged response needs a category to
    /// be filtered on, so "no category" would mean "visible to anyone admitted".
    /// </summary>
    [Theory]
    [InlineData("exercise")]
    [InlineData("running")]
    [InlineData("illness")]
    [InlineData("travel")]
    [InlineData("restaurant")]
    public void RequiredReadScope_RegularActivity_ReturnsTreatmentsRead(string type)
    {
        _decomposer.RequiredReadScope(CreateRegularActivity("r", type))
            .Should().Be(Scope.TreatmentsRead);
    }

    /// <summary>
    /// The read scope must be the read counterpart of the write scope for the same record, so the
    /// read gate and the write gate cannot classify a record into different categories.
    /// </summary>
    [Fact]
    public void RequiredReadScope_IsTheReadCounterpartOfRequiredWriteScope()
    {
        foreach (var activity in new[]
                 {
                     CreateHeartRateActivity("hr", 72),
                     CreateStepCountActivity("sc", 1500),
                     CreateRegularActivity("s", "sleep"),
                 })
        {
            var writeScope = _decomposer.RequiredWriteScope(activity);
            _decomposer.RequiredReadScope(activity)
                .Should().Be(Scope.ImpliedReadScope(writeScope!));
        }
    }

    #endregion

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
