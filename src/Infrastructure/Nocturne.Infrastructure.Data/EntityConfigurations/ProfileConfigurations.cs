using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class ProfileActivationEntityConfiguration : IEntityTypeConfiguration<ProfileActivationEntity>
{
    public void Configure(EntityTypeBuilder<ProfileActivationEntity> entity)
    {
        entity
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_profile_activations_tenant_start_timestamp")
            .IsDescending(false, true)
            .HasFilter("deleted_at IS NULL");

        entity
            .Property(e => e.Timestamp)
            .HasColumnName("start_timestamp");

        entity
            .HasOne<ProfileActivationEntity>()
            .WithMany()
            .HasForeignKey(e => e.RequestedActivationId)
            .OnDelete(DeleteBehavior.SetNull);

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

internal sealed class TherapySettingsEntityConfiguration : IEntityTypeConfiguration<TherapySettingsEntity>
{
    public void Configure(EntityTypeBuilder<TherapySettingsEntity> entity)
    {
        entity
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_therapy_settings_tenant_timestamp")
            .IsDescending(false, true);
    }
}
