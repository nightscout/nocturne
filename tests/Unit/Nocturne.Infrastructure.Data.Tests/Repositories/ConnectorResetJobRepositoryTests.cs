using FluentAssertions;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="ConnectorResetJobRepository"/> over SQLite: a job's record is stored once per id and
/// read back from any tenant's scope, since the table is operator metadata.
/// </summary>
public class ConnectorResetJobRepositoryTests : IDisposable
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public ConnectorResetJobRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "home");
    }

    public void Dispose() => _db.Dispose();

    private ConnectorResetJobRepository Jobs(Guid tenant) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant)));

    [Fact]
    public async Task AJobIsStoredOnceAndReplacedInPlace()
    {
        var created = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
        var pending = new ConnectorResetJobRecord(
            Guid.CreateVersion7(), _tenant, "home", "Pending", created, null, null, null, 2, 0, "[]");

        (await Jobs(_tenant).FindAsync(pending.Id, CancellationToken.None)).Should().BeNull();

        await Jobs(_tenant).SaveAsync(pending, CancellationToken.None);
        var failed = pending with
        {
            State = "Failed", StartedAt = created.AddMinutes(1), CompletedAt = created.AddMinutes(2),
            ErrorMessage = "boom", CompletedConnectors = 2, ConnectorsJson = """[{"ConnectorName":"dexcom"}]""",
        };
        await Jobs(_tenant).SaveAsync(failed, CancellationToken.None);

        (await Jobs(Guid.Empty).FindAsync(pending.Id, CancellationToken.None)).Should().Be(failed);
        using var db = _db.CreateContext();
        db.ConnectorResetJobs.Should().ContainSingle();
    }
}
