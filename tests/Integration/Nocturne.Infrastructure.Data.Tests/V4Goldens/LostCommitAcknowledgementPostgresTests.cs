using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// A write whose commit landed but reported failure, against PostgreSQL: it stores timestamps at
/// microsecond precision, so what the replay compares must survive the round trip.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public sealed class LostCommitAcknowledgementPostgresTests(V4GoldenFixture fx)
{
    private static readonly DateTime T0 = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_update_only_bulk_create_reports_and_broadcasts_the_update_that_landed()
    {
        var tenantId = Guid.NewGuid();
        using var _ = await fx.BeginTenantScopeAsync(tenantId);

        await using var connection = new NpgsqlConnection(fx.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var guc = new NpgsqlCommand("SELECT set_config('app.current_tenant_id', @t, false)", connection))
        {
            guc.Parameters.AddWithValue("t", tenantId.ToString());
            await guc.ExecuteScalarAsync();
        }

        var fault = new FirstTransactionFault(TransactionFault.AfterCommit);
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(connection, o => o.ExecutionStrategy(d => new RetryOnTransientFault(d)))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(fault)
            .Options;

        var seededId = Guid.CreateVersion7();
        await using (var seed = new NocturneDbContext(options) { TenantId = tenantId })
        {
            seed.Boluses.Add(new BolusEntity
            {
                Id = seededId,
                TenantId = tenantId,
                Timestamp = T0,
                DataSource = "synthetic",
                SyncIdentifier = "sync-0",
                Insulin = 5.0,
            });
            await seed.SaveChangesAsync();
        }

        await using var retrying = new NocturneDbContext(options) { TenantId = tenantId };
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();
        var repository = new BolusRepository(
            new TestTenantDbContextFactory(retrying),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance,
            broadcaster);

        var written = await repository.BulkCreateAsync(
            [new Bolus { Timestamp = T0, DataSource = "synthetic", SyncIdentifier = "sync-0", Insulin = 9.0 }],
            WriteOrigin.Live);

        fault.Fired.Should().BeTrue();
        await using var check = new NocturneDbContext(options) { TenantId = tenantId };
        var stored = await check.Boluses.AsNoTracking().SingleAsync(b => b.Id == seededId);
        stored.Insulin.Should().Be(9.0);
        written.Should().ContainSingle().Which.Id.Should().Be(seededId);
        broadcaster.Updated.Should().ContainSingle().Which.Insulin.Should().Be(stored.Insulin);
        broadcaster.Created.Should().BeEmpty();
    }
}
