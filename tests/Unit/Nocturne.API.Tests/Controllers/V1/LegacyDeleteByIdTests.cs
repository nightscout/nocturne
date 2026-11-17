using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Helpers;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
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
    public async Task DeleteTreatment_AFailedDelete_AnswersAServerErrorNotADeleteStatus()
    {
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.DeleteTreatmentAsync(StoredId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rolled back"));

        var result = await NewTreatmentsController(service.Object, "", Scope.TreatmentsReadWrite)
            .DeleteTreatment(StoredId);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
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

    [Theory]
    [InlineData("")]
    [InlineData("?find={}")]
    [InlineData("?find=%7B%7D")]
    [InlineData("?token=x")]
    [InlineData("?count=5&token=x")]
    [InlineData("?find[]=")]
    [InlineData("?find[x]")]
    [InlineData("?find%5Bx%5D")]
    [InlineData("?xfind[a]=1")]
    public async Task BulkDeleteTreatments_WithAFindThatHasNoConditions_IsRefusedWithoutDeleting(string queryString)
    {
        var service = new Mock<ITreatmentService>(MockBehavior.Strict);

        var result = await NewTreatmentsController(service.Object, queryString, Scope.FullAccess)
            .BulkDeleteTreatments();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("?find={}")]
    [InlineData("?token=x")]
    public async Task DeleteTreatment_AnyIdWithAFindThatHasNoConditions_IsRefusedWithoutDeleting(string queryString)
    {
        var service = new Mock<ITreatmentService>(MockBehavior.Strict);

        var result = await NewTreatmentsController(service.Object, queryString, Scope.FullAccess)
            .DeleteTreatment(LegacyDeleteStatus.AnyId);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BulkDeleteTreatments_AcceptsAnEncodedFindKey()
    {
        string? observed = null;
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.DeleteTreatmentsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, CancellationToken _) => observed = find)
            .ReturnsAsync(1);

        var result = await NewTreatmentsController(
                service.Object, "?find%5Bcreated_at%5D%5B%24lte%5D=2020-01-02", Scope.FullAccess)
            .BulkDeleteTreatments();

        AssertDeleteStatus(result, 1);
        observed.Should().Be("find%5Bcreated_at%5D%5B%24lte%5D=2020-01-02");
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

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task DeleteDeviceStatus_AnswersOkWithTheDeleteStatus_FoundOrNot(bool deleted, long count)
    {
        var service = new Mock<IDeviceStatusService>();
        service.Setup(s => s.DeleteDeviceStatusAsync(StoredId, It.IsAny<CancellationToken>())).ReturnsAsync(deleted);

        var result = await NewDeviceStatusController(service.Object, "", Scope.DevicesReadWrite)
            .DeleteDeviceStatus(StoredId);

        AssertDeleteStatus(result, count);
    }

    [Theory]
    [InlineData("?find[created_at][$lte]=2020-01-02", false)]
    [InlineData("?find[device]=openaps://rig&token=x", true)]
    public async Task DeleteDeviceStatus_AnyIdWithFind_DeletesTheMatches(string queryString, bool windowed)
    {
        FindQuery? find = null;
        var service = new Mock<IDeviceStatusService>();
        service
            .Setup(s => s.DeleteDeviceStatusesAsync(It.IsAny<FindQuery>(), It.IsAny<CancellationToken>()))
            .Callback<FindQuery, CancellationToken>((f, _) => find = f)
            .ReturnsAsync(2);

        var result = await NewDeviceStatusController(service.Object, queryString, Scope.FullAccess)
            .DeleteDeviceStatus(LegacyDeleteStatus.AnyId);

        AssertDeleteStatus(result, 2);
        find!.FromMills.HasValue.Should().Be(windowed, "Nightscout windows a delete that names no created_at or _id");
        service.Verify(s => s.DeleteDeviceStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("?find[_id][$nin]=" + StoredId, "5f8d0c1e8a7b4c3d9e5f0002")]
    [InlineData("?find[device][$ne]=loop://iPhone", StoredId)]
    [InlineData("?find[loop][$exists]=true", "5f8d0c1e8a7b4c3d9e5f0002")]
    [InlineData("?find[device]=openaps://rig&find[uploader]=x", "")]
    [InlineData("?find[created_at][$ne]=2020-01-02", StoredId + ",5f8d0c1e8a7b4c3d9e5f0002")]
    public async Task DeleteDeviceStatus_AnyIdWithFieldConditions_DeletesOnlyTheStatusesTheyMatch(
        string queryString, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        DeviceStatus[] stored =
        [
            new() { Id = StoredId, Device = "openaps://rig", Mills = now.ToUnixTimeMilliseconds(), CreatedAt = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
            new()
            {
                Id = "5f8d0c1e8a7b4c3d9e5f0002", Device = "loop://iPhone", Mills = now.ToUnixTimeMilliseconds(),
                CreatedAt = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), Loop = new LoopStatus(),
            },
        ];
        var deleted = new List<string>();
        var service = new Mock<IDeviceStatusService>();
        service
            .Setup(s => s.DeleteDeviceStatusesAsync(It.IsAny<FindQuery>(), It.IsAny<CancellationToken>()))
            .Callback<FindQuery, CancellationToken>((find, _) => deleted.AddRange(stored.Where(find.Matches).Select(s => s.Id!)))
            .ReturnsAsync(() => deleted.Count);

        await NewDeviceStatusController(service.Object, queryString, Scope.FullAccess)
            .DeleteDeviceStatus(LegacyDeleteStatus.AnyId);

        deleted.Should().Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task DeleteDeviceStatus_AnyIdWithAnIdFind_DeletesThatStatusOnly()
    {
        FindQuery? find = null;
        var service = new Mock<IDeviceStatusService>();
        service
            .Setup(s => s.DeleteDeviceStatusesAsync(It.IsAny<FindQuery>(), It.IsAny<CancellationToken>()))
            .Callback<FindQuery, CancellationToken>((f, _) => find = f)
            .ReturnsAsync(1);

        var result = await NewDeviceStatusController(service.Object, $"?find[_id]={StoredId}", Scope.FullAccess)
            .DeleteDeviceStatus(LegacyDeleteStatus.AnyId);

        AssertDeleteStatus(result, 1);
        find!.GetEqualityValues("_id").Should().Equal(StoredId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?token=x")]
    [InlineData("?count=10")]
    [InlineData("?find[created_at][$lte]=garbage")]
    [InlineData("?find[created_at][$lte]=")]
    [InlineData("?find[device]=openaps://rig&find[created_at][]=")]
    [InlineData("?find[device]=openaps://rig&find[created_at][$gte]=true")]
    [InlineData("?find[device]=openaps://rig&find[pump][$where]=1")]
    [InlineData("?find[device]=openaps://rig&find[pump=1")]
    [InlineData("?find[device]=openaps://rig&find[$or][x][pump]=1")]
    [InlineData("?find[device][$options]=i&find[created_at][$lte]=2026-10-02T00:00:00Z")]
    [InlineData("?find[device]=openaps://rig&find[device][$options]=i")]
    [InlineData("?find[device][$regex]=loop&find[uploader][$options]=i")]
    [InlineData("?find[loop][$exists]=yes&find[created_at][$gte]=2020-01-01T00:00:00Z")]
    [InlineData("?find[device]=openaps://rig&find[loop][$exists]=")]
    public async Task DeleteDeviceStatus_AnyIdWithoutAFindItCanApply_IsRefused(string queryString)
    {
        var service = new Mock<IDeviceStatusService>(MockBehavior.Strict);

        var result = await NewDeviceStatusController(service.Object, queryString, Scope.FullAccess)
            .DeleteDeviceStatus(LegacyDeleteStatus.AnyId);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?find={}")]
    [InlineData("?find=%7B%7D")]
    [InlineData("?token=x")]
    public async Task BulkDeleteDeviceStatus_WithoutAFindItCanApply_IsRefused(string queryString)
    {
        var service = new Mock<IDeviceStatusService>(MockBehavior.Strict);

        var result = await NewDeviceStatusController(service.Object, queryString, Scope.FullAccess)
            .BulkDeleteDeviceStatus();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DeleteDeviceStatus_AnyId_NeedsTheBulkDeleteScope()
    {
        var service = new Mock<IDeviceStatusService>(MockBehavior.Strict);

        var result = await NewDeviceStatusController(
                service.Object, "?find[created_at][$lte]=2020-01-02", Scope.DevicesReadWrite)
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
        IDeviceStatusService service, string queryString, string scope) =>
        new(service, TimeProvider.System, NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext(queryString, scope) },
        };
}
