using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Interceptors;

namespace Nocturne.Infrastructure.Data.Tests.Interceptors;

#region Test Entities

[Table("test_auditable")]
public class TestAuditableEntity : ITenantScoped, ISoftDeletable, IAuditable
{
    [Key] public Guid Id { get; set; }

    [Column("tenant_id")] public Guid TenantId { get; set; }

    public string? Name { get; set; }

    public int Value { get; set; }

    [AuditIgnored] public DateTime SysCreatedAt { get; set; }

    [AuditIgnored] public DateTime SysUpdatedAt { get; set; }

    [AuditRedacted] public string? SecretField { get; set; }

    [Column("deleted_at")] public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// Entity that implements IAuditable but NOT ISoftDeletable.
/// Used to test entity type naming ("SensorGlucoseEntity" -> "SensorGlucose").
/// </summary>
[Table("test_sensor_glucose")]
public class TestSensorGlucoseEntity : ITenantScoped, IAuditable
{
    [Key] public Guid Id { get; set; }

    [Column("tenant_id")] public Guid TenantId { get; set; }

    public double GlucoseValue { get; set; }
}

[Table("test_non_auditable")]
public class TestNonAuditableEntity : ITenantScoped
{
    [Key] public Guid Id { get; set; }

    [Column("tenant_id")] public Guid TenantId { get; set; }

    public string? Name { get; set; }
}

#endregion

#region Test DbContext

public class TestNocturneDbContext : NocturneDbContext
{
    public TestNocturneDbContext(DbContextOptions<NocturneDbContext> options) : base(options) { }

    public DbSet<TestAuditableEntity> TestAuditables { get; set; }
    public DbSet<TestSensorGlucoseEntity> TestSensorGlucoses { get; set; }
    public DbSet<TestNonAuditableEntity> TestNonAuditables { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Remove the global query filters that reference PostgreSQL-specific functions
        // (set_config / current_setting) which don't work with SQLite.
        modelBuilder.Entity<TestAuditableEntity>(e =>
        {
            e.HasQueryFilter(null as LambdaExpression);
            e.Property(x => x.DeletedAt).IsRequired(false);
        });

        modelBuilder.Entity<TestSensorGlucoseEntity>(e =>
        {
            e.HasQueryFilter(null as LambdaExpression);
        });

        modelBuilder.Entity<TestNonAuditableEntity>(e =>
        {
            e.HasQueryFilter(null as LambdaExpression);
        });
    }
}

#endregion

[Trait("Category", "Unit")]
public class MutationAuditInterceptorTests : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly MutationAuditInterceptor _interceptor;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public MutationAuditInterceptorTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext)null!);

        _interceptor = new MutationAuditInterceptor(httpContextAccessor.Object);

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();

        // Seed the tenant so FK constraints are satisfied
        context.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test" });
        context.SaveChanges();
    }

    private TestNocturneDbContext CreateContext()
    {
        var context = new TestNocturneDbContext(_contextOptions);
        context.TenantId = _tenantId;
        return context;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Create_ProducesAuditRecordWithCreateActionAndNullChanges()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "Test",
            Value = 42,
            SecretField = "secret"
        };

        context.TestAuditables.Add(entity);

        await InvokeSavingChanges(context);

        var auditLogs = context.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().HaveCount(1);
        var log = auditLogs[0];
        log.Action.Should().Be("create");
        log.ChangesJson.Should().BeNull();
        log.EntityType.Should().Be("TestAuditable");
        log.EntityId.Should().Be(entity.Id);
        log.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task Update_ProducesAuditRecordWithChangedFieldDiff()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "Original",
            Value = 10
        };

        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        // Modify in a fresh context
        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        tracked!.Name = "Updated";

        await InvokeSavingChanges(context2);

        var auditLogs = context2.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().HaveCount(1);
        var log = auditLogs[0];
        log.Action.Should().Be("update");
        log.ChangesJson.Should().NotBeNull();

        var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log.ChangesJson!);
        changes.Should().ContainKey("Name");
        var nameChange = changes!["Name"];
        nameChange.GetProperty("old").GetString().Should().Be("Original");
        nameChange.GetProperty("new").GetString().Should().Be("Updated");
    }

    [Fact]
    public async Task Update_WithNoActualChanges_ProducesNoAuditRecord()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "Same",
            Value = 5
        };

        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        // Re-fetch and mark as modified without changing anything
        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        context2.Entry(tracked!).State = EntityState.Modified;

        await InvokeSavingChanges(context2);

        var auditLogs = context2.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task HardDelete_ProducesDeleteActionWithFullSnapshot()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "ToDelete",
            Value = 99,
            SecretField = "top-secret"
        };

        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        context2.TestAuditables.Remove(tracked!);

        await InvokeSavingChanges(context2);

        var auditLogs = context2.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().HaveCount(1);
        var log = auditLogs[0];
        log.Action.Should().Be("delete");
        log.ChangesJson.Should().NotBeNull();

        var snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log.ChangesJson!);
        snapshot.Should().ContainKey("Name");
        snapshot.Should().ContainKey("Value");
        // Redacted field should show "[redacted]"
        snapshot.Should().ContainKey("SecretField");
        snapshot!["SecretField"].GetString().Should().Be("[redacted]");
        // Ignored fields should be absent
        snapshot.Should().NotContainKey("SysCreatedAt");
        snapshot.Should().NotContainKey("SysUpdatedAt");
    }

    [Fact]
    public async Task SoftDelete_DeletedAtNullToNonNull_ProducesDeleteActionWithFullSnapshot()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "SoftDeleteMe",
            Value = 7,
            DeletedAt = null
        };

        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        tracked!.DeletedAt = DateTime.UtcNow;

        await InvokeSavingChanges(context2);

        var auditLogs = context2.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().HaveCount(1);
        var log = auditLogs[0];
        log.Action.Should().Be("delete");
        log.ChangesJson.Should().NotBeNull();

        var snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log.ChangesJson!);
        // Snapshot uses OriginalValue, so it should have the pre-delete state
        snapshot.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Restore_DeletedAtNonNullToNull_ProducesRestoreActionWithFullSnapshot()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "RestoreMe",
            Value = 3,
            DeletedAt = DateTime.UtcNow
        };

        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        using var context2 = CreateContext();
        // Bypass soft-delete filter to load the soft-deleted entity
        var tracked = await context2.TestAuditables.IgnoreQueryFilters().FirstAsync(e => e.Id == entity.Id);
        tracked.DeletedAt = null;

        await InvokeSavingChanges(context2);

        var auditLogs = context2.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().HaveCount(1);
        var log = auditLogs[0];
        log.Action.Should().Be("restore");
        log.ChangesJson.Should().NotBeNull();

        var snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log.ChangesJson!);
        // Restore snapshot uses CurrentValue
        snapshot.Should().ContainKey("Name");
        snapshot!["Name"].GetString().Should().Be("RestoreMe");
    }

    [Fact]
    public async Task SoftDelete_UserAttributed_SetsDeletedByUserTrue()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "UserDeleteMe",
            DeletedAt = null
        };
        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        using var context2 = CreateContext();
        var auditContext = new Mock<IAuditContext>();
        auditContext.Setup(x => x.AuthType).Returns("Bearer");
        context2.AuditContext = auditContext.Object;

        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        tracked!.DeletedAt = DateTime.UtcNow;

        await InvokeSavingChanges(context2);

        ((bool)context2.Entry(tracked).Property("DeletedByUser").CurrentValue!).Should().BeTrue();
    }

    [Fact]
    public async Task SoftDelete_SystemNoAuth_LeavesDeletedByUserFalse()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "SystemDeleteMe",
            DeletedAt = null
        };
        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        // No AuditContext and null HttpContext -> AuthType null -> system-attributed delete.
        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        tracked!.DeletedAt = DateTime.UtcNow;

        await InvokeSavingChanges(context2);

        ((bool)context2.Entry(tracked).Property("DeletedByUser").CurrentValue!).Should().BeFalse();
    }

    [Fact]
    public async Task Restore_ResetsDeletedByUserToFalse()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "RestoreMe",
            DeletedAt = DateTime.UtcNow
        };
        context.TestAuditables.Add(entity);
        context.Entry(entity).Property("DeletedByUser").CurrentValue = true;
        await context.SaveChangesAsync();

        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.IgnoreQueryFilters().FirstAsync(e => e.Id == entity.Id);
        tracked.DeletedAt = null;

        await InvokeSavingChanges(context2);

        ((bool)context2.Entry(tracked).Property("DeletedByUser").CurrentValue!).Should().BeFalse();
    }

    [Fact]
    public async Task AuditIgnored_PropertiesOmittedFromUpdateDiffs()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "IgnoreTest",
            Value = 1,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        };

        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        tracked!.SysUpdatedAt = DateTime.UtcNow.AddHours(1);
        tracked.Name = "Changed";

        await InvokeSavingChanges(context2);

        var auditLogs = context2.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().HaveCount(1);
        var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(auditLogs[0].ChangesJson!);
        changes.Should().ContainKey("Name");
        changes.Should().NotContainKey("SysCreatedAt");
        changes.Should().NotContainKey("SysUpdatedAt");
    }

    [Fact]
    public async Task AuditIgnored_PropertiesOmittedFromDeleteSnapshot()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "IgnoreDeleteTest",
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        };

        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        context2.TestAuditables.Remove(tracked!);

        await InvokeSavingChanges(context2);

        var log = context2.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity).Single();
        var snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log.ChangesJson!);
        snapshot.Should().NotContainKey("SysCreatedAt");
        snapshot.Should().NotContainKey("SysUpdatedAt");
    }

    [Fact]
    public async Task AuditRedacted_ShowsRedactedInsteadOfActualValues()
    {
        using var context = CreateContext();
        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "RedactTest",
            SecretField = "old-secret"
        };

        context.TestAuditables.Add(entity);
        await context.SaveChangesAsync();

        using var context2 = CreateContext();
        var tracked = await context2.TestAuditables.FindAsync(entity.Id);
        tracked!.SecretField = "new-secret";

        await InvokeSavingChanges(context2);

        var log = context2.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity).Single();
        var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log.ChangesJson!);

        changes.Should().ContainKey("SecretField");
        var secretChange = changes!["SecretField"];
        secretChange.GetProperty("old").GetString().Should().Be("[redacted]");
        secretChange.GetProperty("new").GetString().Should().Be("[redacted]");
    }

    [Fact]
    public async Task EntityTypeNaming_RemovesEntitySuffix()
    {
        using var context = CreateContext();
        var entity = new TestSensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            GlucoseValue = 120.5
        };

        context.TestSensorGlucoses.Add(entity);

        await InvokeSavingChanges(context);

        var log = context.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity).Single();
        log.EntityType.Should().Be("TestSensorGlucose");
    }

    [Fact]
    public async Task NonAuditableEntity_ProducesNoAuditRecord()
    {
        using var context = CreateContext();
        var entity = new TestNonAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "NotAudited"
        };

        context.TestNonAuditables.Add(entity);

        await InvokeSavingChanges(context);

        var auditLogs = context.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task MutationAuditLogEntity_DoesNotTriggerRecursiveAuditing()
    {
        // MutationAuditLogEntity does not implement IAuditable, so adding one
        // directly should not produce any additional audit records.
        using var context = CreateContext();
        var auditLog = new MutationAuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            EntityType = "SomeEntity",
            EntityId = Guid.CreateVersion7(),
            Action = "create",
            CreatedAt = DateTime.UtcNow
        };

        context.Set<MutationAuditLogEntity>().Add(auditLog);

        await InvokeSavingChanges(context);

        // Only the one we manually added should exist -- no recursive audit
        var auditLogs = context.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity)
            .ToList();

        auditLogs.Should().HaveCount(1);
        auditLogs[0].Should().BeSameAs(auditLog);
    }

    [Fact]
    public async Task Create_WithDbContextAuditContext_PopulatesActorFields()
    {
        using var context = CreateContext();
        context.AuditContext = SystemAuditContext.ForService("service:demo-generator");

        var entity = new TestAuditableEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = "SystemTest",
            Value = 1
        };

        context.TestAuditables.Add(entity);

        await InvokeSavingChanges(context);

        var log = context.ChangeTracker.Entries<MutationAuditLogEntity>()
            .Select(e => e.Entity).Single();
        log.AuthType.Should().Be("system");
        log.Endpoint.Should().Be("service:demo-generator");
        log.CorrelationId.Should().NotBeNullOrEmpty();
        log.SubjectId.Should().BeNull();
        log.IpAddress.Should().BeNull();
    }

    /// <summary>
    /// Invokes the interceptor's SavingChangesAsync in the same way EF Core would,
    /// without actually saving to the database (we only need to inspect the ChangeTracker).
    /// </summary>
    private async Task InvokeSavingChanges(DbContext context)
    {
        context.ChangeTracker.DetectChanges();

        var eventDefinition = new EventDefinition<string>(
            new Mock<ILoggingOptions>().Object,
            new EventId(1),
            LogLevel.Debug,
            "MutationAuditInterceptorTests",
            (LogLevel level) => (ILogger logger, string arg, Exception? ex) => { });

        var eventData = new DbContextEventData(
            eventDefinition,
            (def, data) => "test",
            context);

        await _interceptor.SavingChangesAsync(eventData, default);
    }
}
