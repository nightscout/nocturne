using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class SettingsEntityConfiguration : IEntityTypeConfiguration<SettingsEntity>
{
    public void Configure(EntityTypeBuilder<SettingsEntity> entity)
    {
        entity
            .HasIndex(s => new { s.TenantId, s.Key })
            .HasDatabaseName("ix_settings_tenant_id_key")
            .IsUnique(); // Settings keys should be unique per tenant

        entity
            .HasIndex(s => s.Mills)
            .HasDatabaseName("ix_settings_mills")
            .IsDescending(); // Most recent first

        entity
            .HasIndex(s => s.IsActive)
            .HasDatabaseName("ix_settings_is_active");

        entity
            .HasIndex(s => s.SysCreatedAt)
            .HasDatabaseName("ix_settings_sys_created_at");

        NocturneDbContext.ScopeKeyToTenant(entity);

        entity.Property(s => s.IsActive).HasDefaultValue(true);
    }
}
