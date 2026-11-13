using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class SleepSessionEntityConfiguration : IEntityTypeConfiguration<SleepSessionEntity>
{
    public void Configure(EntityTypeBuilder<SleepSessionEntity> entity)
    {
        entity
            .HasIndex(s => new { s.TenantId, s.Source, s.OriginalId })
            .IsUnique()
            .HasFilter("original_id IS NOT NULL")
            .HasDatabaseName("ux_sleep_sessions_tenant_source_original");

        entity
            .HasIndex(s => new { s.TenantId, s.StartTime })
            .IsDescending(false, true)
            .HasDatabaseName("ix_sleep_sessions_tenant_start_time");

        NocturneDbContext.ScopeKeyToTenant(entity);
    }
}

internal sealed class SleepStageEntityConfiguration : IEntityTypeConfiguration<SleepStageEntity>
{
    public void Configure(EntityTypeBuilder<SleepStageEntity> entity)
    {
        entity
            .HasOne(s => s.SleepSession)
            .WithMany(ss => ss.Stages)
            .HasForeignKey(s => new { s.TenantId, s.SleepSessionId })
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasIndex(s => new { s.TenantId, s.SleepSessionId })
            .HasDatabaseName("ix_sleep_stages_tenant_id_sleep_session_id");
    }
}

internal sealed class SleepBiometricSampleEntityConfiguration : IEntityTypeConfiguration<SleepBiometricSampleEntity>
{
    public void Configure(EntityTypeBuilder<SleepBiometricSampleEntity> entity)
    {
        entity
            .HasOne(b => b.SleepSession)
            .WithMany(ss => ss.BiometricSamples)
            .HasForeignKey(b => new { b.TenantId, b.SleepSessionId })
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasIndex(b => new { b.TenantId, b.SleepSessionId })
            .HasDatabaseName("ix_sleep_biometric_samples_tenant_id_sleep_session_id");
    }
}
