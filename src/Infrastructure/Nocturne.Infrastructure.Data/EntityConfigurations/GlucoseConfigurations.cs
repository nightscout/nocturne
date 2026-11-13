using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class SensorGlucoseEntityConfiguration : IEntityTypeConfiguration<SensorGlucoseEntity>
{
    public void Configure(EntityTypeBuilder<SensorGlucoseEntity> entity)
    {
        entity
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasIndex(e => e.PatientDeviceId)
            .HasDatabaseName("ix_sensor_glucose_patient_device_id")
            .HasFilter("patient_device_id IS NOT NULL");

        // These also serve the dedup unlinked read in LinkedRecordTypes, which ranges on timestamp
        // over soft-deleted rows too and so cannot use the partial list indexes.
        entity
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_sensor_glucose_tenant_timestamp")
            .IsDescending(false, true);

        var secondary = entity.Property(e => e.IsSecondaryStream);
        secondary.HasDefaultValue(false).ValueGeneratedNever();
        secondary.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        secondary.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}

internal sealed class MeterGlucoseEntityConfiguration : IEntityTypeConfiguration<MeterGlucoseEntity>
{
    public void Configure(EntityTypeBuilder<MeterGlucoseEntity> entity)
    {
        entity
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasIndex(e => e.PatientDeviceId)
            .HasDatabaseName("ix_meter_glucose_patient_device_id")
            .HasFilter("patient_device_id IS NOT NULL");
    }
}
