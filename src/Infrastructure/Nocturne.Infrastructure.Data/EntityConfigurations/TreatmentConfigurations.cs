using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class BolusEntityConfiguration : IEntityTypeConfiguration<BolusEntity>
{
    public void Configure(EntityTypeBuilder<BolusEntity> entity)
    {
        entity
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_boluses_tenant_timestamp")
            .IsDescending(false, true);

        entity
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasOne<BolusCalculationEntity>()
            .WithMany()
            .HasForeignKey(e => e.BolusCalculationId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasOne<ApsSnapshotEntity>()
            .WithMany()
            .HasForeignKey(e => e.ApsSnapshotId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class BasalInjectionEntityConfiguration : IEntityTypeConfiguration<BasalInjectionEntity>
{
    public void Configure(EntityTypeBuilder<BasalInjectionEntity> entity)
    {
        entity
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasIndex(e => e.PatientDeviceId)
            .HasDatabaseName("ix_basal_injections_patient_device_id")
            .HasFilter("patient_device_id IS NOT NULL");
    }
}

internal sealed class CarbIntakeEntityConfiguration : IEntityTypeConfiguration<CarbIntakeEntity>
{
    public void Configure(EntityTypeBuilder<CarbIntakeEntity> entity)
    {
        entity
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_carb_intakes_tenant_timestamp")
            .IsDescending(false, true);
    }
}

internal sealed class DeviceEventEntityConfiguration : IEntityTypeConfiguration<DeviceEventEntity>
{
    public void Configure(EntityTypeBuilder<DeviceEventEntity> entity)
    {
        // Latest-of-a-kind lookups (device age, alert enrichment) filter on tenant + event type
        // and take the newest row. Without the event_type column the plan walks the whole
        // tenant's history backwards and never terminates early for a type that was never logged.
        entity
            .HasIndex(e => new { e.TenantId, e.EventType, e.Timestamp })
            .HasDatabaseName("ix_device_events_tenant_event_type_timestamp")
            .IsDescending(false, false, true);

        entity
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class TempBasalEntityConfiguration : IEntityTypeConfiguration<TempBasalEntity>
{
    public void Configure(EntityTypeBuilder<TempBasalEntity> entity)
    {
        entity
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_temp_basals_start_timestamp")
            .IsDescending();

        entity
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_temp_basals_tenant_start_timestamp")
            .IsDescending(false, true);

        entity
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasOne<ApsSnapshotEntity>()
            .WithMany()
            .HasForeignKey(e => e.ApsSnapshotId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class NoteEntityConfiguration : IEntityTypeConfiguration<NoteEntity>
{
    public void Configure(EntityTypeBuilder<NoteEntity> entity)
    {
        // The legacy treatment read stamps a record with the Note beside it, deleted or not, on every
        // page it serves, and both legacy-id indexes ModelConventions gives it skip a system-deleted Note.
        entity
            .HasIndex([nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId)], "ix_notes_tenant_legacy_id_any")
            .HasDatabaseName("ix_notes_tenant_legacy_id_any");
    }
}
