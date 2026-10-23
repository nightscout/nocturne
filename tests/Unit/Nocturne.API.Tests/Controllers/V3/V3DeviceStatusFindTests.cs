using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
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
    public async Task Search_PushesTheDateWindowAndDeviceToTheStore()
    {
        var (_, reads) = await Read("?date$gte=1727658000000&date$lte=1727661600000&device$eq=loop://iPhone");

        reads.Should().OnlyContain(read =>
            read.From == DateTimeOffset.FromUnixTimeMilliseconds(Start + Hour).UtcDateTime
            && read.To >= DateTimeOffset.FromUnixTimeMilliseconds(Start + 2 * Hour).UtcDateTime
            && read.To < DateTimeOffset.FromUnixTimeMilliseconds(Start + 2 * Hour + 1).UtcDateTime
            && read.Device == "loop://iPhone");
    }

    [Fact]
    public async Task Search_ADeviceEqualityBesideAnotherOperator_StillNarrowsTheStoreToTheDevice()
    {
        var (identifiers, reads) = await Read("?device$eq=loop://iPhone&device$ne=loop://iPad");

        identifiers.Should().Equal(IPhone);
        reads.Should().OnlyContain(read => read.Device == "loop://iPhone");
    }

    [Theory]
    [InlineData("?identifier$eq=" + IPhone)]
    [InlineData("?identifier=" + IPhone)]
    public async Task Search_AnIdentifierFilter_ResolvesTheStatusByIdWithoutScanningTheStore(string queryString)
    {
        var (identifiers, reads) = await Read(queryString);

        identifiers.Should().Equal(IPhone);
        reads.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_AnIdentifierInAJsonFilter_ResolvesTheStatusById()
    {
        var (identifiers, reads) = await Read(QueryString.Create("filter", $$"""{"identifier":"{{Phone}}"}""").Value!);

        identifiers.Should().Equal(Phone);
        reads.Should().BeEmpty();
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

    private sealed record StoreRead(DateTime? From, DateTime? To, string? Device);

    private static async Task<(List<string> Identifiers, List<StoreRead> Reads)> Read(string queryString)
    {
        var groups = Stored
            .Select(s => new DeviceStatusGroup(
                new ApsSnapshot
                {
                    Id = Guid.NewGuid(),
                    LegacyId = s.Id,
                    Device = s.Device,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(s.Mills).UtcDateTime,
                    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(s.Mills).UtcDateTime,
                    ModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(s.Mills).UtcDateTime,
                    AidAlgorithm = AidAlgorithm.OpenAps,
                },
                null,
                null,
                null,
                DateTimeOffset.FromUnixTimeMilliseconds(s.Mills).UtcDateTime))
            .ToList();

        IEnumerable<DeviceStatusGroup> InWindow(DateTime? from, DateTime? to, string? device) => groups.Where(g =>
            (from is null || g.Aps!.Timestamp >= from)
            && (to is null || g.Aps!.Timestamp <= to)
            && (device is null || g.Aps!.Device == device));

        var reads = new List<StoreRead>();
        var store = new Mock<IDeviceStatusGroupRepository>();
        store
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? device, int limit, int offset, bool descending, CancellationToken _) =>
            {
                reads.Add(new StoreRead(from, to, device));
                var window = InWindow(from, to, device);
                return (descending ? window.OrderByDescending(g => g.Aps!.Timestamp) : window.OrderBy(g => g.Aps!.Timestamp))
                    .Skip(offset)
                    .Take(limit)
                    .ToList();
            });
        store
            .Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long cursor, int limit, CancellationToken _) => groups
                .Where(g => new DateTimeOffset(g.ModifiedAt).ToUnixTimeMilliseconds() > cursor)
                .OrderBy(g => g.ModifiedAt)
                .Take(limit)
                .Select(g => new HistoryRecord<DeviceStatusGroup>(g, false))
                .ToList());

        var snapshots = new Mock<IApsSnapshotRepository>();
        snapshots
            .Setup(r => r.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => groups.Select(g => g.Aps!).FirstOrDefault(aps => aps.LegacyId == id));

        var stateSpans = new Mock<IStateSpanRepository>();
        stateSpans
            .Setup(r => r.GetByCategory(
                It.IsAny<StateSpanCategory>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var controller = new DeviceStatusController(
            new DeviceStatusProjectionService(
                store.Object,
                snapshots.Object,
                Mock.Of<IPumpSnapshotRepository>(),
                Mock.Of<IUploaderSnapshotRepository>(),
                stateSpans.Object,
                Mock.Of<IDeviceStatusExtrasRepository>(),
                NullLogger<DeviceStatusProjectionService>.Instance),
            Mock.Of<IDeviceStatusDecomposer>(),
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
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
