using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Pins what makes a profile re-upsert free: <c>correlation_id</c> is the only column a decomposer
/// changes when nothing about the profile has, and it is indexed, so an update to it cannot be HOT
/// and appends to every index on the table. Npgsql writes a <see cref="System.Guid"/> big-endian, so
/// a UUID v7 id is byte-monotonic in the btree and those appends rarely refill the pages the dead
/// entries freed — only vacuum returns an entirely empty one, and not before two cycles — which is why
/// production density collapsed to 1.24% rather than settling. The column
/// is also <see cref="Entities.AuditIgnoredAttribute"/>, so such an update neither audits nor
/// broadcasts, which is why the write stayed invisible while that happened.
/// The model builds offline against the Npgsql provider (no connection needed for change tracking).
/// </summary>
[Trait("Category", "Unit")]
public class CorrelationIdChurnTests
{
    private static NocturneDbContext NewContext() => OfflineDbContext.Create();

    private static TherapySettingsEntity TrackedSettings(NocturneDbContext ctx, Guid correlationId)
    {
        var entity = new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(),
            LegacyId = "profile1:Default",
            ProfileName = "Default",
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CorrelationId = correlationId,
            Dia = 3.0,
        };
        ctx.Attach(entity);
        return entity;
    }

    private static BasalScheduleEntity TrackedSchedule(NocturneDbContext ctx, Guid correlationId)
    {
        var entity = new BasalScheduleEntity
        {
            Id = Guid.CreateVersion7(),
            LegacyId = "profile1:Default",
            ProfileName = "Default",
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CorrelationId = correlationId,
            EntriesJson = """[{"Time":"00:00","Value":1.5,"TimeAsSeconds":0}]""",
        };
        ctx.Attach(entity);
        return entity;
    }

    [Fact]
    public void ReassigningTheSameCorrelationId_IssuesNoUpdate()
    {
        var correlationId = Guid.CreateVersion7();
        using var ctx = NewContext();
        var entity = TrackedSettings(ctx, correlationId);

        entity.CorrelationId = correlationId;
        ctx.ChangeTracker.DetectChanges();

        var entry = ctx.Entry(entity);
        entry.State.Should().Be(EntityState.Unchanged, "no UPDATE should be issued");
    }

    [Fact]
    public void AFreshCorrelationId_IsTheOnlyThingThatMakesAnUnchangedRowDirty()
    {
        using var ctx = NewContext();
        var entity = TrackedSettings(ctx, Guid.CreateVersion7());

        entity.CorrelationId = Guid.CreateVersion7();
        ctx.ChangeTracker.DetectChanges();

        var entry = ctx.Entry(entity);
        entry.State.Should().Be(EntityState.Modified);
        entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name)
            .Should().Equal([nameof(TherapySettingsEntity.CorrelationId)],
                "if anything else were dirty the churn would not be attributable to the correlation id alone");
        V4MaterialChange.HasMaterialChange(entry).Should().BeFalse(
            "the column is [AuditIgnored], which is why this write never showed up in the audit log");
    }

    /// <summary>
    /// The whole point of preserving the id: a re-upsert that maps an unchanged profile over the
    /// stored row must leave the entity clean, so EF emits nothing at all.
    /// </summary>
    [Fact]
    public void MappingAnUnchangedProfileOverAStoredSchedule_IssuesNoUpdate()
    {
        var correlationId = Guid.CreateVersion7();
        using var ctx = NewContext();
        var entity = TrackedSchedule(ctx, correlationId);

        BasalScheduleMapper.UpdateEntity(entity, new BasalSchedule
        {
            LegacyId = entity.LegacyId,
            ProfileName = entity.ProfileName,
            Timestamp = entity.Timestamp,
            CorrelationId = correlationId,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 1.5, TimeAsSeconds = 0 }],
        });
        ctx.ChangeTracker.DetectChanges();

        ctx.Entry(entity).State.Should().Be(EntityState.Unchanged, "no UPDATE should be issued");
    }

    [Fact]
    public void MappingAnUnchangedProfileOverAStoredSchedule_WithAFreshId_IssuesAnUpdate()
    {
        using var ctx = NewContext();
        var entity = TrackedSchedule(ctx, Guid.CreateVersion7());

        BasalScheduleMapper.UpdateEntity(entity, new BasalSchedule
        {
            LegacyId = entity.LegacyId,
            ProfileName = entity.ProfileName,
            Timestamp = entity.Timestamp,
            CorrelationId = Guid.CreateVersion7(),
            Entries = [new ScheduleEntry { Time = "00:00", Value = 1.5, TimeAsSeconds = 0 }],
        });
        ctx.ChangeTracker.DetectChanges();

        ctx.Entry(entity).State.Should().Be(EntityState.Modified,
            "this is the write the fix removes; if it stops happening the other tests prove nothing");
    }
}
