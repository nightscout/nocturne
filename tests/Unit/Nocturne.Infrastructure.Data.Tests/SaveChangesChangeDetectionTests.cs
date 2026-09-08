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
/// Covers the single change-detection pass a save runs, and the stamps that depend on it.
/// <c>UpdateTimestamps</c>, the <see cref="MutationAuditInterceptor"/> and EF's own pre-save check
/// each enumerate the change tracker, so detection has to happen once up front and be off for the
/// rest of the save; a stamp applied to a modified row after that point only reaches the UPDATE
/// because it is written through the tracker. Every store here carries the audit interceptor and
/// is in-memory SQLite, so the persisted row is observable, not just the tracker.
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
        var db = NewStore(Guid.NewGuid());

        await using var ctx = db.CreateContext();
        ctx.Foods.Add(new FoodEntity { Id = Guid.CreateVersion7() });

        await ctx.SaveChangesAsync();

        ctx.ChangeTracker.AutoDetectChangesEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task FailedSave_RestoresAutoDetectChanges()
    {
        var db = NewStore(Guid.NewGuid());

        await using var ctx = db.CreateContext(Guid.Empty); // no tenant resolved
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
                "sys_updated_at written through the tracker still reaches the UPDATE");
            (await verify.MutationAuditLog.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task EntityTimestamped_ModifyStillBumpsUpdatedAt()
    {
        var db = NewStore(Guid.NewGuid());
        var id = Guid.CreateVersion7();

        await using (var ctx = db.CreateContext())
        {
            ctx.Subjects.Add(new SubjectEntity { Id = id, Name = "original" });
            await ctx.SaveChangesAsync();
        }

        DateTime stampedOnInsert;
        await using (var ctx = db.CreateContext())
        {
            var subject = await ctx.Subjects.SingleAsync(s => s.Id == id);
            stampedOnInsert = subject.UpdatedAt;

            subject.Name = "renamed";
            await Task.Delay(5);
            await ctx.SaveChangesAsync();
        }

        await using (var verify = db.CreateContext())
        {
            var subject = await verify.Subjects.SingleAsync(s => s.Id == id);
            subject.UpdatedAt.Should().BeAfter(stampedOnInsert,
                "updated_at written through the tracker still reaches the UPDATE");
        }
    }

    [Fact]
    public async Task ClockFace_ModifyStillBumpsItsNullableUpdatedAt()
    {
        var tenantId = Guid.NewGuid();
        var db = NewStore(tenantId);
        var id = Guid.CreateVersion7();

        await using (var ctx = db.CreateContext())
        {
            ctx.ClockFaces.Add(new ClockFaceEntity { Id = id, Name = "original" });
            await ctx.SaveChangesAsync();
        }

        DateTime stampedOnInsert;
        await using (var ctx = db.CreateContext())
        {
            var clockFace = await ctx.ClockFaces.SingleAsync(c => c.Id == id);
            stampedOnInsert = clockFace.UpdatedAt!.Value;

            clockFace.Name = "renamed";
            await Task.Delay(5);
            await ctx.SaveChangesAsync();
        }

        await using (var verify = db.CreateContext())
        {
            var clockFace = await verify.ClockFaces.SingleAsync(c => c.Id == id);
            clockFace.UpdatedAt!.Value.Should().BeAfter(stampedOnInsert,
                "the entity-specific updated_at written through the tracker still reaches the UPDATE");
        }
    }

    [Fact]
    public void EveryTrackerWrittenStamp_IsMapped()
    {
        using var ctx = OfflineDbContext.Create();

        // UpdateTimestamps looks these up by name on the change tracker, which throws on a
        // property the model does not map.
        var markers = new (Type Owner, string Property)[]
        {
            (typeof(ISystemTimestamped), nameof(ISystemTimestamped.SysUpdatedAt)),
            (typeof(IEntityTimestamped), nameof(IEntityTimestamped.UpdatedAt)),
            (typeof(ClockFaceEntity), nameof(ClockFaceEntity.UpdatedAt)),
        };

        foreach (var (owner, property) in markers)
        {
            var unmapped = ctx.Model.GetEntityTypes()
                .Where(t => owner.IsAssignableFrom(t.ClrType) && t.FindProperty(property) is null)
                .Select(t => t.ClrType.Name)
                .ToList();

            unmapped.Should().BeEmpty($"{property} must be mapped on every {owner.Name}");
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
    /// An isolated SQLite store carrying the audit interceptor, so a save walks the change tracker
    /// as many times as it does in production.
    /// </summary>
    private SqliteTestDatabase NewStore(Guid tenantId)
    {
        var db = TestDbContextFactory.CreateSqliteWithTenant(
            tenantId, "test", new MutationAuditInterceptor(Mock.Of<IHttpContextAccessor>()));
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
