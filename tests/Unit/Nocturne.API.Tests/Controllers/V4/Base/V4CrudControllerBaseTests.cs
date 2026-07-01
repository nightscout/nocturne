using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.V4.Repositories;
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

public class TestCreateRequest
{
    public DateTime Timestamp { get; set; }
    public string? Device { get; set; }
}

public class TestUpdateRequest
{
    public DateTime Timestamp { get; set; }
    public string? Device { get; set; }
}

public interface ITestRecordRepository : IV4Repository<TestRecord>;

[ApiController]
[Route("api/v4/test")]
public class TestCrudController(ITestRecordRepository repository)
    : V4CrudControllerBase<TestRecord, TestCreateRequest, TestUpdateRequest, ITestRecordRepository>(repository)
{
    protected override TestRecord MapCreateToModel(TestCreateRequest request) => new()
    {
        Timestamp = request.Timestamp,
        Device = request.Device,
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
        var model = new TestRecord { Id = Guid.NewGuid(), Timestamp = request.Timestamp, Device = request.Device };
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
    public async Task Delete_NotFound_Returns404()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.DeleteAsync(id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.Delete(id);

        result.Should().BeOfType<NotFoundResult>();
    }
}
