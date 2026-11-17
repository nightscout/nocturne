using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class TrackerDefinitionEntityConfiguration : IEntityTypeConfiguration<TrackerDefinitionEntity>
{
    public void Configure(EntityTypeBuilder<TrackerDefinitionEntity> entity)
    {
        entity
            .HasIndex(d => d.CreatedAt)
            .HasDatabaseName("ix_tracker_definitions_created_at");
    }
}

internal sealed class TrackerInstanceEntityConfiguration : IEntityTypeConfiguration<TrackerInstanceEntity>
{
    public void Configure(EntityTypeBuilder<TrackerInstanceEntity> entity)
    {
        entity
            .HasIndex(i => i.DefinitionId)
            .HasDatabaseName("ix_tracker_instances_definition_id");

        entity
            .HasIndex(i => i.CompletedAt)
            .HasDatabaseName("ix_tracker_instances_completed_at")
            .HasFilter("completed_at IS NULL"); // Partial index for active instances

        entity
            .HasIndex(i => i.StartedAt)
            .HasDatabaseName("ix_tracker_instances_started_at")
            .IsDescending();
    }
}

internal sealed class TrackerPresetEntityConfiguration : IEntityTypeConfiguration<TrackerPresetEntity>
{
    public void Configure(EntityTypeBuilder<TrackerPresetEntity> entity)
    {
        entity
            .HasIndex(p => p.DefinitionId)
            .HasDatabaseName("ix_tracker_presets_definition_id");
    }
}

internal sealed class TrackerNotificationThresholdEntityConfiguration : IEntityTypeConfiguration<TrackerNotificationThresholdEntity>
{
    public void Configure(EntityTypeBuilder<TrackerNotificationThresholdEntity> entity)
    {
        // Tracker Notification Thresholds - configure relationship to use TrackerDefinitionId
        entity
            .HasOne(t => t.Definition)
            .WithMany(d => d.NotificationThresholds)
            .HasForeignKey(t => t.TrackerDefinitionId);

        // Managed alert rule synthesised from the threshold: SET NULL on rule deletion so
        // the startup backfill re-synthesises rather than leaving a dangling reference.
        entity
            .HasOne<AlertRuleEntity>()
            .WithMany()
            .HasForeignKey(t => t.AlertRuleId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasIndex(t => t.TrackerDefinitionId)
            .HasDatabaseName("ix_tracker_notification_thresholds_definition_id");

        entity
            .HasIndex(t => new { t.TrackerDefinitionId, t.DisplayOrder })
            .HasDatabaseName("ix_tracker_notification_thresholds_def_order");
    }
}
