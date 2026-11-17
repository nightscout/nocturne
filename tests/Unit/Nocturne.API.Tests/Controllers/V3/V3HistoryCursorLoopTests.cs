using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Xunit;
using System.Collections;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A client that pages <c>history/{lastModified}</c> in a loop resumes from the cursor header of each
/// page. The header must equal the history clock of the newest record served, so the next request
/// skips nothing and repeats nothing, and the loop ends on an empty page.
/// </summary>
[Trait("Category", "Unit")]
public class V3HistoryCursorLoopTests
{
    private const int PageSize = 2;

    private static readonly long[] Clocks = [1_000, 2_000, 3_000, 4_000, 5_000];

    private static ControllerContext Context() => new() { HttpContext = new DefaultHttpContext() };

    private static IReadOnlyList<T> After<T>(IEnumerable<T> all, Func<T, long> clock, long since, int take) =>
        all.Where(r => clock(r) > since).OrderBy(clock).Take(take).ToList();

    private static async Task<List<(int Served, long Cursor)>> Drain(
        Func<long, Task<(IActionResult Result, ControllerBase Controller)>> request
    )
    {
        var pages = new List<(int Served, long Cursor)>();
        long since = 0;
        for (var guard = 0; guard < 10; guard++)
        {
            var (result, controller) = await request(since);
            var body = result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<V3Envelope<object>>().Subject.Result;
            var served = ((IEnumerable)body!).Cast<object>().Count();
            if (served == 0)
                return pages;

            var etag = controller.Response.Headers["ETag"].ToString();
            var cursor = long.Parse(etag.Trim('W', '/', '"'));
            controller.Response.Headers["Last-Modified"].ToString()
                .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(cursor).UtcDateTime.ToString("R"));
            pages.Add((served, cursor));
            since = cursor;
        }

        throw new Xunit.Sdk.XunitException("The history loop did not end: the cursor never passed the newest record.");
    }

    private static void ShouldHaveServedEverythingOnce(List<(int Served, long Cursor)> pages, long[] newestOfEachPage)
    {
        pages.Select(p => p.Cursor).Should().Equal(newestOfEachPage);
        pages.Sum(p => p.Served).Should().Be(Clocks.Length);
    }

    [Fact]
    public async Task Entries_CursorIsTheNewestServedRecordsClock()
    {
        var entries = Clocks.Select((c, i) => new Entry { Id = $"5f8d0c1e8a7b4c3d9e5f000{i}", Type = "sgv", Sgv = 100, Mills = c + 10_000 }).ToList();
        var clockOf = (Entry e) => e.Mills - 10_000;
        var service = new Mock<IEntryService>();
        service
            .Setup(s => s.GetEntriesModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long since, int take, CancellationToken _) =>
            {
                var page = After(entries, clockOf, since, take);
                return new ModifiedSincePage<Entry>(page, page.Count > 0 ? clockOf(page[^1]) : null);
            });

        var pages = await Drain(async since =>
        {
            var controller = new EntriesController(
                Mock.Of<IDocumentProcessingService>(), service.Object, Mock.Of<ICanonicalAlertEvaluator>(),
                NullLogger<EntriesController>.Instance) { ControllerContext = Context() };
            return (await controller.GetEntryHistory(since, PageSize), controller);
        });

        ShouldHaveServedEverythingOnce(pages, [2_000, 4_000, 5_000]);
    }

    [Fact]
    public async Task Treatments_CursorIsTheServerModificationStamp()
    {
        var treatments = Clocks.Select((c, i) => new Treatment { EventType = "Note", SrvModified = c, Mills = c - 500 + i }).ToList();
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.GetTreatmentsModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long since, int take, CancellationToken _) =>
                (IEnumerable<Treatment>)After(treatments, t => t.SrvModified!.Value, since, take));

        var pages = await Drain(async since =>
        {
            var controller = new TreatmentsController(
                Mock.Of<IDocumentProcessingService>(), service.Object, NullLogger<TreatmentsController>.Instance)
            { ControllerContext = Context() };
            return (await controller.GetTreatmentHistory(since, PageSize), controller);
        });

        ShouldHaveServedEverythingOnce(pages, [2_000, 4_000, 5_000]);
    }

    [Fact]
    public async Task Treatments_CursorFallsBackToTheEventTimeWhenThereIsNoModificationStamp()
    {
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.GetTreatmentsModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Treatment { EventType = "Note", Mills = 7_000 } });
        var controller = new TreatmentsController(
            Mock.Of<IDocumentProcessingService>(), service.Object, NullLogger<TreatmentsController>.Instance)
        { ControllerContext = Context() };

        await controller.GetTreatmentHistory(0);

        controller.Response.Headers["ETag"].ToString().Should().Be("W/\"7000\"");
    }

    [Fact]
    public async Task DeviceStatus_CursorIsTheServerModificationStamp()
    {
        var statuses = Clocks.Select((c, i) => new DeviceStatus { Id = $"5f8d0c1e8a7b4c3d9e5f000{i}", SrvModified = c, Mills = c - 500 + i }).ToList();
        var service = new Mock<IDeviceStatusService>();
        service
            .Setup(s => s.GetDeviceStatusesModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long since, int take, CancellationToken _) =>
                (IEnumerable<DeviceStatus>)After(statuses, d => d.SrvModified!.Value, since, take));

        var pages = await Drain(async since =>
        {
            var controller = new DeviceStatusController(
                service.Object, Mock.Of<IDocumentProcessingService>(), NullLogger<DeviceStatusController>.Instance)
            { ControllerContext = Context() };
            return (await controller.GetDeviceStatusHistory(since, PageSize), controller);
        });

        ShouldHaveServedEverythingOnce(pages, [2_000, 4_000, 5_000]);
    }

    [Fact]
    public async Task Food_CursorIsTheModificationStampOfThePage()
    {
        var foods = Clocks.Select((c, i) => (Food: new Food { Id = $"5f8d0c1e8a7b4c3d9e5f000{i}", Name = $"food {i}" }, Clock: c)).ToList();
        var repository = new Mock<IFoodRepository>();
        repository
            .Setup(f => f.GetFoodModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long since, int take, CancellationToken _) =>
            {
                var page = After(foods, f => f.Clock, since, take);
                return new ModifiedSincePage<Food>(page.Select(f => f.Food).ToList(), page.Count > 0 ? page[^1].Clock : null);
            });

        var pages = await Drain(async since =>
        {
            var controller = new FoodController(
                repository.Object, Mock.Of<IFoodService>(), Mock.Of<IDocumentProcessingService>(),
                NullLogger<FoodController>.Instance) { ControllerContext = Context() };
            return (await controller.GetFoodHistory(since, PageSize), controller);
        });

        ShouldHaveServedEverythingOnce(pages, [2_000, 4_000, 5_000]);
    }

    [Fact]
    public async Task Profile_CursorIsTheModificationStampOfThePage()
    {
        var profiles = Clocks.Select((c, i) => new Profile { Id = $"5f8d0c1e8a7b4c3d9e5f000{i}", SrvModified = c }).ToList();
        var projection = new Mock<IProfileProjectionService>();
        projection
            .Setup(s => s.GetProfilesModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long since, int take, CancellationToken _) =>
            {
                var page = After(profiles, p => p.SrvModified!.Value, since, take);
                return new ModifiedSincePage<Profile>(page, page.Count > 0 ? page[^1].SrvModified : null);
            });

        var pages = await Drain(async since =>
        {
            var controller = new ProfileController(
                projection.Object, Mock.Of<IProfileWriteService>(), Mock.Of<IDocumentProcessingService>(),
                NullLogger<ProfileController>.Instance) { ControllerContext = Context() };
            return (await controller.GetProfileHistory(since, PageSize), controller);
        });

        ShouldHaveServedEverythingOnce(pages, [2_000, 4_000, 5_000]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 500)]
    [InlineData(100_000, 1_000)]
    public async Task Limit_IsClampedToTheCollectionCap(int requested, int expected)
    {
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.GetTreatmentsModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());
        var controller = new TreatmentsController(
            Mock.Of<IDocumentProcessingService>(), service.Object, NullLogger<TreatmentsController>.Instance)
        { ControllerContext = Context() };

        await controller.GetTreatmentHistory(0, requested);

        service.Verify(s => s.GetTreatmentsModifiedSinceAsync(0, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProfileLimit_IsClampedToOneHundred()
    {
        var projection = new Mock<IProfileProjectionService>();
        projection
            .Setup(s => s.GetProfilesModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModifiedSincePage<Profile>([], null));
        var controller = new ProfileController(
            projection.Object, Mock.Of<IProfileWriteService>(), Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance) { ControllerContext = Context() };

        await controller.GetProfileHistory(0, 5_000);

        projection.Verify(s => s.GetProfilesModifiedSinceAsync(0, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HistoryResponse_SetsNoCacheControl()
    {
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.GetTreatmentsModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Treatment { EventType = "Note", SrvModified = 5 } });
        var controller = new TreatmentsController(
            Mock.Of<IDocumentProcessingService>(), service.Object, NullLogger<TreatmentsController>.Instance)
        { ControllerContext = Context() };

        await controller.GetTreatmentHistory(0);

        controller.Response.Headers.CacheControl.ToString().Should().BeEmpty();
    }
}
