using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

public interface ISyncKeyedTestRecordRepository : ITestRecordRepository, ISyncKeyedRepository<TestRecord>;

/// <summary>
/// The timestamps the V4 write actions record in a request's <see cref="TenantCacheWriteFootprint"/>,
/// which decide the eHbA1c years a glucose write clears.
/// </summary>
[Trait("Category", "Unit")]
public class V4CrudWriteFootprintTests
{
    private static readonly DateTime Stored = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Written = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ISyncKeyedTestRecordRepository> _repo = new();
    private readonly DefaultHttpContext _http = new();

    private TestCrudController Controller() => new(_repo.Object)
    {
        ControllerContext = new ControllerContext { HttpContext = _http },
    };

    private TenantCacheWriteFootprint Open() => TenantCacheWriteFootprint.Open(_http);

    [Fact]
    public async Task A_create_records_the_row_it_moves_and_where_it_lands()
    {
        var footprint = Open();
        _repo.Setup(r => r.GetUpsertTargetTimestampsAsync(It.IsAny<IReadOnlyCollection<TestRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Stored]);
        _repo.Setup(r => r.CreateAsync(It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRecord { Id = Guid.NewGuid(), Timestamp = Written });

        await Controller().Create(new TestCreateRequest { Timestamp = Written, DataSource = "uploader" });

        footprint.Timestamps.Should().BeEquivalentTo([Stored, Written]);
    }

    [Fact]
    public async Task A_bulk_create_records_the_rows_it_moves_and_where_they_land()
    {
        var footprint = Open();
        _repo.Setup(r => r.GetUpsertTargetTimestampsAsync(It.IsAny<IReadOnlyCollection<TestRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Stored]);
        _repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<TestRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TestRecord { Id = Guid.NewGuid(), Timestamp = Written }]);

        await Controller().CreateBulk([new TestCreateRequest { Timestamp = Written, DataSource = "uploader" }]);

        footprint.Timestamps.Should().BeEquivalentTo([Stored, Written]);
    }

    [Fact]
    public async Task An_update_records_the_old_and_the_new_timestamp()
    {
        var footprint = Open();
        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRecord { Id = id, Timestamp = Stored });
        _repo.Setup(r => r.UpdateAsync(id, It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRecord { Id = id, Timestamp = Written });

        await Controller().Update(id, new TestUpdateRequest { Timestamp = Written });

        footprint.Timestamps.Should().BeEquivalentTo([Stored, Written]);
    }

    [Fact]
    public async Task A_delete_records_nothing_so_the_filter_clears_every_year()
    {
        var footprint = Open();

        await Controller().Delete(Guid.NewGuid());

        footprint.Timestamps.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_footprint_a_create_does_not_look_up_the_rows_it_moves()
    {
        _repo.Setup(r => r.CreateAsync(It.IsAny<TestRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRecord { Id = Guid.NewGuid(), Timestamp = Written });

        await Controller().Create(new TestCreateRequest { Timestamp = Written, DataSource = "uploader" });

        _repo.Verify(
            r => r.GetUpsertTargetTimestampsAsync(It.IsAny<IReadOnlyCollection<TestRecord>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
