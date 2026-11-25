using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Glucose;
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
            new Mock<ITempBasalRepository>().Object,
            new Mock<IBolusRepository>().Object,
            new Mock<ICarbIntakeRepository>().Object,
            new Mock<IBGCheckRepository>().Object,
            new Mock<INoteRepository>().Object,
            new Mock<IDeviceEventRepository>().Object,
            new Mock<IBolusCalculationRepository>().Object,
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);
    }

    private void SetupProjection(params Treatment[] treatments)
    {
        _projection.SetupWindowRead(treatments, range => !range.IncludeDeleted);
    }

    private void SetupProjectionIncludingDeleted(params Treatment[] treatments)
    {
        _projection.SetupWindowRead(treatments, range => range.IncludeDeleted);
    }

    [Fact]
    public async Task QueryAsync_IsValidFalse_ReturnsTheDeletedTreatmentsAndNoLiveOne()
    {
        SetupProjectionIncludingDeleted(
            new Treatment { Id = "live", Mills = 3000, EventType = "Meal Bolus" },
            new Treatment { Id = "deleted", Mills = 2000, EventType = "Meal Bolus", IsValid = false },
            new Treatment { Id = "deleted-older", Mills = 1000, EventType = "Note", IsValid = false });

        var result = await _service.QueryAsync(new TreatmentQuery { Find = "find[isValid]=false", Count = 10 });

        result.Select(t => t.Id).Should().Equal("deleted", "deleted-older");
        result.Should().OnlyContain(t => t.IsValid == false);
    }

    [Fact]
    public async Task QueryAsync_IsValidFalse_PagesOverTheMatchingTreatments()
    {
        SetupProjectionIncludingDeleted(
            new Treatment { Id = "newest-deleted", Mills = 3000, IsValid = false },
            new Treatment { Id = "live", Mills = 2000 },
            new Treatment { Id = "older-deleted", Mills = 1000, IsValid = false });

        var result = await _service.QueryAsync(new TreatmentQuery { Find = "find[isValid]=false", Count = 1, Skip = 1 });

        result.Should().ContainSingle().Which.Id.Should().Be("older-deleted");
    }

    [Fact]
    public async Task QueryAsync_IsValidTrue_ReturnsNoDeletedTreatment()
    {
        SetupProjectionIncludingDeleted(
            new Treatment { Id = "deleted", Mills = 3000, IsValid = false },
            new Treatment { Id = "stored-valid", Mills = 2000, IsValid = true },
            new Treatment { Id = "unmarked", Mills = 1000 });

        var result = await _service.QueryAsync(new TreatmentQuery { Find = "find[isValid]=true", Count = 10 });

        result.Select(t => t.Id).Should().Equal("stored-valid");
    }

    [Fact]
    public async Task CountAsync_IsValidFalse_CountsTheDeletedTreatmentsTheListReturns()
    {
        SetupProjectionIncludingDeleted(
            new Treatment { Id = "live", Mills = 3000 },
            new Treatment { Id = "deleted", Mills = 2000, IsValid = false },
            new Treatment { Id = "deleted-older", Mills = 1000, IsValid = false });

        var count = await _service.CountAsync("find[isValid]=false");
        var listed = await _service.QueryAsync(new TreatmentQuery { Find = "find[isValid]=false", Count = 10 });

        count.Should().Be(2);
        listed.Should().HaveCount((int)count);
    }

    [Fact]
    public async Task QueryAsync_FindThatDoesNotNameIsValid_ReadsLiveTreatmentsOnly()
    {
        SetupProjection(new Treatment { Id = "live", Mills = 3000, EventType = "Meal Bolus" });

        var result = await _service.QueryAsync(new TreatmentQuery { Find = "find[eventType]=Meal Bolus", Count = 10 });

        result.Select(t => t.Id).Should().Equal("live");
        _projection.VerifyWindowRead(range => range.IncludeDeleted, Times.Never());
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
    public async Task QueryAsync_EventTypeFilterAscending_ReturnsTheOldestMatchesOldestFirst()
    {
        var all = new[]
        {
            new Treatment { Id = "1", Mills = 5000, EventType = "Site Change" },
            new Treatment { Id = "2", Mills = 4000, EventType = "Correction Bolus" },
            new Treatment { Id = "3", Mills = 3000, EventType = "Site Change" },
            new Treatment { Id = "4", Mills = 2000, EventType = "Site Change" },
        };
        _projection.SetupWindowRead(
            range => all.OrderBy(t => t.Mills).Take(range.Limit).ToList(),
            range => !range.Descending);

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[eventType]=Site Change",
            Count = 2,
            ReverseResults = true,
        });

        result.Select(t => t.Id).Should().Equal("4", "3");
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
        _projection.VerifyWindowRead(
            range => range.From == from.ToUtcDateTime() && range.To == to.ToUtcDateTime() && range.Limit == 10,
            Times.Once());
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
        _projection.VerifyWindowRead(
            range => range.From == from.ToUtcDateTime() && range.To is null,
            Times.Once());
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

        _projection.SetupWindowRead(firstPage, range => range is { From: null, To: null, Limit: 100 });
        _projection.SetupWindowRead(secondPage, range => range is { From: null, To: null, Limit: 400 });

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[eventType]=Site Change",
            Count = 2,
        });

        result.Select(t => t.Id).Should().Equal("match-1", "match-2");
        _projection.VerifyWindowRead(range => range is { From: null, To: null, Limit: 400 }, Times.Once());
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

    [Theory]
    [InlineData("find[eventType]=/bolus/i")]
    [InlineData("find[enteredBy]=/XDRIP/i")]
    [InlineData("find[notes]=/lunch/i")]
    public async Task QueryAsync_BareRegexLiteralOnATreatmentField_MatchesAsARegex(string find)
    {
        SetupProjection(
            new Treatment { Id = "1", Mills = 3000, EventType = "Meal Bolus", EnteredBy = "xdrip", Notes = "Lunch" },
            new Treatment { Id = "2", Mills = 2000, EventType = "Correction Bolus", EnteredBy = "xdrip-2", Notes = "lunch fix" },
            new Treatment { Id = "3", Mills = 1000, EventType = "Note", EnteredBy = "Loop", Notes = "walk" });

        var result = await _service.QueryAsync(new TreatmentQuery { Find = find, Count = 10 });

        result.Select(t => t.Id).Should().Equal("1", "2");
    }

    [Fact]
    public async Task QueryAsync_BareRegexLiteralOnAnotherField_StaysLiteral()
    {
        SetupProjection(
            new Treatment { Id = "1", Mills = 2000, EventType = "Meal Bolus", Insulin = 2 },
            new Treatment { Id = "2", Mills = 1000, EventType = "Note", Insulin = 3 });

        var result = await _service.QueryAsync(new TreatmentQuery { Find = "find[insulin]=/2/", Count = 10 });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CountAsync_BareRegexLiteralOnEventType_CountsRegexMatches()
    {
        SetupProjection(
            new Treatment { Id = "1", Mills = 3000, EventType = "Meal Bolus" },
            new Treatment { Id = "2", Mills = 2000, EventType = "Correction Bolus" },
            new Treatment { Id = "3", Mills = 1000, EventType = "Note" });

        var count = await _service.CountAsync("find[eventType]=/bolus/i");

        count.Should().Be(2);
    }
}
