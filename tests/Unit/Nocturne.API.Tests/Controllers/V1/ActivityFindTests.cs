using Nocturne.API.Services.Health;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Helpers;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Pins the find, default window and count semantics of v1 <c>GET /activity</c> against legacy
/// Nightscout's <c>activity.list</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityFindTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IActivityService> _service = new();
    private string? _observedFind;
    private int? _observedCount;

    public ActivityFindTests()
    {
        _service
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, int? count, int? _, CancellationToken _) =>
            {
                _observedFind = find;
                _observedCount = count;
            })
            .ReturnsAsync(Array.Empty<Activity>());
    }

    [Fact]
    public async Task CreatedAtBounds_ReachTheService()
    {
        await Get("?find[created_at][$gte]=2026-09-20&find[created_at][$lte]=2026-09-20T23:59:59Z");

        var query = FindQuery.Parse(_observedFind);
        query.FromMills.Should().Be(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        query.ToMills.Should().Be(new DateTimeOffset(2026, 9, 20, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task FieldFilter_ReachesTheServiceAlongsideTheDefaultWindow()
    {
        await Get("?find[type][$eq]=Run");

        var query = FindQuery.Parse(_observedFind);
        query.GetEqualityValue("type").Should().Be("Run");
        query.FromMills.Should().Be(Now.AddDays(-4).ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?count=5")]
    public async Task NoDateBound_AppliesTheDefaultFourDayWindow(string queryString)
    {
        await Get(queryString);

        var query = FindQuery.Parse(_observedFind);
        query.FromMills.Should().Be(Now.AddDays(-4).ToUnixTimeMilliseconds());
        query.ToMills.Should().BeNull();
    }

    [Fact]
    public async Task DateStringBound_SuppressesTheDefaultWindow()
    {
        await Get("?find[dateString][$gte]=2026-01-01");

        FindQuery.Parse(_observedFind).FromMills
            .Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task NoCount_IsLimitedOnlyByTheMergedCap()
    {
        await Get("");

        _observedCount.Should().Be(LegacyReadLimits.MaxMergedCount);
    }

    [Fact]
    public async Task ExplicitCount_Limits()
    {
        await Get("?count=7", count: 7);

        _observedCount.Should().Be(7);
    }

    private async Task Get(string queryString, int? count = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string> { Scope.TreatmentsRead };
        var controller = new ActivityController(
            _service.Object,
            Mock.Of<IActivityDecomposer>(),
            Mock.Of<ILogger<ActivityController>>(),
            new FakeTimeProvider(Now))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.GetActivities(count: count);
        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
