using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Tests for <see cref="TreatmentReadService"/> find-query filtering: field filters
/// (eventType, enteredBy, $exists, $and groups) applied over the projected legacy shape,
/// with time bounds pushed down to the projection query.
/// </summary>
public class TreatmentReadServiceFindFilterTests
{
    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly TreatmentReadService _service;

    public TreatmentReadServiceFindFilterTests()
    {
        _service = new TreatmentReadService(
            _projection.Object,
            new Mock<ITreatmentDecomposer>().Object,
            new Mock<IDecompositionPipeline>().Object,
            new Mock<ITempBasalRepository>().Object,
            new Mock<IBolusRepository>().Object,
            new Mock<ICarbIntakeRepository>().Object,
            new Mock<IBGCheckRepository>().Object,
            new Mock<INoteRepository>().Object,
            new Mock<IDeviceEventRepository>().Object,
            new Mock<IBolusCalculationRepository>().Object,
            NullLogger<TreatmentReadService>.Instance);
    }

    private void SetupProjection(params Treatment[] treatments)
    {
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(
                It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);
    }

    [Fact]
    public async Task QueryAsync_EventTypeFilter_ReturnsOnlyMatchingType()
    {
        // LoopFollow CAGE: find[eventType]=Site Change&count=1 must return the newest
        // Site Change even when newer treatments of other types exist
        SetupProjection(
            new Treatment { Id = "1", Mills = 5000, EventType = "Correction Bolus" },
            new Treatment { Id = "2", Mills = 4000, EventType = "Site Change" },
            new Treatment { Id = "3", Mills = 3000, EventType = "Carb Correction" },
            new Treatment { Id = "4", Mills = 2000, EventType = "Site Change" });

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[eventType]=Site Change",
            Count = 1,
        });

        result.Should().ContainSingle().Which.Id.Should().Be("2");
    }

    [Fact]
    public async Task QueryAsync_AndGroupsOfNe_ExcludeOwnUploads()
    {
        // Trio's external-treatments import excludes its own records with chained $ne clauses
        SetupProjection(
            new Treatment { Id = "1", Mills = 3000, EnteredBy = "Trio", EventType = "Meal Bolus" },
            new Treatment { Id = "2", Mills = 2000, EnteredBy = "xdrip", EventType = "Meal Bolus" },
            new Treatment { Id = "3", Mills = 1000, EventType = "Note" });

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[$and][0][enteredBy][$ne]=Trio&find[$and][1][eventType][$ne]=Temp Basal",
            Count = 10,
        });

        result.Select(t => t.Id).Should().Equal("2", "3");
    }

    [Fact]
    public async Task QueryAsync_ExistsFilter_MatchesPresentField()
    {
        SetupProjection(
            new Treatment { Id = "1", Mills = 3000, Carbs = 20, EventType = "Carb Correction" },
            new Treatment { Id = "2", Mills = 2000, Insulin = 1.5, EventType = "Correction Bolus" });

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[carbs][$exists]=true",
            Count = 10,
        });

        result.Should().ContainSingle().Which.Id.Should().Be("1");
    }

    [Fact]
    public async Task QueryAsync_TimeOnlyFind_PushesBoundsWithoutInMemoryFiltering()
    {
        var from = 1672531200000; // 2023-01-01T00:00:00Z
        var to = 1672617600000;   // 2023-01-02T00:00:00Z
        SetupProjection(new Treatment { Id = "1", Mills = from + 1000 });

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[created_at][$gte]=2023-01-01T00:00:00.000Z&find[created_at][$lte]=2023-01-02T00:00:00.000Z",
            Count = 10,
        });

        result.Should().ContainSingle();
        _projection.Verify(
            p => p.GetProjectedTreatmentsAsync(from, to, 10, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task QueryAsync_FieldFilterWithTimeRange_PushesBoundsAndFilters()
    {
        var from = 1672531200000;
        SetupProjection(
            new Treatment { Id = "1", Mills = from + 2000, EventType = "Note" },
            new Treatment { Id = "2", Mills = from + 1000, EventType = "Site Change" });

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[eventType]=Note&find[created_at][$gte]=2023-01-01T00:00:00.000Z",
            Count = 10,
        });

        result.Should().ContainSingle().Which.Id.Should().Be("1");
        _projection.Verify(
            p => p.GetProjectedTreatmentsAsync(from, null, It.IsAny<int>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task QueryAsync_FilteredPageUnderfilled_GrowsFetchWindow()
    {
        // First window (needed*4 = 8, floored to 100) returns 100 rows with too few matches and
        // doesn't exhaust the store, so the service must refetch with a larger window.
        var firstPage = Enumerable.Range(0, 100)
            .Select(i => new Treatment { Id = $"n{i}", Mills = 100_000 - i, EventType = "Note" })
            .ToList();
        firstPage[99] = new Treatment { Id = "match-1", Mills = 100_000 - 99, EventType = "Site Change" };

        var secondPage = firstPage
            .Append(new Treatment { Id = "match-2", Mills = 50, EventType = "Site Change" })
            .ToList();

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, 100, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstPage);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, 400, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondPage);

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[eventType]=Site Change",
            Count = 2,
        });

        result.Select(t => t.Id).Should().Equal("match-1", "match-2");
        _projection.Verify(
            p => p.GetProjectedTreatmentsAsync(null, null, 400, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CountAsync_WithFieldFilter_CountsOnlyMatches()
    {
        SetupProjection(
            new Treatment { Id = "1", Mills = 3000, EventType = "Sensor Change" },
            new Treatment { Id = "2", Mills = 2000, EventType = "Note" },
            new Treatment { Id = "3", Mills = 1000, EventType = "Sensor Change" });

        var count = await _service.CountAsync("find[eventType]=Sensor Change");

        count.Should().Be(2);
    }
}
