using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Pins legacy Nightscout's 4-day <c>created_at</c> default on v1 treatment finds with field
/// filters (<c>lib/server/query.js</c> <c>enforceDateFilter</c>), across find, count and delete.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentFindDateWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly long WindowStartMills = Now.AddDays(-4).ToUnixTimeMilliseconds();

    [Theory]
    [InlineData("find[id][$eq]=4f2d0c1e-8a7b-4c3d-9e5f-000000000001")]
    [InlineData("find[eventType]=Correction%20Bolus&count=10")]
    [InlineData("{\"eventType\":\"Note\"}")]
    [InlineData("find[date][$gte]=1000&find[eventType]=Note")]
    [InlineData("find[$and][0][created_at][$gte]=2020-01-01&find[eventType]=Note")]
    public void FieldFilteredFindWithoutIdOrDateBound_GetsFourDayCreatedAtWindow(string find)
    {
        var windowed = LegacyTreatmentDateWindow.Apply(find, Now);

        var parsed = FindQuery.Parse(windowed);
        parsed.FromMills.Should().BeGreaterThanOrEqualTo(WindowStartMills);
        parsed.HasTopLevelCondition("created_at").Should().BeTrue();
        parsed.HasFieldFilters.Should().BeTrue("the original field filters must survive");
    }

    [Theory]
    [InlineData("find[_id]=5f8d0c1e8a7b4c3d9e5f0001&find[eventType]=Note")]
    [InlineData("find[_id][$in][]=5f8d0c1e8a7b4c3d9e5f0001&find[_id][$in][]=5f8d0c1e8a7b4c3d9e5f0002")]
    [InlineData("find[created_at][$gte]=2020-01-01&find[eventType]=Note")]
    [InlineData("find[created_at][$lte]=2020-01-01&find[eventType]=Note")]
    [InlineData("find[dateString][$gte]=2020-01-01&find[eventType]=Note")]
    [InlineData("{\"_id\":\"5f8d0c1e8a7b4c3d9e5f0001\",\"eventType\":\"Note\"}")]
    [InlineData("{\"created_at\":{\"$gte\":\"2020-01-01\"},\"eventType\":\"Note\"}")]
    public void FindWithIdOrExplicitDateBound_IsNotNarrowed(string find)
    {
        LegacyTreatmentDateWindow.Apply(find, Now).Should().Be(find);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("count=10")]
    [InlineData("find[created_at][$gte]=2020-01-01")]
    [InlineData("find[date][$gte]=1000")]
    public void FindWithoutFieldFilters_IsUnchanged(string? find)
    {
        LegacyTreatmentDateWindow.Apply(find, Now).Should().Be(find);
    }

    [Fact]
    public void WindowBound_IsLegacyIsoString()
    {
        LegacyTreatmentDateWindow.Apply("{\"eventType\":\"Note\"}", Now)
            .Should().Be("{\"eventType\":\"Note\",\"created_at\":{\"$gte\":\"2026-09-23T12:00:00.000Z\"}}");
    }

    [Fact]
    public async Task GetTreatments_PassesWindowedFindToService()
    {
        string? observed = null;
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.GetTreatmentsAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, int? _, int? _, CancellationToken _) => observed = find)
            .ReturnsAsync(Array.Empty<Treatment>());

        await NewTreatmentsController(service.Object, "?find[id][$eq]=abc").GetTreatments();

        FindQuery.Parse(observed).FromMills.Should().Be(WindowStartMills);
    }

    [Fact]
    public async Task BulkDeleteTreatments_PassesWindowedFindToService()
    {
        string? observed = null;
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.DeleteTreatmentsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, CancellationToken _) => observed = find)
            .ReturnsAsync(0);

        await NewTreatmentsController(service.Object, "?find[id][$eq]=abc").BulkDeleteTreatments();

        FindQuery.Parse(observed).FromMills.Should().Be(WindowStartMills);
    }

    [Fact]
    public async Task BulkDeleteTreatments_WithId_IsNotNarrowed()
    {
        string? observed = null;
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.DeleteTreatmentsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, CancellationToken _) => observed = find)
            .ReturnsAsync(0);

        await NewTreatmentsController(service.Object, "?find[_id]=5f8d0c1e8a7b4c3d9e5f0001")
            .BulkDeleteTreatments();

        observed.Should().Be("find[_id]=5f8d0c1e8a7b4c3d9e5f0001");
    }

    [Fact]
    public async Task CountTreatments_PassesWindowedFindToStore()
    {
        string? observed = null;
        var store = new Mock<ITreatmentStore>();
        store
            .Setup(s => s.CountAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, CancellationToken _) => observed = find)
            .ReturnsAsync(0);

        await NewCountController(store.Object, "").CountTreatments("{\"eventType\":\"Note\"}");

        FindQuery.Parse(observed).FromMills.Should().Be(WindowStartMills);
    }

    [Theory]
    [InlineData("?find[eventType]=Note")]
    [InlineData("?find%5BeventType%5D=Note")]
    public async Task CountTreatments_QueryStringFind_IsFilteredAndWindowed(string queryString)
    {
        string? observed = null;
        var store = new Mock<ITreatmentStore>();
        store
            .Setup(s => s.CountAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, CancellationToken _) => observed = find)
            .ReturnsAsync(0);

        await NewCountController(store.Object, queryString).CountTreatments(find: null);

        var parsed = FindQuery.Parse(observed);
        parsed.HasFieldFilters.Should().BeTrue();
        parsed.FromMills.Should().Be(WindowStartMills);
    }

    [Fact]
    public async Task CountGenericTreatments_QueryStringFind_IsFilteredAndWindowed()
    {
        string? observed = null;
        var store = new Mock<ITreatmentStore>();
        store
            .Setup(s => s.CountAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, CancellationToken _) => observed = find)
            .ReturnsAsync(0);
        var controller = NewCountController(store.Object, "?find[eventType]=Note");
        controller.HttpContext.Items["GrantedScopes"] = Scope.Normalize([Scope.TreatmentsRead]);

        await controller.CountGeneric("treatments", find: null);

        var parsed = FindQuery.Parse(observed);
        parsed.HasFieldFilters.Should().BeTrue();
        parsed.FromMills.Should().Be(WindowStartMills);
    }

    [Fact]
    public void JsonFindWithDuplicateKeys_IsWindowedWithoutThrowing()
    {
        const string find = "{\"eventType\":\"Note\",\"eventType\":\"Meal Bolus\"}";

        var windowed = LegacyTreatmentDateWindow.Apply(find, Now);

        windowed.Should().StartWith(find[..^1]);
        FindQuery.Parse(windowed).FromMills.Should().Be(WindowStartMills);
    }

    [Fact]
    public async Task ReadService_PushesWindowDownToProjection()
    {
        var projection = new Mock<IV4ToLegacyProjectionService>();
        projection
            .Setup(p => p.GetProjectedTreatmentsAsync(
                It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());

        await NewReadService(projection.Object, NullLogger<TreatmentReadService>.Instance).QueryAsync(
            new TreatmentQuery
            {
                Find = LegacyTreatmentDateWindow.Apply("find[id][$eq]=abc", Now),
                Count = int.MaxValue,
            });

        projection.Verify(p => p.GetProjectedTreatmentsAsync(
            WindowStartMills, null, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task FilteredCount_LogsWhenItHitsTheFetchCap()
    {
        const int maxFilterFetch = 3;
        var projected = Enumerable.Range(0, maxFilterFetch)
            .Select(i => new Treatment { EventType = "Note", Mills = i })
            .ToList();
        var projection = new Mock<IV4ToLegacyProjectionService>();
        projection
            .Setup(p => p.GetProjectedTreatmentsAsync(
                It.IsAny<long?>(), It.IsAny<long?>(), maxFilterFetch, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);
        var logger = new Mock<ILogger<TreatmentReadService>>();

        var service = NewReadService(projection.Object, logger.Object);
        service.MaxFilterFetch = maxFilterFetch;

        await service.CountAsync("{\"eventType\":\"Temp Basal\"}");

        logger.Verify(l => l.Log(
            LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private static TreatmentsController NewTreatmentsController(ITreatmentService service, string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);
        return new TreatmentsController(
            service, Mock.Of<IDocumentProcessingService>(), new FakeTimeProvider(Now),
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static CountController NewCountController(ITreatmentStore store, string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);
        return new CountController(
            Mock.Of<IEntryStore>(), store,
            CountControllerTests.DeviceStatusProjection(new Mock<IApsSnapshotRepository>(), new Mock<IPumpSnapshotRepository>()),
            Mock.Of<IProfileProjectionService>(), Mock.Of<IFoodRepository>(),
            Mock.Of<IActivityService>(), new FakeTimeProvider(Now), NullLogger<CountController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static TreatmentReadService NewReadService(
        IV4ToLegacyProjectionService projection, ILogger<TreatmentReadService> logger) =>
        new(projection, Mock.Of<ITreatmentDecomposer>(), Mock.Of<IDecompositionPipeline>(),
            Mock.Of<ITempBasalRepository>(), Mock.Of<IBolusRepository>(), Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(), Mock.Of<INoteRepository>(), Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(), Mock.Of<IStateSpanService>(), logger);
}
