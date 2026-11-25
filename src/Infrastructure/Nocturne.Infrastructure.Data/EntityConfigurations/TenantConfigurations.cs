using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Core.Models.Identity;
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

        entity
            .HasOne<SubjectEntity>()
            .WithMany()
            .HasForeignKey(t => t.SetupOwnerSubjectId)
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasIndex(t => t.SetupOwnerSubjectId)
            .HasDatabaseName("ix_tenants_setup_owner_subject_id");
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
            .HasIndex(tm => tm.NextReasonLapseAt)
            .HasDatabaseName("ix_tenant_members_next_reason_lapse_at")
            .HasFilter("next_reason_lapse_at IS NOT NULL");

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

internal sealed class TenantMemberReasonEntityConfiguration : IEntityTypeConfiguration<TenantMemberReasonEntity>
{
    public void Configure(EntityTypeBuilder<TenantMemberReasonEntity> entity)
    {
        entity
            .HasOne(e => e.TenantMember)
            .WithMany(m => m.Reasons)
            .HasForeignKey(e => e.TenantMemberId)
            .OnDelete(DeleteBehavior.Cascade);

        // No action, so deleting a team's access that a reason still names fails: whatever stops the
        // access must take its reasons away first. ClientNoAction keeps EF from nulling a tracked
        // reason's source to let the delete through.
        entity
            .HasOne(e => e.TeamAccess)
            .WithMany()
            .HasForeignKey(e => e.SourceId)
            .OnDelete(DeleteBehavior.ClientNoAction);

        entity.ToTable(t => t.HasCheckConstraint(
            "ck_tenant_member_reasons_team_source",
            $"(kind = '{MembershipReasonKind.Team}') = (source_id IS NOT NULL)"));

        entity
            .HasOne(e => e.CreatedFromInvite)
            .WithMany()
            .HasForeignKey(e => e.CreatedFromInviteId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.TenantMemberId, "ix_tenant_member_reasons_tenant_member_id");

        entity
            .HasIndex(e => e.TenantMemberId, "ix_tenant_member_reasons_one_direct")
            .IsUnique()
            .HasFilter($"kind = '{MembershipReasonKind.Direct}'");

        entity
            .HasIndex(e => new { e.TenantMemberId, e.Kind, e.SourceId }, "ix_tenant_member_reasons_one_per_source")
            .IsUnique()
            .HasFilter("source_id IS NOT NULL");
    }
}

internal sealed class TenantMemberReasonRoleEntityConfiguration : IEntityTypeConfiguration<TenantMemberReasonRoleEntity>
{
    public void Configure(EntityTypeBuilder<TenantMemberReasonRoleEntity> entity)
    {
        entity
            .HasOne(e => e.TenantMemberReason)
            .WithMany(r => r.ReasonRoles)
            .HasForeignKey(e => e.TenantMemberReasonId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(e => e.TenantRole)
            .WithMany()
            .HasForeignKey(e => e.TenantRoleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.TenantMemberReasonId, e.TenantRoleId }).IsUnique();
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
