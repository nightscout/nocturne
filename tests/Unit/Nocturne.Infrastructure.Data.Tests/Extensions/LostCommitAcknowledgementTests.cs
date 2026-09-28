using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// A write whose commit landed but reported failure is replayed by the execution strategy, and the
/// replay finds the work already done. The caller must still be told what the first attempt wrote.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LostCommitAcknowledgementTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-1651-000000000001");
    private static readonly DateTime T0 = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
    private readonly FirstTransactionFault _fault = new(TransactionFault.AfterCommit);
    private readonly NocturneDbContext _retrying;

    public LostCommitAcknowledgementTests()
    {
        _retrying = new NocturneDbContext(TransactionFaultOptions.RetryingSqlite(_db.Connection, _fault))
        {
            TenantId = TenantId,
        };
    }

    public void Dispose()
    {
        _retrying.Dispose();
        _db.Dispose();
    }

    private List<Guid> SeedBoluses(int count, string? syncIdentifier = null, double insulin = 1.0)
    {
        using var seed = _db.CreateContext();
        var rows = Enumerable.Range(0, count).Select(i => new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = T0.AddMinutes(i),
            DataSource = "synthetic",
            SyncIdentifier = syncIdentifier is null ? null : $"{syncIdentifier}-{i}",
            Insulin = insulin,
        }).ToList();
        seed.Boluses.AddRange(rows);
        seed.SaveChanges();
        return rows.Select(r => r.Id).ToList();
    }

    [Fact]
    public async Task Device_status_extras_bulk_create_reports_the_rows_the_first_attempt_inserted()
    {
        var repository = new DeviceStatusExtrasRepository(
            new TestTenantDbContextFactory(_retrying), new SystemAuditContext(),
            NullLogger<DeviceStatusExtrasRepository>.Instance);

        var written = await repository.BulkCreateAsync(
            [
                new DeviceStatusExtras { Timestamp = T0, CorrelationId = Guid.NewGuid() },
                new DeviceStatusExtras { Timestamp = T0.AddMinutes(5), CorrelationId = Guid.NewGuid() },
            ],
            WriteOrigin.Live);

        _fault.Fired.Should().BeTrue();
        await using var check = _db.CreateContext();
        var stored = await check.DeviceStatusExtras.AsNoTracking().Select(e => e.CorrelationId).ToListAsync();
        written.Select(w => w.CorrelationId).Should().BeEquivalentTo(stored).And.HaveCount(2);
    }

    [Fact]
    public async Task An_update_only_bulk_create_reports_and_broadcasts_the_update_that_landed()
    {
        var seededId = SeedBoluses(1, syncIdentifier: "sync", insulin: 5.0).Single();
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();
        var repository = new BolusRepository(
            new TestTenantDbContextFactory(_retrying),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance,
            broadcaster);

        var written = await repository.BulkCreateAsync(
            [new Bolus { Timestamp = T0, DataSource = "synthetic", SyncIdentifier = "sync-0", Insulin = 9.0 }],
            WriteOrigin.Live);

        _fault.Fired.Should().BeTrue();
        await using var check = _db.CreateContext();
        var stored = await check.Boluses.AsNoTracking().SingleAsync();
        stored.Insulin.Should().Be(9.0);
        written.Should().ContainSingle().Which.Id.Should().Be(seededId);
        broadcaster.Updated.Should().ContainSingle().Which.Insulin.Should().Be(stored.Insulin);
        broadcaster.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task An_update_only_bulk_upsert_by_legacy_id_reports_and_broadcasts_the_update_that_landed()
    {
        Guid seededId;
        using (var seed = _db.CreateContext())
        {
            var row = new BolusEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                Timestamp = T0,
                DataSource = "synthetic",
                LegacyId = "legacy-0",
                Insulin = 5.0,
            };
            seed.Boluses.Add(row);
            seed.SaveChanges();
            seededId = row.Id;
        }
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();
        var repository = new BolusRepository(
            new TestTenantDbContextFactory(_retrying),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance,
            broadcaster);

        var written = await repository.BulkUpsertAsync(
            [new Bolus { Timestamp = T0, DataSource = "synthetic", LegacyId = "legacy-0", Insulin = 9.0 }],
            WriteOrigin.Live);

        _fault.Fired.Should().BeTrue();
        await using var check = _db.CreateContext();
        var stored = await check.Boluses.AsNoTracking().SingleAsync();
        stored.Insulin.Should().Be(9.0);
        written.Updated.Should().ContainSingle().Which.Id.Should().Be(seededId);
        written.Should().ContainSingle();
        // A replay would find the row already at 9.0 and broadcast nothing.
        broadcaster.Updated.Should().ContainSingle().Which.Insulin.Should().Be(stored.Insulin);
        broadcaster.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task A_soft_delete_reports_the_rows_the_first_attempt_deleted()
    {
        SeedBoluses(2);

        var count = await _retrying.AuditedSoftDeleteAsync(
            _retrying.Boluses, new Mock<IAuditContext>().Object, "data_source=synthetic");

        _fault.Fired.Should().BeTrue();
        await using var check = _db.CreateContext();
        var deleted = await check.Boluses.IgnoreQueryFilters().CountAsync(b => b.DeletedAt != null);
        count.Should().Be(deleted).And.Be(2);
    }

    [Fact]
    public async Task A_soft_delete_with_entities_reports_the_rows_the_first_attempt_deleted()
    {
        var ids = SeedBoluses(2);

        var result = await _retrying.AuditedSoftDeleteWithEntitiesAsync(
            _retrying.Boluses, new Mock<IAuditContext>().Object, "data_source=synthetic");

        _fault.Fired.Should().BeTrue();
        await using var check = _db.CreateContext();
        var deleted = await check.Boluses.IgnoreQueryFilters()
            .Where(b => b.DeletedAt != null).Select(b => b.Id).ToListAsync();
        deleted.Should().BeEquivalentTo(ids);
        result.Count.Should().Be(2);
        result.Entities.Select(e => e.Id).Should().BeEquivalentTo(deleted);
    }

    [Fact]
    public async Task A_hard_delete_reports_the_rows_the_first_attempt_deleted()
    {
        SeedBoluses(2);

        var count = await _retrying.AuditedExecuteDeleteAsync(_retrying.Boluses, new Mock<IAuditContext>().Object);

        _fault.Fired.Should().BeTrue();
        await using var check = _db.CreateContext();
        (await check.Boluses.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        count.Should().Be(2);
    }
}
