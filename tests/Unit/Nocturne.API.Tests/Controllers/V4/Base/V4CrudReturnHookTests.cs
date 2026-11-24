using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>A controller that marks every record the base hands its before-return hook.</summary>
public class MarkingCrudController(ITestRecordRepository repository) : TestCrudController(repository)
{
    public const string Mark = "returned";

    protected override Task OnBeforeReturnAsync(IReadOnlyCollection<TestRecord> records, CancellationToken ct)
    {
        foreach (var record in records)
            record.App = Mark;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Every action of the record base that returns records, except create, hands them to
/// <c>OnBeforeReturnAsync</c> first, and only the carb intakes fill anything there.
/// </summary>
[Trait("Category", "Unit")]
public class V4CrudReturnHookTests
{
    private readonly Mock<ITestRecordRepository> _repo = new();
    private readonly MarkingCrudController _controller;

    public V4CrudReturnHookTests()
    {
        _controller = new MarkingCrudController(_repo.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static TestRecord Record() => new() { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow };

    private static object? Body(IConvertToActionResult result) => ((ObjectResult)result.Convert()).Value;

    [Fact]
    public async Task GetAll_ReturnsTheRecordsTheHookSaw()
    {
        _repo.Setup(r => r.GetAsync(null, null, null, null, 100, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Record(), Record()]);

        var body = Body(await _controller.GetAll(null, null));

        body.Should().BeOfType<PaginatedResponse<TestRecord>>()
            .Which.Data.Should().HaveCount(2).And.OnlyContain(r => r.App == MarkingCrudController.Mark);
    }

    [Fact]
    public async Task GetById_ReturnsTheRecordTheHookSaw()
    {
        var record = Record();
        _repo.Setup(r => r.GetByIdAsync(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var body = Body(await _controller.GetById(record.Id));

        body.Should().BeOfType<TestRecord>().Which.App.Should().Be(MarkingCrudController.Mark);
    }

    [Fact]
    public async Task Create_ReturnsTheRecordAsStored()
    {
        _repo.Setup(r => r.CreateAsync(It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record());

        var body = Body(await _controller.Create(new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow }));

        body.Should().BeOfType<TestRecord>().Which.App.Should().BeNull();
    }

    [Fact]
    public async Task CreateBulk_ReturnsTheRecordsTheHookSaw()
    {
        _repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Record(), Record()]);

        var body = Body(await _controller.CreateBulk(
            [new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow }, new TestCreateRequest { Timestamp = DateTimeOffset.UtcNow }]));

        body.Should().BeOfType<TestRecord[]>()
            .Which.Should().HaveCount(2).And.OnlyContain(r => r.App == MarkingCrudController.Mark);
    }

    [Fact]
    public async Task Update_ReturnsTheRecordTheHookSaw()
    {
        var existing = Record();
        _repo.Setup(r => r.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _repo.Setup(r => r.UpdateAsync(existing.Id, It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record());

        var body = Body(await _controller.Update(existing.Id, new TestUpdateRequest { Timestamp = DateTime.UtcNow }));

        body.Should().BeOfType<TestRecord>().Which.App.Should().Be(MarkingCrudController.Mark);
    }

    [Fact]
    public async Task ListDeleted_ReturnsTheRecordsTheHookSaw()
    {
        _repo.Setup(r => r.GetDeletedAsync(100, 0, It.IsAny<CancellationToken>())).ReturnsAsync([Record()]);

        var body = Body(await _controller.ListDeleted());

        body.Should().BeOfType<PaginatedResponse<TestRecord>>()
            .Which.Data.Should().ContainSingle().Which.App.Should().Be(MarkingCrudController.Mark);
    }

    [Fact]
    public async Task Restore_ReturnsTheRecordTheHookSaw()
    {
        var record = Record();
        _repo.Setup(r => r.RestoreAsync(record.Id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var body = Body(await _controller.Restore(record.Id));

        body.Should().BeOfType<TestRecord>().Which.App.Should().Be(MarkingCrudController.Mark);
    }

    [Fact]
    public async Task BulkRestore_ReturnsTheRecordsTheHookSaw()
    {
        _repo.Setup(r => r.BulkRestoreAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkRestoreResult<TestRecord> { Restored = [Record(), Record()] });

        var body = Body(await _controller.BulkRestore([Guid.NewGuid(), Guid.NewGuid()]));

        body.Should().BeOfType<BulkRestoreResult<TestRecord>>()
            .Which.Restored.Should().HaveCount(2).And.OnlyContain(r => r.App == MarkingCrudController.Mark);
    }

    [Fact]
    public async Task TheDefaultHook_LeavesTheRecordAsStored()
    {
        var record = Record();
        _repo.Setup(r => r.GetByIdAsync(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        var plain = new TestCrudController(_repo.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var body = Body(await plain.GetById(record.Id));

        body.Should().BeSameAs(record);
        record.App.Should().BeNull();
    }

    [Fact]
    public void OnlyCarbIntakes_FillAnythingBeforeReturning()
    {
        var overriding = typeof(CarbIntakeController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && DerivesFromReadOnlyBase(t))
            .Where(t => t.GetMethod("OnBeforeReturnAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null)
            .ToList();

        overriding.Should().Equal(typeof(CarbIntakeController));
    }

    private static bool DerivesFromReadOnlyBase(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(V4ReadOnlyControllerBase<,>))
                return true;
        }

        return false;
    }
}
