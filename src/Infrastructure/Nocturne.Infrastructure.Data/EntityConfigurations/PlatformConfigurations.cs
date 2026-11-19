using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class PlatformSettingsEntityConfiguration : IEntityTypeConfiguration<PlatformSettingsEntity>
{
    public void Configure(EntityTypeBuilder<PlatformSettingsEntity> entity)
    {
        entity
            .HasIndex(ps => ps.Category)
            .HasDatabaseName("ix_platform_settings_category")
            .IsUnique();
    }
}

internal sealed class PlatformPolicyEntityConfiguration : IEntityTypeConfiguration<PlatformPolicyEntity>
{
    public void Configure(EntityTypeBuilder<PlatformPolicyEntity> entity)
    {
        entity
            .Property(p => p.Id)
            .ValueGeneratedNever();
    }
}

internal sealed class OwnerNoticeEntityConfiguration : IEntityTypeConfiguration<OwnerNoticeEntity>
{
    public void Configure(EntityTypeBuilder<OwnerNoticeEntity> entity)
    {
        entity
            .HasIndex(n => new { n.Kind, n.Version, n.TenantId, n.SubjectId })
            .HasDatabaseName("ux_owner_notices_kind_version_tenant_subject")
            .IsUnique();

        entity
            .HasOne<TenantEntity>()
            .WithMany()
            .HasForeignKey(n => n.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InstanceKeyCheckEntityConfiguration : IEntityTypeConfiguration<InstanceKeyCheckEntity>
{
    public void Configure(EntityTypeBuilder<InstanceKeyCheckEntity> entity)
    {
        entity
            .Property(c => c.Id)
            .ValueGeneratedNever();
    }
}
