using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class ApsSnapshotEntityConfiguration : IEntityTypeConfiguration<ApsSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<ApsSnapshotEntity> entity)
    {
        // The closed-loop stamp bounds its range by the newest snapshot of one algorithm for one
        // tenant; see ApsSnapshotRepository.StampLoopClosedLoopAsync. Without aps_system in an
        // index, that max() reads every snapshot the tenant has.
        entity
            .HasIndex(e => new { e.TenantId, e.AidAlgorithm, e.Timestamp })
            .HasDatabaseName("ix_aps_snapshots_tenant_algorithm_timestamp")
            .IsDescending(false, false, true)
            .HasFilter("deleted_at IS NULL");

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

internal sealed class PumpSnapshotEntityConfiguration : IEntityTypeConfiguration<PumpSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<PumpSnapshotEntity> entity)
    {
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

internal sealed class UploaderSnapshotEntityConfiguration : IEntityTypeConfiguration<UploaderSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<UploaderSnapshotEntity> entity)
    {
        entity
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class DeviceStatusExtrasEntityConfiguration : IEntityTypeConfiguration<DeviceStatusExtrasEntity>
{
    public void Configure(EntityTypeBuilder<DeviceStatusExtrasEntity> entity)
    {
        // The device status list walks extras newest-first beside the snapshots; see
        // DeviceStatusGroupRepository.GetAsync.
        entity
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_device_status_extras_tenant_timestamp")
            .IsDescending(false, true)
            .HasFilter("deleted_at IS NULL");
    }
}
