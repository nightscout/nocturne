using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="GoogleHealthWorkRepository"/> over SQLite. Concurrent writers and Row Level Security are
/// the Postgres tests' concern.
/// </summary>
public class GoogleHealthWorkRepositoryTests : IDisposable
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    private static readonly GoogleHealthSyncProgress Progress =
        new(GoogleHealthSyncPhase.Reading, "heart-rate", 1, 3, 12, WorkerOwned: true);

    private static readonly GoogleHealthDisconnectRequest Disconnect = new(Guid.CreateVersion7(), Guid.CreateVersion7());

    public GoogleHealthWorkRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "home").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private GoogleHealthWorkRepository Work(Guid? tenant = null) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)));

    [Fact]
    public async Task A_tenant_with_no_row_has_no_work()
    {
        (await Work().GetAsync(CancellationToken.None)).Should().Be(GoogleHealthWork.None);
    }

    [Fact]
    public async Task Each_part_is_set_and_cleared_without_the_other()
    {
        await Work().SetProgressAsync(Progress, CancellationToken.None);
        await Work().SetDisconnectAsync(Disconnect, CancellationToken.None);
        (await Work().GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(Progress, Disconnect));

        await Work().SetProgressAsync(null, CancellationToken.None);
        (await Work().GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(null, Disconnect));

        await Work().SetProgressAsync(Progress, CancellationToken.None);
        await Work().SetDisconnectAsync(null, CancellationToken.None);
        (await Work().GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(Progress, null));

        await using var db = _db.CreateContext(_tenant);
        (await db.GoogleHealthWork.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Every_phase_reads_back_as_written()
    {
        foreach (var phase in Enum.GetValues<GoogleHealthSyncPhase>())
        {
            var progress = Progress with { Phase = phase };
            await Work().SetProgressAsync(progress, CancellationToken.None);
            (await Work().GetAsync(CancellationToken.None)).Progress.Should().Be(progress);
        }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("Reading")]
    public async Task A_phase_that_is_not_a_known_name_reads_as_no_progress_and_keeps_the_disconnect(string phase)
    {
        await using (var seed = _db.CreateContext(_tenant))
        {
            seed.GoogleHealthWork.Add(new GoogleHealthWorkEntity
            {
                TenantId = _tenant, ProgressPhase = phase, ProgressTotalDataTypes = 2,
                DisconnectRequestId = Disconnect.RequestId, DisconnectSubjectId = Disconnect.SubjectId
            });
            await seed.SaveChangesAsync();
        }

        (await Work().GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(null, Disconnect));
    }

    [Fact]
    public async Task A_disconnect_missing_either_id_reads_as_none()
    {
        await using (var seed = _db.CreateContext(_tenant))
        {
            seed.GoogleHealthWork.Add(new GoogleHealthWorkEntity { TenantId = _tenant, DisconnectRequestId = Guid.CreateVersion7() });
            await seed.SaveChangesAsync();
        }

        (await Work().GetAsync(CancellationToken.None)).Should().Be(GoogleHealthWork.None);
    }

    [Fact]
    public async Task Each_tenant_reads_and_writes_only_its_own_row()
    {
        await Work().SetProgressAsync(Progress, CancellationToken.None);
        await Work(_otherTenant).SetDisconnectAsync(Disconnect, CancellationToken.None);

        (await Work().GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(Progress, null));
        (await Work(_otherTenant).GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(null, Disconnect));
    }

    [Fact]
    public async Task A_write_without_a_tenant_is_refused()
    {
        var write = () => Work(Guid.Empty).SetProgressAsync(Progress, CancellationToken.None);

        await write.Should().ThrowAsync<InvalidOperationException>();
    }
}
