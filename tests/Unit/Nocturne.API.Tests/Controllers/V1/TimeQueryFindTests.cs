using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Entries;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
using Xunit;
using TimePatternQuery = Nocturne.Core.Contracts.Legacy.TimePatternQuery;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Drives <see cref="TimeQueryController"/> with the query string a client sends, through the real
/// <see cref="TimeQueryService"/> and <see cref="BraceExpansionService"/>, against stores that apply
/// <see cref="FindQuery"/> as the read services do.
/// </summary>
public class TimeQueryFindTests
{
    private static readonly Entry[] Entries =
    [
        Sgv("a", "2015-04-02T13:05:00.000Z", 100),
        Sgv("b", "2015-04-02T14:05:00.000Z", 130),
        Sgv("c", "2015-04-20T09:00:00.000Z", 180),
        Sgv("d", "2015-05-02T13:05:00.000Z", 200),
        Sgv("e", "2014-04-02T13:05:00.000Z", 150),
        new() { Id = "f", Type = "mbg", DateString = "2015-04-02T13:30:00.000Z" },
    ];

    private static readonly Treatment[] Treatments =
    [
        Bolus("t1", "Bolus", "2015-04-02T13:05:00.000Z"),
        Bolus("t2", "Meal Bolus", "2015-04-03T13:05:00.000Z"),
        Bolus("t3", "Bolus", "2015-05-03T13:05:00.000Z"),
    ];

    [Theory]
    [InlineData("?token=x", "a,b,c,f")]
    [InlineData("?_=1700000000&secret=abc", "a,b,c,f")]
    [InlineData("?find[sgv][$gte]=120", "b,c")]
    [InlineData("?token=x&find[sgv][$gte]=120", "b,c")]
    [InlineData("?token=x&find[sgv][$gte]=120&find[sgv][$lt]=150", "b")]
    [InlineData("?count=2&format=json&find[sgv][$gte]=120", "b,c")]
    public async Task Times_AppliesOnlyFindKeys(string query, string expectedIds)
    {
        var controller = Build(query);

        var result = await controller.GetTimeBasedEntriesWithPrefix("2015-04", count: 100);

        IdsOf(result).Should().BeEquivalentTo(expectedIds.Split(','));
    }

    [Theory]
    [InlineData("?token=x", "a,b,e,f")]
    [InlineData("?token=x&find[sgv][$gte]=120", "b,e")]
    public async Task Times_RegexSegmentIsAndedWithFind(string query, string expectedIds)
    {
        var controller = Build(query);

        var result = await controller.GetTimeBasedEntriesWithPrefixAndRegex(
            "20{14..15}", "-04-02T{13..14}", count: 100);

        IdsOf(result).Should().BeEquivalentTo(expectedIds.Split(','));
    }

    [Theory]
    [InlineData("?token=x", "a,b,c")]
    [InlineData("?token=x&find[sgv][$gte]=120", "b,c")]
    [InlineData("?find[device][$eq]=none", "")]
    public async Task SliceEntries_AppliesOnlyFindKeys(string query, string expectedIds)
    {
        var controller = Build(query);

        var result = await controller.GetSlicedDataWithTypeAndPrefix(
            "entries", "dateString", "sgv", "2015-04", count: 100);

        IdsOf(result).Should().BeEquivalentTo(
            expectedIds.Length == 0 ? [] : expectedIds.Split(','));
    }

    [Theory]
    [InlineData("?token=x", "t1,t2,t3")]
    [InlineData("?token=x&find[eventType]=Bolus", "t1,t3")]
    [InlineData("?find[eventType][$regex]=Meal", "t2")]
    public async Task SliceTreatments_AppliesOnlyFindKeys(string query, string expectedIds)
    {
        var controller = Build(query);

        var result = await controller.GetSlicedData("treatments", "created_at", count: 100);

        IdsOf(result).Should().BeEquivalentTo(expectedIds.Split(','));
    }

    [Fact]
    public async Task Slice_TypeIsAndedWithPatternAndFind()
    {
        var controller = Build("?token=x&find[sgv][$gte]=120");

        var result = await controller.GetSlicedDataWithType("entries", "dateString", "sgv", count: 100);

        IdsOf(result).Should().BeEquivalentTo(["b", "c", "d", "e"]);
    }

    [Fact]
    public async Task Slice_TypeExcludesOtherTypes()
    {
        var controller = Build("?token=x");

        var result = await controller.GetSlicedDataWithTypeAndPrefix(
            "entries", "dateString", "mbg", "2015-04", count: 100);

        IdsOf(result).Should().BeEquivalentTo(["f"]);
    }

    [Fact]
    public async Task Times_SinglePrefixIsAndedWithTheRegexSegment()
    {
        var controller = Build("?token=x");

        var result = await controller.GetTimeBasedEntriesWithPrefixAndRegex(
            "2015-04", "T{13..14}:{00..15}", count: 100);

        IdsOf(result).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task Times_SinglePrefixRegexSegmentIsAndedWithFind()
    {
        var controller = Build("?token=x&find[sgv][$gte]=120");

        var result = await controller.GetTimeBasedEntriesWithPrefixAndRegex(
            "2015-04", "T{13..14}:{00..15}", count: 100);

        IdsOf(result).Should().BeEquivalentTo(["b"]);
    }

    [Fact]
    public async Task Slice_SinglePrefixIsAndedWithTheRegexSegment()
    {
        var controller = Build("?token=x");

        var result = await controller.GetSlicedDataWithAll(
            "entries", "dateString", "sgv", "2015-04", "T{13..14}:{00..15}", count: 100);

        IdsOf(result).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task Times_ConflictingFindOnThePatternFieldIsAnded()
    {
        var controller = Build("?find[dateString][$regex]=2015-05");

        var result = await controller.GetTimeBasedEntriesWithPrefix("2015-04", count: 100);

        IdsOf(result).Should().BeEmpty();
    }

    [Theory]
    [InlineData("dateString][$exists")]
    [InlineData("x]=1&find[sgv")]
    [InlineData("date String")]
    [InlineData("1abc")]
    [InlineData("a..b")]
    public async Task Slice_RefusesAFieldThatIsNotAFieldName(string field)
    {
        var controller = Build("?token=x");

        var result = await controller.GetSlicedData("entries", field, count: 100);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("created_at")]
    [InlineData("device.name")]
    public async Task Slice_AcceptsAFieldPath(string field)
    {
        var controller = Build("?token=x");

        var result = await controller.GetSlicedData("entries", field, count: 100);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task OnlyFindPairsReachTheReadService()
    {
        string? captured = null;
        var entries = new Mock<IEntryService>();
        entries
            .Setup(e => e.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<EntryQuery, CancellationToken>((query, _) => captured = query.Find)
            .ReturnsAsync([]);
        var controller = Build(
            "?token=x&secret=y&_=1&count=5&find[sgv][$gte]=120", entryService: entries.Object);

        await controller.GetTimeBasedEntriesWithPrefix("2015-04");

        captured.Should().NotBeNull();
        captured.Should().NotContain("token").And.NotContain("secret").And.NotContain("count");
        var parsed = FindQuery.Parse(captured);
        parsed.Matches(Sgv("m", "2015-04-02T13:05:00.000Z", 130)).Should().BeTrue();
        parsed.Matches(Sgv("n", "2015-04-02T13:05:00.000Z", 100)).Should().BeFalse();
    }

    [Fact]
    public async Task Times_RegexValueSurvivesTheWireForm()
    {
        string? captured = null;
        var braces = new Mock<IBraceExpansionService>();
        braces
            .Setup(b => b.PrepareTimePatterns(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns(new TimePatternQuery
            {
                CanOptimizeWithIndex = true,
                SingleRegexPattern = @"^2015-04-0[1-2]&x\+y%z\|\(a\)",
                InPatterns = [],
            });
        var entries = new Mock<IEntryService>();
        entries
            .Setup(e => e.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<EntryQuery, CancellationToken>((query, _) => captured = query.Find)
            .ReturnsAsync([]);
        var controller = Build("?token=x", braces.Object, entries.Object);

        await controller.GetTimeBasedEntriesWithPrefix("2015-04");

        var parsed = FindQuery.Parse(captured);
        parsed.Matches(Sgv("m", "2015-04-02&x+y%z|(a)", 100)).Should().BeTrue();
        parsed.Matches(Sgv("n", "2015-04-09&x+y%z|(a)", 100)).Should().BeFalse();
    }

    [Theory]
    [InlineData("20{14..15}", "-04-02T{13..14}", "?find[sgv][$gte]=120&token=x&secret=y&_=1&type=mbg&device=ignored", "b,e")]
    [InlineData("2015-04", null, "?find[dateString][$regex]=T13&find[dateString][$regex]=T14", "")]
    [InlineData("2015-04", null, "?find[type][$regex]=%2F%2Fsgv%2Fi%2F", "")]
    public async Task Echo_AppliedQueryMatchesTheNormalTimesQuery(
        string prefix, string? regex, string query, string expectedIds)
    {
        var controller = Build(query);

        ActionResult<TimeQueryEcho> result;
        if (regex == null)
        {
            result = controller.GetTimeQueryEchoWithPrefix(prefix);
        }
        else
        {
            result = controller.GetTimeQueryEchoWithPrefixAndRegex(prefix, regex);
        }

        var response = result.Result.Should().BeOfType<OkObjectResult>().Which;
        var echo = response.Value.Should().BeOfType<TimeQueryEcho>().Which;

        string echoJson = JsonSerializer.Serialize(echo.Query);
        var echoConditions = FindQuery.Parse(echoJson);
        string[] expectedEntryIds = Array.Empty<string>();
        if (expectedIds.Length != 0)
        {
            expectedEntryIds = expectedIds.Split(',');
        }
        Entries.Where(echoConditions.Matches).Select(e => e.Id).Should().BeEquivalentTo(expectedEntryIds);

        ActionResult normalResult;
        if (regex == null)
        {
            normalResult = await controller.GetTimeBasedEntriesWithPrefix(prefix, count: 100);
        }
        else
        {
            normalResult = await controller.GetTimeBasedEntriesWithPrefixAndRegex(prefix, regex, count: 100);
        }
        IdsOf(normalResult).Should().BeEquivalentTo(expectedEntryIds);
    }

    [Theory]
    [InlineData("{0..2147483647}", null)]
    [InlineData("{a,b}{c,d}{e,f}{g,h}{i,j}{k,l}{m,n}{o,p}{q,r}{s,t}", null)]
    [InlineData("2015-04", "(?=T13)T1")]
    [InlineData("2015-04", @"(T1)\1")]
    public async Task Times_RefusesAPatternItCannotEvaluate(string prefix, string? regex)
    {
        var controller = Build("?token=x");

        var times = regex is null
            ? await controller.GetTimeBasedEntriesWithPrefix(prefix, count: 100)
            : await controller.GetTimeBasedEntriesWithPrefixAndRegex(prefix, regex, count: 100);
        var slice = await controller.GetSlicedDataWithAll(
            "entries", "dateString", "sgv", prefix, regex ?? "T13", count: 100);
        var echo = controller.GetTimeQueryEchoWithPrefixAndRegex(prefix, regex ?? "T13");

        times.Should().BeOfType<BadRequestObjectResult>();
        slice.Should().BeOfType<BadRequestObjectResult>();
        echo.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static string[] IdsOf(ActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeAssignableTo<IEnumerable<Entry>>().Subject
            .Select(e => e.Id!)
            .ToArray();

    private static TimeQueryController Build(
        string query,
        IBraceExpansionService? braces = null,
        IEntryService? entryService = null)
    {
        var entries = entryService ?? FakeEntries();
        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(t => t.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TreatmentQuery query, CancellationToken _) =>
            {
                var parsed = FindQuery.Parse(query.Find);
                return Treatments.Where(parsed.Matches).ToList().AsEnumerable();
            });

        var deviceStatuses = new DeviceStatusProjectionService(
            Mock.Of<IDeviceStatusGroupRepository>(r => r.GetAsync(
                    It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())
                == Task.FromResult<IReadOnlyList<Nocturne.Core.Models.V4.DeviceStatusGroup>>(
                    Array.Empty<Nocturne.Core.Models.V4.DeviceStatusGroup>())),
            new Mock<IApsSnapshotRepository>().Object,
            new Mock<IPumpSnapshotRepository>().Object,
            new Mock<IUploaderSnapshotRepository>().Object,
            new Mock<IStateSpanRepository>().Object,
            new Mock<IDeviceStatusExtrasRepository>().Object,
            NullLogger<DeviceStatusProjectionService>.Instance);

        var service = new TimeQueryService(
            entries,
            treatments.Object,
            deviceStatuses,
            braces ?? new BraceExpansionService(),
            NullLogger<TimeQueryService>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = Scope.Normalize([Scope.FullAccess]);
        httpContext.Request.QueryString = new QueryString(query);

        return new TimeQueryController(service, NullLogger<TimeQueryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static IEntryService FakeEntries()
    {
        var entries = new Mock<IEntryService>();
        entries
            .Setup(e => e.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EntryQuery query, CancellationToken _) =>
            {
                var parsed = FindQuery.Parse(query.Find);
                return Entries.Where(parsed.Matches).ToList().AsEnumerable();
            });
        return entries.Object;
    }

    private static Entry Sgv(string id, string dateString, double sgv) =>
        new() { Id = id, Type = "sgv", DateString = dateString, Sgv = sgv };

    private static Treatment Bolus(string id, string eventType, string createdAt) =>
        new() { Id = id, EventType = eventType, CreatedAt = createdAt };
}
