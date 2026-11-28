using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class StepCountEntityConfiguration : IEntityTypeConfiguration<StepCountEntity>
{
    public void Configure(EntityTypeBuilder<StepCountEntity> entity)
    {
        entity
            .HasIndex(s => s.SysCreatedAt)
            .HasDatabaseName("ix_step_counts_sys_created_at");

        // The activity id a heart rate or step count was decomposed from, read (tombstones included)
        // on every activity write and connector reconcile.
        entity
            .HasIndex(s => new { s.TenantId, s.OriginalId })
            .HasDatabaseName("ix_step_counts_tenant_original_id")
            .HasFilter("original_id IS NOT NULL");

        NocturneDbContext.ScopeKeyToTenant(entity);
    }
}

internal sealed class HeartRateEntityConfiguration : IEntityTypeConfiguration<HeartRateEntity>
{
    public void Configure(EntityTypeBuilder<HeartRateEntity> entity)
    {
        entity
            .HasIndex(h => new { h.TenantId, h.OriginalId })
            .HasDatabaseName("ix_heart_rates_tenant_original_id")
            .HasFilter("original_id IS NOT NULL");

        entity
            .HasIndex(h => h.SysCreatedAt)
            .HasDatabaseName("ix_heart_rates_sys_created_at");

        NocturneDbContext.ScopeKeyToTenant(entity);
    }
}

internal sealed class BodyWeightEntityConfiguration : IEntityTypeConfiguration<BodyWeightEntity>
{
    public void Configure(EntityTypeBuilder<BodyWeightEntity> entity)
    {
        entity
            .HasIndex(b => b.Mills)
            .HasDatabaseName("ix_body_weights_mills")
            .IsDescending();

        entity
            .HasIndex(b => b.SysCreatedAt)
            .HasDatabaseName("ix_body_weights_sys_created_at");

        entity
            .HasIndex(b => new { b.TenantId, b.Mills })
            .HasDatabaseName("ix_body_weights_tenant_mills");

        NocturneDbContext.ScopeKeyToTenant(entity);
    }
}
