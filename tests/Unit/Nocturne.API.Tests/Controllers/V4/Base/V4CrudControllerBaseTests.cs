using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4.Base;

#region Test Helpers

public class TestRecord : IV4Record
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public long Mills => new DateTimeOffset(Timestamp).ToUnixTimeMilliseconds();
    public int? UtcOffset { get; set; }
    public string? Device { get; set; }
    public string? App { get; set; }
    public string? DataSource { get; set; }
    public Guid? CorrelationId { get; set; }
    public string? LegacyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}

public class TestCreateRequest : IBulkUpsertRequest
{
    public DateTimeOffset Timestamp { get; set; }
    public string? Device { get; set; }
    public string? DataSource { get; set; }
    public string? SyncIdentifier { get; set; }
}

public class TestUpdateRequest
{
    public DateTime Timestamp { get; set; }
    public string? Device { get; set; }
}

public interface ITestRecordRepository : IV4Repository<TestRecord>, IBulkCreateRepository<TestRecord>;

[ApiController]
[Route("api/v4/test")]
public class TestCrudController(ITestRecordRepository repository)
    : V4CrudControllerBase<TestRecord, TestCreateRequest, TestUpdateRequest, ITestRecordRepository>(repository)
{
    public override string WriteScope => Scope.GlucoseReadWrite;

    protected override V4BulkNaming BulkNaming => new("Test record", "record", "records");

    protected override TestRecord MapCreateToModel(TestCreateRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        Device = request.Device,
        DataSource = request.DataSource,
    };

    protected override TestRecord MapUpdateToModel(Guid id, TestUpdateRequest request, TestRecord existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp,
        Device = request.Device,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
    };
}

/// <summary>A controller that records how often the base ran the per-record after-create hook.</summary>
public class CountingCrudController(ITestRecordRepository repository) : TestCrudController(repository)
{
    public int AfterCreateCalls { get; private set; }

    protected override Task<TestRecord> OnAfterCreateAsync(TestRecord created, CancellationToken ct)
    {
        AfterCreateCalls++;
        return base.OnAfterCreateAsync(created, ct);
    }
}

#endregion

public class V4CrudControllerBaseTests
{
    private readonly Mock<ITestRecordRepository> _repo = new();
    private readonly TestCrudController _controller;

    public V4CrudControllerBaseTests()
    {
        _controller = new TestCrudController(_repo.Object);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    [Fact]
    public async Task GetAll_ReturnsOk_WithPaginatedResponse()
    {
        var records = new List<TestRecord>
        {
            new() { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow }
        };
        _repo.Setup(r => r.GetAsync(null, null, null, null, 100, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records);
        _repo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _controller.GetAll(null, null, 100, 0, "timestamp_desc", null, null);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<PaginatedResponse<TestRecord>>().Subject;
        response.Data.Should().HaveCount(1);
        response.Pagination.Total.Should().Be(1);
        response.Pagination.Limit.Should().Be(100);
        response.Pagination.Offset.Should().Be(0);
    }

    [Fact]
    public async Task ListDeleted_LimitAtCeiling_ReachesRepositoryUnchanged()
    {
        _repo.Setup(r => r.GetDeletedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _controller.ListDeleted(V4ReadLimits.MaxPageSize, 0);

        _repo.Verify(r => r.GetDeletedAsync(V4ReadLimits.MaxPageSize, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListDeleted_LimitAboveCeiling_IsClamped()
    {
        _repo.Setup(r => r.GetDeletedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _controller.ListDeleted(V4ReadLimits.MaxPageSize + 1, -1);

        _repo.Verify(r => r.GetDeletedAsync(V4ReadLimits.MaxPageSize, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAll_InvalidSort_ReturnsBadRequest()
    {
        var result = await _controller.GetAll(null, null, 100, 0, "invalid", null, null);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetAll_TimestampAsc_PassesFalseDescending()
    {
        _repo.Setup(r => r.GetAsync(null, null, null, null, 100, 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _controller.GetAll(null, null, 100, 0, "timestamp_asc", null, null);

        _repo.Verify(r => r.GetAsync(null, null, null, null, 100, 0, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetById_Found_ReturnsOk()
    {
        var id = Guid.NewGuid();
        var record = new TestRecord { Id = id, Timestamp = DateTime.UtcNow };
        _repo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await _controller.GetById(id);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(record);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestRecord?)null);

        var result = await _controller.GetById(id);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_Valid_Returns201()
    {
        var request = new TestCreateRequest { Timestamp = DateTime.UtcNow, Device = "test" };
        var model = new TestRecord { Id = Guid.NewGuid(), Timestamp = request.Timestamp.UtcDateTime, Device = request.Device };
        _repo.Setup(r => r.CreateAsync(It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);

        var result = await _controller.Create(request);

        var createdResult = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        createdResult.Value.Should().Be(model);
        createdResult.RouteValues!["id"].Should().Be(model.Id);
    }

    [Fact]
    public async Task Create_DefaultTimestamp_ReturnsBadRequest()
    {
        var request = new TestCreateRequest { Timestamp = default };

        var result = await _controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Update_Valid_ReturnsOk()
    {
        var id = Guid.NewGuid();
        var existing = new TestRecord { Id = id, Timestamp = DateTime.UtcNow };
        var request = new TestUpdateRequest { Timestamp = DateTime.UtcNow, Device = "updated" };
        var updated = new TestRecord { Id = id, Timestamp = request.Timestamp, Device = request.Device };

        _repo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repo.Setup(r => r.UpdateAsync(id, It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        var result = await _controller.Update(id, request);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(updated);
    }

    [Fact]
    public async Task Update_NotFound_Returns404()
    {
        var id = Guid.NewGuid();
        var request = new TestUpdateRequest { Timestamp = DateTime.UtcNow };
        _repo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestRecord?)null);

        var result = await _controller.Update(id, request);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_DefaultTimestamp_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        var existing = new TestRecord { Id = id, Timestamp = DateTime.UtcNow };
        var request = new TestUpdateRequest { Timestamp = default };
        _repo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await _controller.Update(id, request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Delete_Exists_ReturnsNoContent()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.DeleteAsync(id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.Delete(id);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task CreateBulk_WritesEveryItemAndReturnsThem()
    {
        _repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<TestRecord> models, WriteOrigin _, CancellationToken _) => [.. models]);

        var result = await _controller.CreateBulk(
        [
            new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow, Device = "a" },
            new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow.AddMinutes(1), Device = "b" },
        ]);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(201);
        objectResult.Value.Should().BeOfType<TestRecord[]>()
            .Which.Select(r => r.Device).Should().Equal("a", "b");
    }

    [Fact]
    public async Task CreateBulk_OverTheCap_ReturnsBadRequest()
    {
        var result = await _controller.CreateBulk(
            [.. Enumerable.Repeat(0, V4BulkValidation.MaxItems + 1).Select(_ => new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow })]);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be($"Bulk operations are limited to {V4BulkValidation.MaxItems} records per request");
        _repo.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateBulk_DefaultTimestampOnOneItem_RejectsTheWholeBatch()
    {
        var result = await _controller.CreateBulk(
        [
            new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow },
            new TestCreateRequest { Timestamp = default },
        ]);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repo.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateBulk_RunsTheAfterCreateHookOncePerRecord()
    {
        _repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<TestRecord> models, WriteOrigin _, CancellationToken _) => [.. models]);
        var controller = new CountingCrudController(_repo.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await controller.CreateBulk(
            [.. Enumerable.Repeat(0, 4).Select(_ => new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow })]);

        controller.AfterCreateCalls.Should().Be(4);
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.DeleteAsync(id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.Delete(id);

        result.Should().BeOfType<NotFoundResult>();
    }
}
