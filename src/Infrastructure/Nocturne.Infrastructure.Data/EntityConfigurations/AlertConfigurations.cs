using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class AlertRuleEntityConfiguration : IEntityTypeConfiguration<AlertRuleEntity>
{
    public void Configure(EntityTypeBuilder<AlertRuleEntity> entity)
    {
        entity
            .HasIndex(r => r.ManagedBy)
            .HasDatabaseName("ix_alert_rules_managed_by")
            .HasFilter("managed_by IS NOT NULL");

        entity
            .Property(r => r.StarterKind)
            .HasConversion<string>();

        entity
            .HasIndex(r => new { r.StarterKind, r.TenantId })
            .IsUnique()
            .HasFilter("starter_kind IS NOT NULL")
            .HasDatabaseName("ix_alert_rules_starter_kind_tenant");

        entity.ToTable("alert_rules");
        entity.Property(e => e.ConditionType).HasConversion(
            new Converters.EnumMemberValueConverter<Core.Models.Alerts.AlertConditionType>());
        entity.Property(e => e.ConditionParams).HasColumnType("jsonb").HasDefaultValue("{}");
        entity.Property(e => e.Severity).HasConversion(
            new Converters.EnumMemberValueConverter<Core.Models.Alerts.AlertRuleSeverity>());
        entity.Property(e => e.ScopeClass).HasConversion(
            new Converters.EnumMemberValueConverter<Core.Models.Alerts.RuleScopeClass>());
        entity.Property(e => e.ClientConfiguration).HasColumnType("jsonb").HasDefaultValue("{}");
        entity.Property(e => e.IsEnabled).HasDefaultValue(true);
    }
}

internal sealed class AlertConditionTimerEntityConfiguration : IEntityTypeConfiguration<AlertConditionTimerEntity>
{
    public void Configure(EntityTypeBuilder<AlertConditionTimerEntity> entity)
    {
        entity.ToTable("alert_condition_timers");
        entity.HasKey(e => new { e.AlertRuleId, e.ConditionPath });

        entity.HasOne(e => e.AlertRule)
            .WithMany()
            .HasForeignKey(e => e.AlertRuleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertTrackerStateEntityConfiguration : IEntityTypeConfiguration<AlertTrackerStateEntity>
{
    public void Configure(EntityTypeBuilder<AlertTrackerStateEntity> entity)
    {
        entity.ToTable("alert_tracker_state");
        entity.HasKey(e => e.AlertRuleId);
        entity.Property(e => e.AlertRuleId).ValueGeneratedNever();
        entity.Property(e => e.State).HasDefaultValue("idle");

        entity.HasOne(e => e.AlertRule)
            .WithOne(r => r.TrackerState)
            .HasForeignKey<AlertTrackerStateEntity>(e => e.AlertRuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.ActiveExcursion)
            .WithMany()
            .HasForeignKey(e => e.ActiveExcursionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AlertExcursionEntityConfiguration : IEntityTypeConfiguration<AlertExcursionEntity>
{
    public void Configure(EntityTypeBuilder<AlertExcursionEntity> entity)
    {
        // Active excursion lookup by tenant
        entity
            .HasIndex(e => new { e.TenantId, e.EndedAt })
            .HasDatabaseName("ix_alert_excursions_tenant_ended_at");

        // Per-rule excursion lookup
        entity
            .HasIndex(e => new { e.AlertRuleId, e.EndedAt })
            .HasDatabaseName("ix_alert_excursions_rule_ended_at");

        entity.ToTable("alert_excursions");

        entity.HasOne(e => e.AlertRule)
            .WithMany()
            .HasForeignKey(e => e.AlertRuleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertExcursionMuteEntityConfiguration : IEntityTypeConfiguration<AlertExcursionMuteEntity>
{
    public void Configure(EntityTypeBuilder<AlertExcursionMuteEntity> entity)
    {
        entity.HasOne(e => e.AlertExcursion)
            .WithMany()
            .HasForeignKey(e => e.AlertExcursionId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Subject)
            .WithMany()
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.TenantId, e.SubjectId, e.AlertExcursionId })
            .IsUnique()
            .HasDatabaseName("ix_alert_excursion_mutes_tenant_subject_excursion");
        entity.HasIndex(e => e.AlertExcursionId);
    }
}

internal sealed class AlertExcursionReceiptEntityConfiguration : IEntityTypeConfiguration<AlertExcursionReceiptEntity>
{
    public void Configure(EntityTypeBuilder<AlertExcursionReceiptEntity> entity)
    {
        entity.Property(e => e.ActorKind).HasConversion<string>();

        entity.HasOne(e => e.AlertExcursion)
            .WithMany()
            .HasForeignKey(e => e.AlertExcursionId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.TenantId, e.AlertExcursionId, e.ActorKind, e.ActorId })
            .IsUnique()
            .HasDatabaseName("ix_alert_excursion_receipts_tenant_excursion_actor");
    }
}

internal sealed class AlertInstanceEntityConfiguration : IEntityTypeConfiguration<AlertInstanceEntity>
{
    public void Configure(EntityTypeBuilder<AlertInstanceEntity> entity)
    {
        entity.ToTable("alert_instances");
        entity.Property(e => e.Status).HasDefaultValue("triggered");

        entity.HasOne(e => e.AlertExcursion)
            .WithMany(ex => ex.Instances)
            .HasForeignKey(e => e.AlertExcursionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertDeliveryEntityConfiguration : IEntityTypeConfiguration<AlertDeliveryEntity>
{
    public void Configure(EntityTypeBuilder<AlertDeliveryEntity> entity)
    {
        // Pending delivery sweep
        entity
            .HasIndex(e => new { e.Status, e.CreatedAt })
            .HasDatabaseName("ix_alert_deliveries_status_created_at");

        entity.ToTable("alert_deliveries");
        entity.Property(e => e.Payload).HasColumnType("jsonb").HasDefaultValue("{}");
        entity.Property(e => e.Status).HasDefaultValue("pending");
        entity.Property(e => e.ChannelType).HasConversion(
            new Converters.EnumMemberValueConverter<Core.Models.Alerts.ChannelType>());
        entity.Property(e => e.RetryCount).HasDefaultValue(0);

        entity.HasOne(e => e.AlertInstance)
            .WithMany()
            .HasForeignKey(e => e.AlertInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.AlertRuleChannel)
            .WithMany()
            .HasForeignKey(e => e.AlertRuleChannelId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AlertInviteEntityConfiguration : IEntityTypeConfiguration<AlertInviteEntity>
{
    public void Configure(EntityTypeBuilder<AlertInviteEntity> entity)
    {
        // Unique invite token lookup
        entity
            .HasIndex(e => e.Token)
            .HasDatabaseName("ix_alert_invites_token")
            .IsUnique();

        entity.ToTable("alert_invites");
        entity.Property(e => e.PermissionScope).HasDefaultValue("view_acknowledge");

        entity.HasOne(e => e.AlertRuleChannel)
            .WithMany()
            .HasForeignKey(e => e.AlertRuleChannelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertCustomSoundEntityConfiguration : IEntityTypeConfiguration<AlertCustomSoundEntity>
{
    public void Configure(EntityTypeBuilder<AlertCustomSoundEntity> entity)
    {
        entity.ToTable("alert_custom_sounds");
    }
}

internal sealed class AlertRuleChannelEntityConfiguration : IEntityTypeConfiguration<AlertRuleChannelEntity>
{
    public void Configure(EntityTypeBuilder<AlertRuleChannelEntity> entity)
    {
        entity.ToTable("alert_rule_channels");
        entity.Property(e => e.ChannelType).HasConversion(
            new Converters.EnumMemberValueConverter<Core.Models.Alerts.ChannelType>());

        entity.HasOne(e => e.AlertRule)
            .WithMany(r => r.Channels)
            .HasForeignKey(e => e.AlertRuleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TenantAlertSettingsEntityConfiguration : IEntityTypeConfiguration<TenantAlertSettingsEntity>
{
    public void Configure(EntityTypeBuilder<TenantAlertSettingsEntity> entity)
    {
        entity.ToTable("tenant_alert_settings");
        // Unique on TenantId enforces the one-row-per-tenant invariant. Named explicitly
        // so it isn't merged with the FK-driven auto-index on tenant_id.
        entity.HasIndex(e => e.TenantId)
            .IsUnique()
            .HasDatabaseName("IX_tenant_alert_settings_tenant_id_unique");
    }
}

internal sealed class DndWindowEntityConfiguration : IEntityTypeConfiguration<DndWindowEntity>
{
    public void Configure(EntityTypeBuilder<DndWindowEntity> entity)
    {
        entity.ToTable("dnd_windows");
        // Id is client-supplied so an offline-authored window re-syncs idempotently; the
        // conventional v7 default only applies if a caller omits one.
        entity.Property(e => e.Scope).HasConversion(
            new Converters.EnumMemberValueConverter<Core.Models.Alerts.DndScope>());
        // Scope-keyed lookups for the gate/supersede only ever read uncleared windows,
        // so a partial index over active windows (WHERE cleared_at IS NULL) keeps the
        // cleared/expired audit history out of the hot path (ADR 0004 D5).
        entity.HasIndex(e => new { e.TenantId, e.Scope })
            .HasFilter("cleared_at IS NULL");
    }
}

internal sealed class ClientDeviceEntityConfiguration : IEntityTypeConfiguration<ClientDeviceEntity>
{
    public void Configure(EntityTypeBuilder<ClientDeviceEntity> entity)
    {
        // Unique per install within a tenant — the upsert key.
        entity
            .HasIndex(e => new { e.TenantId, e.InstallId })
            .HasDatabaseName("ix_client_devices_tenant_install")
            .IsUnique();

        // Fan-out resolution: "all devices of this kind in the tenant".
        entity
            .HasIndex(e => new { e.TenantId, e.Kind })
            .HasDatabaseName("ix_client_devices_tenant_kind");

        // Subject-scoped intent delivery and listing a user's own devices.
        entity
            .HasIndex(e => new { e.TenantId, e.SubjectId })
            .HasDatabaseName("ix_client_devices_tenant_subject");

        entity.ToTable("client_devices");
        entity.Property(e => e.Capabilities).HasColumnType("text[]");

        // Fires only on a hard grant delete; a revoke stages removal via ClientDeviceGrantCascade.
        entity.HasOne<OAuthGrantEntity>()
            .WithMany()
            .HasForeignKey(e => e.GrantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
