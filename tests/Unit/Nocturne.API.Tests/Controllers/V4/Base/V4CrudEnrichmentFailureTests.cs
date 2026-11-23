using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>A controller whose before-return hook always fails with <see cref="Failure"/>.</summary>
public class FailingEnrichmentCrudController(ITestRecordRepository repository) : TestCrudController(repository)
{
    public Exception Failure { get; set; } = new InvalidOperationException("enrichment failed");

    public int HookCalls { get; private set; }

    protected override Task OnBeforeReturnAsync(IReadOnlyCollection<TestRecord> records, CancellationToken ct)
    {
        HookCalls++;
        throw Failure;
    }
}

/// <summary>
/// An enrichment never fails a write that is already committed: the write actions answer their normal
/// success with the stored records and log the failure, while a read still surfaces it.
/// </summary>
[Trait("Category", "Unit")]
public class V4CrudEnrichmentFailureTests
{
    private readonly Mock<ITestRecordRepository> _repo = new();
    private readonly ListLogger<object> _logger = new();
    private readonly FailingEnrichmentCrudController _controller;

    public V4CrudEnrichmentFailureTests()
    {
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger);
        var services = new ServiceCollection().AddSingleton(factory.Object).BuildServiceProvider();

        _controller = new FailingEnrichmentCrudController(_repo.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services },
            },
        };
    }

    private static TestRecord Record() => new() { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow };

    private static ObjectResult Result(IConvertToActionResult result) => (ObjectResult)result.Convert();

    private void AssertLoggedOnce(params TestRecord[] records)
    {
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Should().Match<(LogLevel Level, string Message, Exception? Exception)>(e =>
                e.Exception == _controller.Failure
                && e.Message.Contains(nameof(TestRecord))
                && records.All(r => e.Message.Contains(r.Id.ToString())));
    }

    [Fact]
    public async Task Create_AnswersWithTheStoredRecordAndStoresItOnce()
    {
        var stored = Record();
        _repo.Setup(r => r.CreateAsync(It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = Result(await _controller.Create(new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow }));

        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        result.Value.Should().BeSameAs(stored);
        _repo.Verify(r => r.CreateAsync(It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_AnswersWithTheStoredRecordAndLogsTheFailure()
    {
        var existing = Record();
        var stored = Record();
        _repo.Setup(r => r.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _repo.Setup(r => r.UpdateAsync(existing.Id, It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = Result(await _controller.Update(existing.Id, new TestUpdateRequest { Timestamp = DateTime.UtcNow }));

        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        result.Value.Should().BeSameAs(stored);
        _repo.Verify(r => r.UpdateAsync(existing.Id, It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        _controller.HookCalls.Should().Be(1);
        AssertLoggedOnce(stored);
    }

    [Fact]
    public async Task Update_AnswersWithTheStoredRecordWhenTheFailureIsNotFound()
    {
        var existing = Record();
        var stored = Record();
        _controller.Failure = new KeyNotFoundException("a food line is gone");
        _repo.Setup(r => r.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _repo.Setup(r => r.UpdateAsync(existing.Id, It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = Result(await _controller.Update(existing.Id, new TestUpdateRequest { Timestamp = DateTime.UtcNow }));

        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        result.Value.Should().BeSameAs(stored);
    }

    [Fact]
    public async Task CreateBulk_AnswersWithTheStoredRecordsAndLogsTheFailure()
    {
        var stored = new[] { Record(), Record() };
        _repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. stored]);

        var result = Result(await _controller.CreateBulk(
            [new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow }, new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow }]));

        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        result.Value.Should().BeOfType<TestRecord[]>().Which.Should().Equal(stored);
        _repo.Verify(r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        _controller.HookCalls.Should().Be(1);
        AssertLoggedOnce(stored);
    }

    [Fact]
    public async Task CreateBulk_LogsTheCountAndOnlyTheFirstTenIds()
    {
        var stored = Enumerable.Range(0, 12).Select(_ => Record()).ToArray();
        _repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. stored]);

        await _controller.CreateBulk(
            [.. stored.Select(_ => new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow })]);

        var message = _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Message;
        message.Should().Contain("12 committed");
        stored.Take(10).Should().OnlyContain(r => message.Contains(r.Id.ToString()));
        stored.Skip(10).Should().OnlyContain(r => !message.Contains(r.Id.ToString()));
    }

    [Fact]
    public async Task Restore_AnswersWithTheStoredRecordAndLogsTheFailure()
    {
        var stored = Record();
        _repo.Setup(r => r.RestoreAsync(stored.Id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(stored);

        var result = Result(await _controller.Restore(stored.Id));

        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        result.Value.Should().BeSameAs(stored);
        _repo.Verify(r => r.RestoreAsync(stored.Id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        _controller.HookCalls.Should().Be(1);
        AssertLoggedOnce(stored);
    }

    [Fact]
    public async Task BulkRestore_AnswersWithTheStoredRecordsAndLogsTheFailure()
    {
        var restored = new[] { Record(), Record() };
        var outcome = new BulkRestoreResult<TestRecord> { Restored = restored };
        _repo.Setup(r => r.BulkRestoreAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = Result(await _controller.BulkRestore(restored.Select(r => r.Id).ToArray()));

        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        result.Value.Should().BeSameAs(outcome);
        _repo.Verify(r => r.BulkRestoreAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        _controller.HookCalls.Should().Be(1);
        AssertLoggedOnce(restored);
    }

    [Fact]
    public async Task GetById_StillSurfacesTheFailure()
    {
        var record = Record();
        _repo.Setup(r => r.GetByIdAsync(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var act = () => _controller.GetById(record.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("enrichment failed");
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_StillSurfacesTheFailure()
    {
        _repo.Setup(r => r.GetAsync(null, null, null, null, 100, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Record()]);

        var act = () => _controller.GetAll(null, null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListDeleted_StillSurfacesTheFailure()
    {
        _repo.Setup(r => r.GetDeletedAsync(100, 0, It.IsAny<CancellationToken>())).ReturnsAsync([Record()]);

        var act = () => _controller.ListDeleted();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ACancelledRequest_IsNotSwallowedOnAWrite()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _controller.Failure = new OperationCanceledException(cts.Token);
        var stored = Record();
        _repo.Setup(r => r.RestoreAsync(stored.Id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(stored);

        var act = () => _controller.Restore(stored.Id, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _logger.Entries.Should().BeEmpty();
    }
}
