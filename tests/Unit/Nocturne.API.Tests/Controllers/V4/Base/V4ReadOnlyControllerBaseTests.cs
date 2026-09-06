using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

#region Test Helpers

public class ReadOnlyTestRecord : IV4Record
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

public interface IReadOnlyTestRecordRepository : IV4Repository<ReadOnlyTestRecord>;

[ApiController]
[Route("api/v4/readonly-test")]
public class TestReadOnlyController(IReadOnlyTestRecordRepository repository)
    : V4ReadOnlyControllerBase<ReadOnlyTestRecord, IReadOnlyTestRecordRepository>(repository);

#endregion

public class V4ReadOnlyControllerBaseTests
{
    private readonly Mock<IReadOnlyTestRecordRepository> _repo = new();
    private readonly TestReadOnlyController _controller;

    public V4ReadOnlyControllerBaseTests()
    {
        _controller = new TestReadOnlyController(_repo.Object);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    [Fact]
    public async Task GetAll_ReturnsOk_WithPaginatedResponse()
    {
        var records = new List<ReadOnlyTestRecord>
        {
            new() { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow }
        };
        _repo.Setup(r => r.GetAsync(null, null, null, null, 100, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records);
        _repo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _controller.GetAll(null, null, 100, 0, "timestamp_desc", null, null);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<PaginatedResponse<ReadOnlyTestRecord>>().Subject;
        response.Data.Should().HaveCount(1);
        response.Pagination.Total.Should().Be(1);
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
    public async Task GetAll_LimitAtCeiling_ReachesRepositoryUnchanged()
    {
        StubRepository();

        await _controller.GetAll(null, null, V4ReadLimits.MaxPageSize, 0, "timestamp_desc", null, null);

        _repo.Verify(r => r.GetAsync(null, null, null, null, V4ReadLimits.MaxPageSize, 0, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAll_LimitAboveCeiling_IsClampedInBothQueryAndPagination()
    {
        StubRepository();

        var result = await _controller.GetAll(null, null, V4ReadLimits.MaxPageSize + 1, 0, "timestamp_desc", null, null);

        _repo.Verify(r => r.GetAsync(null, null, null, null, V4ReadLimits.MaxPageSize, 0, true, It.IsAny<CancellationToken>()), Times.Once);
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<PaginatedResponse<ReadOnlyTestRecord>>().Subject;
        response.Pagination.Limit.Should().Be(V4ReadLimits.MaxPageSize);
    }

    [Fact]
    public async Task GetAll_NegativeOffset_IsNormalizedToZero()
    {
        StubRepository();

        await _controller.GetAll(null, null, 100, -1, "timestamp_desc", null, null);

        _repo.Verify(r => r.GetAsync(null, null, null, null, 100, 0, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAll_DateSpanAtMaximum_IsAccepted()
    {
        var to = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var from = to.AddDays(-V4ReadLimits.MaxDateSpanDays);
        _repo.Setup(r => r.GetAsync(from, to, null, null, 100, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repo.Setup(r => r.CountAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await _controller.GetAll(from, to, 100, 0, "timestamp_desc", null, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAll_DateSpanOneMillisecondOverMaximum_ReturnsBadRequest()
    {
        var to = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var from = to.AddDays(-V4ReadLimits.MaxDateSpanDays).AddMilliseconds(-1);

        var result = await _controller.GetAll(from, to, 100, 0, "timestamp_desc", null, null);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        _repo.Verify(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_OpenEndedRange_IsNotSpanChecked()
    {
        var from = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _repo.Setup(r => r.GetAsync(from, null, null, null, 100, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repo.Setup(r => r.CountAsync(from, null, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await _controller.GetAll(from, null, 100, 0, "timestamp_desc", null, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private void StubRepository()
    {
        _repo.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
    }

    [Fact]
    public async Task GetById_Found_ReturnsOk()
    {
        var id = Guid.NewGuid();
        var record = new ReadOnlyTestRecord { Id = id, Timestamp = DateTime.UtcNow };
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
            .ReturnsAsync((ReadOnlyTestRecord?)null);

        var result = await _controller.GetById(id);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
