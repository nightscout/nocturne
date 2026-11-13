using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class DeviceEntityConfiguration : IEntityTypeConfiguration<DeviceEntity>
{
    public void Configure(EntityTypeBuilder<DeviceEntity> entity)
    {
        // Devices unique index (scoped to live records, per tenant).
        // TenantId must be part of the key: devices are tenant-owned (FindByIdentitiesAsync
        // is RLS-scoped to the current tenant) and the type/serial often carry shared, non-unique
        // values — e.g. a pump's manufacturer/model ("Insulet"/"Omnipod 5") or the generic Loop
        // uploader identity ("iPhone"/"unknown"). Without TenantId the constraint is global, so the
        // first tenant to register such a device permanently blocks every other tenant's insert,
        // surfacing as a 500 on devicestatus ingestion (and a network error in Loop).
        entity
            .HasIndex(e => new { e.TenantId, e.Category, e.Type, e.Serial })
            .HasDatabaseName("ix_devices_category_type_serial")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");
    }
}

internal sealed class PatientRecordEntityConfiguration : IEntityTypeConfiguration<PatientRecordEntity>
{
    public void Configure(EntityTypeBuilder<PatientRecordEntity> entity)
    {
        // PatientRecord: unique constraint — one record per tenant (scoped to live records)
        entity
            .HasIndex(e => e.TenantId)
            .HasDatabaseName("ix_patient_records_tenant_id")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");
    }
}

internal sealed class PatientDeviceEntityConfiguration : IEntityTypeConfiguration<PatientDeviceEntity>
{
    public void Configure(EntityTypeBuilder<PatientDeviceEntity> entity)
    {
        // PatientDevice: query by tenant + current status
        entity
            .HasIndex(e => new { e.TenantId, e.IsCurrent })
            .HasDatabaseName("ix_patient_devices_tenant_is_current");

        entity
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PatientInsulinEntityConfiguration : IEntityTypeConfiguration<PatientInsulinEntity>
{
    public void Configure(EntityTypeBuilder<PatientInsulinEntity> entity)
    {
        // PatientInsulin: query by tenant + current status
        entity
            .HasIndex(e => new { e.TenantId, e.IsCurrent })
            .HasDatabaseName("ix_patient_insulins_tenant_is_current");
    }
}
