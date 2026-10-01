using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Helpers;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// v1 DELETE by id answers Nightscout's delete status whether or not a record matched, and reads
/// an id of <see cref="LegacyDeleteStatus.AnyId"/> as the bulk delete over the request's find.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyDeleteByIdTests
{
    private const string StoredId = "5f8d0c1e8a7b4c3d9e5f0001";

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task DeleteTreatment_AnswersOkWithTheDeleteStatus_FoundOrNot(bool deleted, long count)
    {
        var service = new Mock<ITreatmentService>();
        service.Setup(s => s.DeleteTreatmentAsync(StoredId, It.IsAny<CancellationToken>())).ReturnsAsync(deleted);

        var result = await NewTreatmentsController(service.Object, "", Scope.TreatmentsReadWrite)
            .DeleteTreatment(StoredId);

        AssertDeleteStatus(result, count);
    }

    [Fact]
    public async Task DeleteTreatment_AnyIdWithFind_DeletesTheMatches()
    {
        string? observed = null;
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.DeleteTreatmentsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, CancellationToken _) => observed = find)
            .ReturnsAsync(3);

        var result = await NewTreatmentsController(
                service.Object, "?find[created_at][$lte]=2020-01-02", Scope.FullAccess)
            .DeleteTreatment(LegacyDeleteStatus.AnyId);

        AssertDeleteStatus(result, 3);
        observed.Should().Be("find[created_at][$lte]=2020-01-02");
        service.Verify(
            s => s.DeleteTreatmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTreatment_AnyIdWithoutFind_IsRefused()
    {
        var service = new Mock<ITreatmentService>(MockBehavior.Strict);

        var result = await NewTreatmentsController(service.Object, "", Scope.FullAccess)
            .DeleteTreatment(LegacyDeleteStatus.AnyId);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DeleteTreatment_AnyId_NeedsTheBulkDeleteScope()
    {
        var service = new Mock<ITreatmentService>(MockBehavior.Strict);

        var result = await NewTreatmentsController(
                service.Object, "?find[eventType]=Note", Scope.TreatmentsReadWrite)
            .DeleteTreatment(LegacyDeleteStatus.AnyId);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task BulkDeleteTreatments_CarriesDeletedCount()
    {
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.DeleteTreatmentsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var result = await NewTreatmentsController(service.Object, "?find[eventType]=Note", Scope.FullAccess)
            .BulkDeleteTreatments();

        AssertDeleteStatus(result, 2);
    }

    [Fact]
    public async Task DeleteDeviceStatus_Found_AnswersOkWithOneDeleted()
    {
        var aps = new Mock<IApsSnapshotRepository>();
        aps.Setup(r => r.GetByLegacyIdAsync(StoredId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApsSnapshot { Id = Guid.CreateVersion7(), LegacyId = StoredId });
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer
            .Setup(d => d.DeleteStoredAsync(StoredId, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var result = await NewDeviceStatusController(aps, decomposer.Object, "", Scope.DevicesReadWrite)
            .DeleteDeviceStatus(StoredId);

        AssertDeleteStatus(result, 1);
    }

    [Fact]
    public async Task DeleteDeviceStatus_Unknown_AnswersOkWithNoneDeleted()
    {
        var decomposer = new Mock<IDeviceStatusDecomposer>(MockBehavior.Strict);

        var result = await NewDeviceStatusController(
                new Mock<IApsSnapshotRepository>(), decomposer.Object, "", Scope.DevicesReadWrite)
            .DeleteDeviceStatus(StoredId);

        AssertDeleteStatus(result, 0);
    }

    [Theory]
    [InlineData("?find[created_at][$lte]=2020-01-02")]
    [InlineData("?find[device]=openaps://rig&token=x")]
    public async Task DeleteDeviceStatus_AnyIdWithFind_DeletesTheMatches(string queryString)
    {
        var aps = new Mock<IApsSnapshotRepository>();
        aps.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ApsSnapshot { Id = Guid.CreateVersion7(), LegacyId = StoredId, Timestamp = DateTime.UtcNow },
                new ApsSnapshot { Id = Guid.CreateVersion7(), LegacyId = "5f8d0c1e8a7b4c3d9e5f0002", Timestamp = DateTime.UtcNow },
            ]);
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer
            .Setup(d => d.DeleteStoredAsync(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await NewDeviceStatusController(aps, decomposer.Object, queryString, Scope.FullAccess)
            .DeleteDeviceStatus(LegacyDeleteStatus.AnyId);

        AssertDeleteStatus(result, 2);
        aps.Verify(r => r.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?token=x")]
    [InlineData("?count=10")]
    [InlineData("?find[_id]=5f8d0c1e8a7b4c3d9e5f0001")]
    [InlineData("?find[created_at][$lte]=garbage")]
    [InlineData("?find[created_at][$lte]=")]
    [InlineData("?find[device]=openaps://rig&find[uploader]=x")]
    [InlineData("?find[created_at][$ne]=2020-01-02")]
    public async Task DeleteDeviceStatus_AnyIdWithoutAFindItCanApply_IsRefused(string queryString)
    {
        var decomposer = new Mock<IDeviceStatusDecomposer>();

        var result = await NewDeviceStatusController(
                new Mock<IApsSnapshotRepository>(), decomposer.Object, queryString, Scope.FullAccess)
            .DeleteDeviceStatus(LegacyDeleteStatus.AnyId);

        result.Should().BeOfType<BadRequestObjectResult>();
        decomposer.Verify(
            d => d.DeleteStoredAsync(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("?token=x")]
    [InlineData("?find[_id]=5f8d0c1e8a7b4c3d9e5f0001")]
    public async Task BulkDeleteDeviceStatus_WithoutAFindItCanApply_IsRefused(string queryString)
    {
        var decomposer = new Mock<IDeviceStatusDecomposer>();

        var result = await NewDeviceStatusController(
                new Mock<IApsSnapshotRepository>(), decomposer.Object, queryString, Scope.FullAccess)
            .BulkDeleteDeviceStatus();

        result.Should().BeOfType<BadRequestObjectResult>();
        decomposer.Verify(
            d => d.DeleteStoredAsync(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteDeviceStatus_AnyId_NeedsTheBulkDeleteScope()
    {
        var decomposer = new Mock<IDeviceStatusDecomposer>(MockBehavior.Strict);

        var result = await NewDeviceStatusController(
                new Mock<IApsSnapshotRepository>(), decomposer.Object,
                "?find[created_at][$lte]=2020-01-02", Scope.DevicesReadWrite)
            .DeleteDeviceStatus(LegacyDeleteStatus.AnyId);

        result.Should().BeOfType<ForbidResult>();
    }

    private static void AssertDeleteStatus(ActionResult result, long count)
    {
        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        body.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        body.GetProperty("deletedCount").GetInt64().Should().Be(count);
        body.GetProperty("n").GetInt64().Should().Be(count);
    }

    private static DefaultHttpContext NewHttpContext(string queryString, string scope)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);
        httpContext.Items["GrantedScopes"] = Scope.Normalize([scope]);
        return httpContext;
    }

    private static TreatmentsController NewTreatmentsController(
        ITreatmentService service, string queryString, string scope) =>
        new(service, Mock.Of<IDocumentProcessingService>(), new FakeTimeProvider(),
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext(queryString, scope) },
        };

    private static DeviceStatusController NewDeviceStatusController(
        Mock<IApsSnapshotRepository> aps, IDeviceStatusDecomposer decomposer, string queryString, string scope) =>
        new(CountControllerTests.DeviceStatusProjection(aps, new Mock<IPumpSnapshotRepository>()),
            decomposer, Mock.Of<IWriteSideEffects>(), Mock.Of<IDataEventSink<DeviceStatus>>(),
            NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext(queryString, scope) },
        };
}
