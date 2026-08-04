using System.Data.Common;
using Microsoft.Data.Sqlite;
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
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public AuditedBulkDeleteExtensionsTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

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

    private TestNocturneDbContext CreateContext()
    {
        var context = new TestNocturneDbContext(_contextOptions);
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

    [Fact]
    public async Task AuditedSoftDelete_SystemAuditContext_SoftDeletesWithoutAuditRows()
    {
        var id = await SeedAsync();

        await using var context = CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.TestAuditables.Where(e => e.Id == id),
            SystemAuditContext.ForService("connector:nightscout"));

        deleted.Should().Be(1);

        await using var verify = CreateContext();
        var row = await verify.TestAuditables.SingleAsync(e => e.Id == id);
        row.DeletedAt.Should().NotBeNull();
        verify.Entry(row).Property("DeletedByUser").CurrentValue.Should().Be(false);
        (await verify.Set<MutationAuditLogEntity>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuditedSoftDelete_NullAuditContext_SoftDeletesWithoutAuditRows()
    {
        var id = await SeedAsync();

        await using var context = CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.TestAuditables.Where(e => e.Id == id),
            auditContext: null);

        deleted.Should().Be(1);

        await using var verify = CreateContext();
        var row = await verify.TestAuditables.SingleAsync(e => e.Id == id);
        row.DeletedAt.Should().NotBeNull();
        (await verify.Set<MutationAuditLogEntity>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuditedSoftDelete_UserAuditContext_WritesAuditRowAndFlagsDeletedByUser()
    {
        var id = await SeedAsync();

        await using var context = CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.TestAuditables.Where(e => e.Id == id),
            new UserAuditContext());

        deleted.Should().Be(1);

        await using var verify = CreateContext();
        var row = await verify.TestAuditables.SingleAsync(e => e.Id == id);
        verify.Entry(row).Property("DeletedByUser").CurrentValue.Should().Be(true);

        var log = await verify.Set<MutationAuditLogEntity>().SingleAsync();
        log.Action.Should().Be("delete");
        log.EntityId.Should().Be(id);
        log.EntityType.Should().Be("TestAuditable");
        log.AuthType.Should().Be("SessionCookie");
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
}
