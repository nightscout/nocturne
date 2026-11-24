using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class AppBuildLinkEntityConfiguration : IEntityTypeConfiguration<AppBuildLinkEntity>
{
    public void Configure(EntityTypeBuilder<AppBuildLinkEntity> entity)
    {
        entity
            .HasIndex(e => new { e.TenantId, e.TargetId })
            .HasDatabaseName("ux_app_build_links_tenant_target")
            .IsUnique();

        entity.Property(e => e.AdminRepositories).HasColumnType("text[]");
    }
}
