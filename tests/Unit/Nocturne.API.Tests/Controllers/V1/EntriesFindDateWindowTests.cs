using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Helpers;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Pins legacy Nightscout's 4-day <c>date</c> default on v1 entries reads that name no
/// <c>date</c>, <c>dateString</c> or <c>_id</c> (<c>lib/server/query.js</c>).
/// </summary>
[Trait("Category", "Unit")]
public class EntriesFindDateWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly long WindowStartMills = Now.AddDays(-4).ToUnixTimeMilliseconds();

    [Theory]
    [InlineData("")]
    [InlineData("count=5")]
    [InlineData("find[type]=sgv&count=5")]
    [InlineData("find[sgv][$gte]=180")]
    [InlineData("find[device]=xDrip&find[dateStringX]=1")]
    public async Task GetEntries_WithNoDateConstraint_ReadsOnlyTheLastFourDays(string queryString)
    {
        var (controller, query) = NewController(queryString);

        await controller.GetEntries(count: 5);

        FindQuery.Parse(query().Find).FromMills.Should().Be(WindowStartMills);
    }

    [Fact]
    public async Task GetEntries_WithNoQueryAtAll_ReadsOnlyTheLastFourDays()
    {
        var (controller, query) = NewController("");

        await controller.GetEntries();

        FindQuery.Parse(query().Find).FromMills.Should().Be(WindowStartMills);
    }

    [Fact]
    public async Task GetEntries_WithAFindParameterAndNoDateConstraint_ReadsOnlyTheLastFourDays()
    {
        var (controller, query) = NewController("");

        await controller.GetEntries(find: "{\"sgv\":{\"$gte\":180}}");

        var parsed = FindQuery.Parse(query().Find);
        parsed.FromMills.Should().Be(WindowStartMills);
        parsed.HasTopLevelCondition("sgv").Should().BeTrue();
    }

    [Theory]
    [InlineData("find[date][$gte]=1700000000000")]
    [InlineData("find[date][$lte]=1700000000000")]
    [InlineData("find[date]=1700000000000")]
    [InlineData("find[dateString][$gte]=2023-11-14")]
    [InlineData("find[_id]=507f1f77bcf86cd799439011")]
    public async Task GetEntries_WithADateOrIdCondition_AppliesNoWindow(string queryString)
    {
        var (controller, query) = NewController(queryString);

        await controller.GetEntries();

        query().Find.Should().Be(queryString);
    }

    [Fact]
    public async Task GetEntries_WithAnOlderDateBound_ReturnsTheBoundNotTheWindow()
    {
        var (controller, query) = NewController("find[date][$gte]=1700000000000");

        await controller.GetEntries();

        FindQuery.Parse(query().Find).FromMills.Should().Be(1700000000000);
    }

    [Fact]
    public async Task GetEntries_WithADateStringParameter_AppliesNoWindow()
    {
        var (controller, query) = NewController("dateString=2023-11-14");

        await controller.GetEntries(dateString: "2023-11-14");

        query().Find.Should().BeNull();
    }

    [Theory]
    [InlineData("sgv")]
    [InlineData("mbg")]
    [InlineData("cal")]
    public async Task GetEntry_TypeSpecWithNoDateConstraint_ReadsOnlyTheLastFourDays(string spec)
    {
        var (controller, query) = NewController("count=5");

        await controller.GetEntry(spec, count: 5);

        query().Type.Should().Be(spec);
        FindQuery.Parse(query().Find).FromMills.Should().Be(WindowStartMills);
    }

    [Theory]
    [InlineData("find[date][$gte]=1700000000000")]
    [InlineData("find[dateString][$gte]=2023-11-14")]
    [InlineData("find[_id]=507f1f77bcf86cd799439011")]
    public async Task GetEntry_TypeSpecWithADateOrIdCondition_AppliesNoWindow(string queryString)
    {
        var (controller, query) = NewController(queryString);

        await controller.GetEntry("sgv");

        query().Find.Should().Be(queryString);
    }

    [Theory]
    [InlineData("date", "find[created_at][$gte]=1", true)]
    [InlineData("date", "find[date][$gte]=1", false)]
    [InlineData("created_at", "find[date][$gte]=1", true)]
    [InlineData("created_at", "find[created_at][$gte]=1", false)]
    [InlineData("date", "find[dateString]=x", false)]
    [InlineData("date", "find[_id]=x", false)]
    public void Apply_OnlyAConditionOnItsOwnDateFieldDisablesTheWindow(
        string dateField, string find, bool windowed)
    {
        var result = LegacyFindDateWindow.Apply(find, Now, dateField, windowUnfiltered: true);

        (result != find).Should().Be(windowed);
        if (windowed)
            FindQuery.Parse(result).HasTopLevelCondition(dateField).Should().BeTrue();
    }

    [Fact]
    public void Apply_NamesTheGivenDateFieldInEveryWireForm()
    {
        LegacyFindDateWindow.Apply(null, Now, "date", windowUnfiltered: true)
            .Should().StartWith("find[date][$gte]=");
        LegacyFindDateWindow.Apply("a=b", Now, "date", windowUnfiltered: true)
            .Should().Contain("&find[date][$gte]=");
        LegacyFindDateWindow.Apply("{\"sgv\":1}", Now, "date", windowUnfiltered: true)
            .Should().Contain("\"date\":{\"$gte\":");
    }

    private static (EntriesController Controller, Func<EntryQuery> Query) NewController(string queryString)
    {
        EntryQuery? observed = null;
        var service = new Mock<IEntryService>();
        service
            .Setup(x => x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .Callback((EntryQuery query, CancellationToken _) => observed = query)
            .ReturnsAsync(Array.Empty<Entry>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString.Length == 0 ? "" : "?" + queryString);
        var controller = new EntriesController(
            service.Object,
            Mock.Of<IDocumentProcessingService>(),
            Mock.Of<ICanonicalAlertEvaluator>(),
            new FakeTimeProvider(Now),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (controller, () => observed ?? throw new InvalidOperationException("No query reached the service."));
    }
}
