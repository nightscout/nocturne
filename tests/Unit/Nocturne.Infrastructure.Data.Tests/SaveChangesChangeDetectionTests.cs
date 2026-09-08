using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Pins the single change-detection pass per save. <c>UpdateTimestamps</c>, the
/// <see cref="MutationAuditInterceptor"/> and EF's own pre-save check each used to trigger a full
/// <c>DetectChanges</c>, so every jsonb column of every tracked entity was parsed three times per
/// save. The store is in-memory SQLite so the persisted row, not just the tracker, is observable.
/// </summary>
[Trait("Category", "Unit")]
public class SaveChangesChangeDetectionTests : IDisposable
{
    private const string CompactJson = """[{"Time":"00:00","Value":1.5,"TimeAsSeconds":0}]""";
    private const string JsonbNormalizedJson = """[{"Time": "00:00", "Value": 1.5, "TimeAsSeconds": 0}]""";

    private readonly List<SqliteTestDatabase> _databases = [];

    [Fact]
    public async Task Save_DetectsChangesExactlyOnce()
    {
        var tenantId = Guid.NewGuid();
        var db = NewStore(tenantId);
        var id = await SeedScheduleAsync(db, tenantId);

        await using var ctx = NewAuditedContext(db);
        var schedule = await ctx.BasalSchedules.SingleAsync(s => s.Id == id);
        schedule.ProfileName = "Renamed";

        var detections = 0;
        ctx.ChangeTracker.DetectedAllChanges += (_, _) => detections++;

        await ctx.SaveChangesAsync();

        detections.Should().Be(1,
            "the save detects once up front and runs with auto-detection off from then on");
    }

    [Fact]
    public async Task Save_RestoresAutoDetectChanges()
    {
        var tenantId = Guid.NewGuid();
        var db = NewStore(tenantId);

        await using var ctx = db.CreateContext();
        ctx.Foods.Add(new FoodEntity { Id = Guid.CreateVersion7() });

        await ctx.SaveChangesAsync();

        ctx.ChangeTracker.AutoDetectChangesEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task FailedSave_RestoresAutoDetectChanges()
    {
        var db = NewStore();

        await using var ctx = db.CreateContext(); // no tenant resolved
        ctx.Foods.Add(new FoodEntity { Id = Guid.CreateVersion7() });

        var act = () => ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        ctx.ChangeTracker.AutoDetectChangesEnabled.Should().BeTrue(
            "the save restores the flag even when it throws part-way through");
    }

    [Fact]
    public async Task Save_WithDetectionAlreadyDisabled_DetectsNothingAndLeavesItDisabled()
    {
        var tenantId = Guid.NewGuid();
        var db = NewStore(tenantId);
        var id = Guid.CreateVersion7();
        var before = DateTime.UtcNow;

        await using var ctx = db.CreateContext();
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;

        var detections = 0;
        ctx.ChangeTracker.DetectedAllChanges += (_, _) => detections++;

        ctx.Foods.Add(new FoodEntity { Id = id });
        await ctx.SaveChangesAsync();

        detections.Should().Be(0, "a caller that disabled detection is not detected for");
        ctx.ChangeTracker.AutoDetectChangesEnabled.Should().BeFalse();

        await using var verify = db.CreateContext();
        var food = await verify.Foods.SingleAsync(f => f.Id == id);
        food.SysCreatedAt.Should().BeOnOrAfter(before,
            "an explicitly added row is still stamped and inserted without detection");
    }

    [Fact]
    public async Task JsonbNormalizedRoundTrip_IssuesNoUpdateAndNoAuditRow()
    {
        var tenantId = Guid.NewGuid();
        var db = NewStore(tenantId);
        var id = await SeedScheduleAsync(db, tenantId);

        DateTime stampedOnInsert;
        await using (var ctx = NewAuditedContext(db))
        {
            var schedule = await ctx.BasalSchedules.SingleAsync(s => s.Id == id);
            stampedOnInsert = schedule.SysUpdatedAt;

            // What an upsert does: re-assign the compact serialization over the value Postgres
            // normalized on write.
            schedule.EntriesJson = JsonbNormalizedJson;
            await Task.Delay(5);
            await ctx.SaveChangesAsync();

            ctx.Entry(schedule).State.Should().Be(EntityState.Unchanged);
        }

        await using (var verify = db.CreateContext())
        {
            var schedule = await verify.BasalSchedules.SingleAsync(s => s.Id == id);
            schedule.SysUpdatedAt.Should().Be(stampedOnInsert, "no UPDATE was issued");
            (await verify.MutationAuditLog.CountAsync()).Should().Be(0, "no audit row was written");
        }
    }

    [Fact]
    public async Task SemanticJsonChange_StillUpdatesAndAudits()
    {
        var tenantId = Guid.NewGuid();
        var db = NewStore(tenantId);
        var id = await SeedScheduleAsync(db, tenantId);

        DateTime stampedOnInsert;
        await using (var ctx = NewAuditedContext(db))
        {
            var schedule = await ctx.BasalSchedules.SingleAsync(s => s.Id == id);
            stampedOnInsert = schedule.SysUpdatedAt;

            schedule.EntriesJson = """[{"Time":"00:00","Value":2.0,"TimeAsSeconds":0}]""";
            await Task.Delay(5);
            await ctx.SaveChangesAsync();
        }

        await using (var verify = db.CreateContext())
        {
            var schedule = await verify.BasalSchedules.SingleAsync(s => s.Id == id);
            schedule.SysUpdatedAt.Should().BeAfter(stampedOnInsert,
                "the single detection pass still flags a real modification, and the stamp written "
                + "through the tracker still reaches the UPDATE");
            (await verify.MutationAuditLog.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public void EveryTimestampMarkerProperty_IsMapped()
    {
        using var ctx = OfflineDbContext.Create();

        var markers = new (Type Marker, string Property)[]
        {
            (typeof(ISystemCreated), nameof(ISystemCreated.SysCreatedAt)),
            (typeof(ISystemTimestamped), nameof(ISystemTimestamped.SysUpdatedAt)),
            (typeof(IEntityCreated), nameof(IEntityCreated.CreatedAt)),
            (typeof(IEntityTimestamped), nameof(IEntityTimestamped.UpdatedAt)),
        };

        // UpdateTimestamps stamps these through the change tracker, which throws on a property the
        // model does not map.
        foreach (var (marker, property) in markers)
        {
            var unmapped = ctx.Model.GetEntityTypes()
                .Where(t => marker.IsAssignableFrom(t.ClrType) && t.FindProperty(property) is null)
                .Select(t => t.ClrType.Name)
                .ToList();

            unmapped.Should().BeEmpty($"{property} must be mapped on every {marker.Name}");
        }
    }

    private async Task<Guid> SeedScheduleAsync(SqliteTestDatabase db, Guid tenantId)
    {
        var id = Guid.CreateVersion7();
        await using var ctx = db.CreateContext();
        ctx.BasalSchedules.Add(new BasalScheduleEntity
        {
            Id = id,
            TenantId = tenantId,
            Timestamp = DateTime.UtcNow,
            ProfileName = "Default",
            EntriesJson = CompactJson,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// An isolated SQLite store carrying the audit interceptor, so a save runs the same three
    /// <c>Entries()</c> walks production does.
    /// </summary>
    private SqliteTestDatabase NewStore(Guid? tenantId = null)
    {
        var interceptor = new MutationAuditInterceptor(Mock.Of<IHttpContextAccessor>());
        var db = tenantId is null
            ? TestDbContextFactory.CreateSqlite()
            : TestDbContextFactory.CreateSqliteWithTenant(tenantId.Value, "test", interceptor);
        _databases.Add(db);
        return db;
    }

    /// <summary>
    /// A context with a human actor attached: a save with no audit context is an unattributed
    /// background save and is never audited, which would make an audit-row assertion vacuous.
    /// </summary>
    private static NocturneDbContext NewAuditedContext(SqliteTestDatabase db)
    {
        var ctx = db.CreateContext();
        ctx.AuditContext = new StubAuditContext();
        return ctx;
    }

    private sealed class StubAuditContext : IAuditContext
    {
        public Guid? SubjectId { get; } = Guid.CreateVersion7();
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => null;
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "PUT /api/v4/basal-schedules";
        public bool IsSystem => false;
    }

    public void Dispose()
    {
        foreach (var db in _databases)
        {
            db.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
