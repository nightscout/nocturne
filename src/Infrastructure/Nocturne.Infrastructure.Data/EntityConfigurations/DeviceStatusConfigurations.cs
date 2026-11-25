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
        // The newest snapshot of one uploader type is read on every alert pass of a tenant with
        // a transmitter rule; see UploaderSnapshotRepository.GetLatestOfTypeAsync. Without type
        // in an index, a tenant with no such snapshot has every one it holds read to find none.
        entity
            .HasIndex(e => new { e.TenantId, e.Type, e.Timestamp })
            .HasDatabaseName("ix_uploader_snapshots_tenant_type_timestamp")
            .IsDescending(false, false, true)
            .HasFilter("deleted_at IS NULL");

        entity
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class SensorStateReportEntityConfiguration : IEntityTypeConfiguration<SensorStateReportEntity>
{
    public void Configure(EntityTypeBuilder<SensorStateReportEntity> entity)
    {
        // An alert pass that reads the sensor's state reads the newest recent report of the newest
        // reading's device and source; see SensorStateReportRepository.GetNewestAsync.
        entity
            .HasIndex(e => new { e.TenantId, e.DataSource, e.Device, e.Timestamp })
            .HasDatabaseName("ix_sensor_state_reports_tenant_source_device_timestamp")
            .IsDescending(false, false, false, true)
            .HasFilter("deleted_at IS NULL");
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
