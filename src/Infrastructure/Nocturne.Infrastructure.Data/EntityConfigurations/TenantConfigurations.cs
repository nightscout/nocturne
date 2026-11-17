using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class TenantEntityConfiguration : IEntityTypeConfiguration<TenantEntity>
{
    public void Configure(EntityTypeBuilder<TenantEntity> entity)
    {
        entity
            .HasIndex(t => t.Slug)
            .HasDatabaseName("ix_tenants_slug")
            .IsUnique();

        // Unique share token for public dashboard resolution. Postgres allows multiple
        // NULLs in a unique index, so tenants without sharing enabled don't collide.
        entity
            .HasIndex(t => t.ShareToken)
            .HasDatabaseName("ix_tenants_share_token")
            .IsUnique();

        entity
            .Property(t => t.PatientRelationship)
            .HasConversion<string>();

        // Signal loss sweep: find tenants that haven't reported recently
        entity
            .HasIndex(t => t.LastReadingAt)
            .HasDatabaseName("ix_tenants_last_reading_at");
    }
}

internal sealed class TenantMemberEntityConfiguration : IEntityTypeConfiguration<TenantMemberEntity>
{
    public void Configure(EntityTypeBuilder<TenantMemberEntity> entity)
    {
        entity
            .HasIndex(tm => tm.SubjectId)
            .HasDatabaseName("ix_tenant_members_subject_id");

        entity
            .HasOne(tm => tm.Tenant)
            .WithMany(t => t.Members)
            .HasForeignKey(tm => tm.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(tm => tm.Subject)
            .WithMany()
            .HasForeignKey(tm => tm.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(e => e.CreatedFromInvite)
            .WithMany(i => i.CreatedMembers)
            .HasForeignKey(e => e.CreatedFromInviteId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasIndex(e => new { e.TenantId, e.SubjectId })
            .HasDatabaseName("ix_tenant_members_tenant_subject")
            .IsUnique();

        entity
            .HasIndex(e => new { e.TenantId, e.Username })
            .HasDatabaseName("ix_tenant_members_tenant_username")
            .IsUnique()
            .HasFilter("username IS NOT NULL");
    }
}

internal sealed class TenantRoleEntityConfiguration : IEntityTypeConfiguration<TenantRoleEntity>
{
    public void Configure(EntityTypeBuilder<TenantRoleEntity> entity)
    {
        entity.HasIndex(e => new { e.TenantId, e.Slug }).IsUnique();
    }
}

internal sealed class TenantMemberRoleEntityConfiguration : IEntityTypeConfiguration<TenantMemberRoleEntity>
{
    public void Configure(EntityTypeBuilder<TenantMemberRoleEntity> entity)
    {
        entity.HasIndex(e => new { e.TenantMemberId, e.TenantRoleId }).IsUnique();
    }
}

internal sealed class MemberInviteEntityConfiguration : IEntityTypeConfiguration<MemberInviteEntity>
{
    public void Configure(EntityTypeBuilder<MemberInviteEntity> entity)
    {
        // The tenant relationship is configured centrally for every ITenantScoped entity by
        // ConfigureTenantCascadeDeletes, which binds the Tenant navigation.
        entity.HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedBySubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasIndex(e => e.TenantId);
    }
}

internal sealed class MembershipRequestEntityConfiguration : IEntityTypeConfiguration<MembershipRequestEntity>
{
    public void Configure(EntityTypeBuilder<MembershipRequestEntity> entity)
    {
        entity.HasIndex(e => new { e.TenantId, e.SubjectId })
            .HasFilter("status = 'pending'")
            .IsUnique();
    }
}

internal sealed class TenantCreationInviteEntityConfiguration : IEntityTypeConfiguration<TenantCreationInviteEntity>
{
    public void Configure(EntityTypeBuilder<TenantCreationInviteEntity> entity)
    {
        entity.HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedBySubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Uses)
            .WithOne()
            .HasForeignKey(u => u.InviteId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.TokenHash).IsUnique();
    }
}

internal sealed class TenantCreationInviteUseEntityConfiguration : IEntityTypeConfiguration<TenantCreationInviteUseEntity>
{
    public void Configure(EntityTypeBuilder<TenantCreationInviteUseEntity> entity)
    {
        entity.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.InviteId);
    }
}
