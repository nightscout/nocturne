using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 devicestatus search honours every condition <see cref="Nocturne.Core.Models.Queries.FindQuery"/>
/// supports, not only the device and the time window the store can filter.
/// </summary>
[Trait("Category", "Unit")]
public class V3DeviceStatusFindTests
{
    private const long Start = 1727654400000;
    private const long Hour = 3_600_000;

    private const string Rig = "5f8d0c1e8a7b4c3d9e5f0001";
    private const string IPhone = "5f8d0c1e8a7b4c3d9e5f0002";
    private const string Phone = "5f8d0c1e8a7b4c3d9e5f0003";
    private const string IPad = "5f8d0c1e8a7b4c3d9e5f0004";

    [Theory]
    [InlineData("?device$eq=loop://iPhone", IPhone)]
    [InlineData("?device$ne=loop://iPhone", Rig + "," + Phone + "," + IPad)]
    [InlineData("?device$nin=loop://iPhone", Rig + "," + Phone + "," + IPad)]
    [InlineData("?device$re=^loop", IPhone + "," + IPad)]
    [InlineData("?date$gte=1727658000000&date$lte=1727661600000", IPhone + "," + Phone)]
    [InlineData("?date$gt=1727658000000", Phone + "," + IPad)]
    [InlineData("?created_at$gte=1727658000000&created_at$lt=1727665200000", IPhone + "," + Phone)]
    [InlineData("?created_at$lte=1727654400000", Rig)]
    [InlineData("?device$ne=loop://iPhone&date$gte=1727658000000", Phone + "," + IPad)]
    [InlineData("?device$eq=loop://iPhone&device$ne=loop://iPad", IPhone)]
    [InlineData("?device$in=openaps://rig|xdrip://phone", Rig + "," + Phone)]
    [InlineData("?device$nin=openaps://rig|xdrip://phone", IPhone + "," + IPad)]
    public async Task Search_HonoursTheCondition(string queryString, string expected)
    {
        var (identifiers, _) = await Read(queryString);

        identifiers.Should().BeEquivalentTo(expected.Split(','));
    }

    [Theory]
    [InlineData("""{"device":{"$in":["openaps://rig","xdrip://phone"]}}""", Rig + "," + Phone)]
    [InlineData("""{"device":{"$nin":["openaps://rig","xdrip://phone"]}}""", IPhone + "," + IPad)]
    [InlineData("""{"$or":[{"device":"openaps://rig"},{"device":{"$regex":"iPad$"}}]}""", Rig + "," + IPad)]
    public async Task Search_HonoursAJsonFilter(string filter, string expected)
    {
        var (identifiers, _) = await Read(QueryString.Create("filter", filter).Value!);

        identifiers.Should().BeEquivalentTo(expected.Split(','));
    }

    [Fact]
    public async Task Search_BoundsTheReadByTheDateWindowAndDevice()
    {
        var (_, reads) = await Read("?date$gte=1727658000000&date$lte=1727661600000&device$eq=loop://iPhone");

        reads.Should().ContainSingle().Which.Should().Match<FindQuery>(read =>
            read.FromMills == Start + Hour
            && read.ToMills == Start + 2 * Hour
            && read.GetPushableEquality("device", StringComparison.Ordinal) == "loop://iPhone");
    }

    [Fact]
    public async Task Search_ADeviceEqualityBesideAnotherOperator_StillBoundsTheReadToTheDevice()
    {
        var (identifiers, reads) = await Read("?device$eq=loop://iPhone&device$ne=loop://iPad");

        identifiers.Should().Equal(IPhone);
        reads.Should().OnlyContain(read => read.GetPushableEquality("device", StringComparison.Ordinal) == "loop://iPhone");
    }

    [Theory]
    [InlineData("?identifier$eq=" + IPhone)]
    [InlineData("?identifier=" + IPhone)]
    public async Task Search_AnIdentifierFilter_ReadsTheStatusById(string queryString)
    {
        var (identifiers, reads) = await Read(queryString);

        identifiers.Should().Equal(IPhone);
        reads.Should().ContainSingle().Which.GetEqualityValues("_id").Should().Equal(IPhone);
    }

    [Fact]
    public async Task Search_AnIdentifierInAJsonFilter_ResolvesTheStatusById()
    {
        var (identifiers, reads) = await Read(QueryString.Create("filter", $$"""{"identifier":"{{Phone}}"}""").Value!);

        identifiers.Should().Equal(Phone);
        reads.Should().ContainSingle().Which.GetEqualityValues("_id").Should().Equal(Phone);
    }

    [Fact]
    public async Task Search_AJsonFilterWithDuplicateKeys_IsEvaluatedAsBefore()
    {
        var (identifiers, _) = await Read(
            QueryString.Create("filter", """{"device":"loop://iPhone","device":"loop://iPad"}""").Value!);

        identifiers.Should().BeEmpty();
    }

    [Theory]
    [InlineData("?identifier$eq=" + IPhone + "&_id$eq=" + Phone)]
    [InlineData("?identifier$eq=" + Phone + "&_id$eq=" + IPhone)]
    public async Task Search_AnIdentifierBesideADifferentId_MatchesNothing(string queryString)
    {
        var (identifiers, _) = await Read(queryString);

        identifiers.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_AnIdentifierBesideADifferentIdInAJsonFilter_MatchesNothing()
    {
        var (identifiers, _) = await Read(
            QueryString.Create("filter", $$"""{"identifier":"{{IPhone}}","_id":"{{Phone}}"}""").Value!);

        identifiers.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_AnIdentifierNestedInAnOr_ResolvesTheStatuses()
    {
        var (identifiers, _) = await Read(
            QueryString.Create("filter", $$"""{"$or":[{"identifier":"{{Rig}}"},{"identifier":"{{IPad}}"}]}""").Value!);

        identifiers.Should().BeEquivalentTo(Rig, IPad);
    }

    [Fact]
    public async Task Search_PagesAfterFiltering()
    {
        var (identifiers, _) = await Read("?device$ne=openaps://rig&limit=1&skip=1&sort$desc=date");

        identifiers.Should().Equal(Phone);
    }

    private static readonly (string Id, string Device, long Mills)[] Stored =
    [
        (Rig, "openaps://rig", Start),
        (IPhone, "loop://iPhone", Start + Hour),
        (Phone, "xdrip://phone", Start + 2 * Hour),
        (IPad, "loop://iPad", Start + 3 * Hour),
    ];

    /// <summary>
    /// Searches through a device status service that matches the find the controller passes against
    /// each stored status's wire shape.
    /// </summary>
    private static async Task<(List<string> Identifiers, List<FindQuery> Reads)> Read(string queryString)
    {
        var statuses = Stored
            .Select(s => new DeviceStatus
            {
                Id = s.Id,
                Device = s.Device,
                Mills = s.Mills,
                Date = s.Mills,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(s.Mills).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            })
            .ToList();

        var reads = new List<FindQuery>();
        var deviceStatuses = new Mock<IDeviceStatusService>();
        deviceStatuses
            .Setup(s => s.GetDeviceStatusesAsync(
                It.IsAny<FindQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<ReadSortField>()))
            .ReturnsAsync((FindQuery find, int count, int skip, CancellationToken _, bool descending, ReadSortField _) =>
            {
                reads.Add(find);
                var matching = statuses.Where(find.Matches);
                return (descending ? matching.OrderByDescending(s => s.Mills) : matching.OrderBy(s => s.Mills))
                    .Skip(skip)
                    .Take(count)
                    .ToList();
            });

        var controller = new DeviceStatusController(
            deviceStatuses.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Request = { QueryString = new QueryString(queryString) } },
            },
        };

        var result = await controller.GetDeviceStatus();

        var body = JsonSerializer.SerializeToElement(
            result.Should().BeOfType<OkObjectResult>().Subject.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var identifiers = body.GetProperty("result").EnumerateArray()
            .Select(status => status.GetProperty("identifier").GetString()!)
            .ToList();
        return (identifiers, reads);
    }
}
