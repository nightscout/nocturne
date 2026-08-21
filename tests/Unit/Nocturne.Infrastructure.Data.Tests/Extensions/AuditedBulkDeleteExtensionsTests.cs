using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Tests.Interceptors;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// These helpers append audit rows themselves rather than through
/// <c>MutationAuditInterceptor</c>, so they carry their own copy of the system/unattributed
/// skip. A connector's reconcile sweep (<c>SoftDeleteAbsentBySourceAndDateRangeAsync</c>) runs
/// through here on every sync and was writing one actorless audit row per swept record.
/// </summary>
[Trait("Category", "Unit")]
public class AuditedBulkDeleteExtensionsTests : IDisposable
{
    private const string Scope = "data_source=test-connector";

    private readonly DbConnection _connection;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public AuditedBulkDeleteExtensionsTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
        context.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test" });
        context.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private TestNocturneDbContext CreateContext(StatementRecorder? recorder = null)
    {
        var builder = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging();

        if (recorder is not null)
            builder.AddInterceptors(recorder);

        var context = new TestNocturneDbContext(builder.Options);
        context.TenantId = _tenantId;
        return context;
    }

    private async Task<Guid> SeedAsync()
    {
        await using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "SweepMe",
            Value = 1
        };
        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();
        return entity.Id;
    }

    private async Task SeedManyAsync(int count)
    {
        await using var context = CreateContext();
        for (var i = 0; i < count; i++)
        {
            context.TestAuditables.Add(new TestAuditableEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                Name = "SweepMe",
                Value = i
            });
        }
        await context.SaveChangesAsync();
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? CorrelationId => null;
        public string? Endpoint => "DELETE /api/v4/treatments";
        public bool IsSystem => false;
    }

    /// <summary>
    /// Records every statement the context issues, so a test can assert that a path materialized
    /// nothing (no SELECT) or paged its work (more than one DELETE) — properties the row counts alone
    /// cannot distinguish.
    /// </summary>
    private sealed class StatementRecorder : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        public int SelectCount => _statements.Count(s => s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        public int DeleteCount => _statements.Count(s => s.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command) => _statements.Add(command.CommandText.TrimStart());
    }

    private static (int Count, string Scope) ReadSummary(string? changesJson)
    {
        using var doc = JsonDocument.Parse(changesJson!);
        return (doc.RootElement.GetProperty("count").GetInt32(),
            doc.RootElement.GetProperty("scope").GetString()!);
    }

    [Fact]
    public async Task AuditedSoftDelete_SystemAuditContext_SoftDeletesWithoutAuditRows()
    {
        var id = await SeedAsync();

        await using var context = CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.TestAuditables.Where(e => e.Id == id),
            SystemAuditContext.ForService("connector:nightscout"),
            Scope);

        deleted.Should().Be(1);

        await using var verify = CreateContext();
        var row = await verify.TestAuditables.SingleAsync(e => e.Id == id);
        row.DeletedAt.Should().NotBeNull();
        verify.Entry(row).Property("DeletedByUser").CurrentValue.Should().Be(false);
        (await verify.Set<MutationAuditLogEntity>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuditedSoftDelete_SystemAuditContext_MaterializesNothing()
    {
        await SeedManyAsync(5);
        var recorder = new StatementRecorder();

        await using var context = CreateContext(recorder);
        var deleted = await context.AuditedSoftDeleteAsync(
            context.TestAuditables.Where(e => e.Name == "SweepMe"),
            SystemAuditContext.ForService("connector:nightscout"),
            Scope);

        deleted.Should().Be(5);
        recorder.SelectCount.Should().Be(0);
    }

    [Fact]
    public async Task AuditedSoftDelete_NullAuditContext_SoftDeletesWithoutAuditRows()
    {
        var id = await SeedAsync();

        await using var context = CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.TestAuditables.Where(e => e.Id == id),
            auditContext: null,
            Scope);

        deleted.Should().Be(1);

        await using var verify = CreateContext();
        var row = await verify.TestAuditables.SingleAsync(e => e.Id == id);
        row.DeletedAt.Should().NotBeNull();
        (await verify.Set<MutationAuditLogEntity>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuditedSoftDelete_UserAuditContext_WritesOneSummaryRowAndFlagsDeletedByUser()
    {
        await SeedManyAsync(3);

        await using var context = CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.TestAuditables.Where(e => e.Name == "SweepMe"),
            new UserAuditContext(),
            Scope);

        deleted.Should().Be(3);

        await using var verify = CreateContext();
        foreach (var row in await verify.TestAuditables.ToListAsync())
            verify.Entry(row).Property("DeletedByUser").CurrentValue.Should().Be(true);

        var log = await verify.Set<MutationAuditLogEntity>().SingleAsync();
        log.Action.Should().Be("bulk_delete");
        log.EntityId.Should().BeNull();
        log.EntityType.Should().Be("TestAuditable");
        log.AuthType.Should().Be("SessionCookie");
        log.SubjectName.Should().Be("tester");
        ReadSummary(log.ChangesJson).Should().Be((3, Scope));
    }

    [Fact]
    public async Task AuditedSoftDelete_MatchesNothing_WritesNoAuditRow()
    {
        await using var context = CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.TestAuditables.Where(e => e.Name == "Absent"),
            new UserAuditContext(),
            Scope);

        deleted.Should().Be(0);
        (await context.Set<MutationAuditLogEntity>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuditedSoftDeleteWithEntities_UnderCap_ReturnsEntitiesWithPerRowAuditRows()
    {
        await SeedManyAsync(AuditedBulkDeleteExtensions.BroadcastMaterializationCap);

        await using var context = CreateContext();
        var result = await context.AuditedSoftDeleteWithEntitiesAsync(
            context.TestAuditables.Where(e => e.Name == "SweepMe"),
            new UserAuditContext(),
            Scope);

        result.Count.Should().Be(AuditedBulkDeleteExtensions.BroadcastMaterializationCap);
        result.Entities.Should().HaveCount(AuditedBulkDeleteExtensions.BroadcastMaterializationCap);
        result.Collapsed.Should().BeFalse();

        await using var verify = CreateContext();
        var logs = await verify.Set<MutationAuditLogEntity>().ToListAsync();
        logs.Should().HaveCount(AuditedBulkDeleteExtensions.BroadcastMaterializationCap);
        logs.Should().OnlyContain(l => l.Action == "delete" && l.EntityId != null);
    }

    [Fact]
    public async Task AuditedSoftDeleteWithEntities_OverCap_CollapsesToOneSummaryRow()
    {
        var total = AuditedBulkDeleteExtensions.BroadcastMaterializationCap + 1;
        await SeedManyAsync(total);

        await using var context = CreateContext();
        var result = await context.AuditedSoftDeleteWithEntitiesAsync(
            context.TestAuditables.Where(e => e.Name == "SweepMe"),
            new UserAuditContext(),
            Scope);

        result.Count.Should().Be(total);
        result.Entities.Should().BeEmpty();
        result.Collapsed.Should().BeTrue();

        await using var verify = CreateContext();
        var log = await verify.Set<MutationAuditLogEntity>().SingleAsync();
        log.Action.Should().Be("bulk_delete");
        log.EntityId.Should().BeNull();
        ReadSummary(log.ChangesJson).Should().Be((total, Scope));
        (await verify.TestAuditables.CountAsync(e => e.DeletedAt != null)).Should().Be(total);
    }

    [Fact]
    public async Task AuditedExecuteDelete_SystemAuditContext_DeletesWithoutAuditRows()
    {
        var id = await SeedAsync();

        await using var context = CreateContext();
        var deleted = await context.AuditedExecuteDeleteAsync(
            context.TestAuditables.Where(e => e.Id == id),
            SystemAuditContext.ForService("connector:nightscout"));

        deleted.Should().Be(1);

        await using var verify = CreateContext();
        (await verify.TestAuditables.AnyAsync(e => e.Id == id)).Should().BeFalse();
        (await verify.Set<MutationAuditLogEntity>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuditedExecuteDelete_SystemAuditContext_MaterializesNothing()
    {
        await SeedManyAsync(5);
        var recorder = new StatementRecorder();

        await using var context = CreateContext(recorder);
        var deleted = await context.AuditedExecuteDeleteAsync(
            context.TestAuditables.Where(e => e.Name == "SweepMe"),
            SystemAuditContext.ForService("connector:nightscout"));

        deleted.Should().Be(5);
        recorder.SelectCount.Should().Be(0);
        recorder.DeleteCount.Should().Be(1);
    }

    [Fact]
    public async Task AuditedExecuteDelete_UserAuditContext_WritesAuditRow()
    {
        var id = await SeedAsync();

        await using var context = CreateContext();
        await context.AuditedExecuteDeleteAsync(
            context.TestAuditables.Where(e => e.Id == id),
            new UserAuditContext());

        await using var verify = CreateContext();
        var log = await verify.Set<MutationAuditLogEntity>().SingleAsync();
        log.Action.Should().Be("delete");
        log.EntityId.Should().Be(id);
        log.AuthType.Should().Be("SessionCookie");
    }

    [Fact]
    public async Task AuditedExecuteDelete_MoreRowsThanOnePage_PagesAndSnapshotsEveryRow()
    {
        const int total = 1500;
        await SeedManyAsync(total);
        var recorder = new StatementRecorder();

        await using var context = CreateContext(recorder);
        var deleted = await context.AuditedExecuteDeleteAsync(
            context.TestAuditables.Where(e => e.Name == "SweepMe"),
            new UserAuditContext());

        deleted.Should().Be(total);
        recorder.DeleteCount.Should().Be(2);

        await using var verify = CreateContext();
        (await verify.TestAuditables.CountAsync()).Should().Be(0);
        (await verify.Set<MutationAuditLogEntity>().CountAsync(l => l.Action == "delete")).Should().Be(total);
    }
}
