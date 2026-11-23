using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class TeamEntityConfiguration : IEntityTypeConfiguration<TeamEntity>
{
    public void Configure(EntityTypeBuilder<TeamEntity> entity)
    {
        entity.HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedBySubjectId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasMany(e => e.Members)
            .WithOne(m => m.Team)
            .HasForeignKey(m => m.TeamId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TeamMemberEntityConfiguration : IEntityTypeConfiguration<TeamMemberEntity>
{
    public void Configure(EntityTypeBuilder<TeamMemberEntity> entity)
    {
        entity.HasOne(e => e.Subject)
            .WithMany()
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.TeamId, e.SubjectId }).IsUnique();

        entity.HasIndex(e => e.SubjectId);
    }
}

internal sealed class TeamInviteEntityConfiguration : IEntityTypeConfiguration<TeamInviteEntity>
{
    public void Configure(EntityTypeBuilder<TeamInviteEntity> entity)
    {
        entity.HasOne(e => e.Team)
            .WithMany()
            .HasForeignKey(e => e.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedBySubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.TokenHash).IsUnique();
    }
}

internal sealed class TeamAccessLinkEntityConfiguration : IEntityTypeConfiguration<TeamAccessLinkEntity>
{
    public void Configure(EntityTypeBuilder<TeamAccessLinkEntity> entity)
    {
        entity.HasOne(e => e.Team)
            .WithMany()
            .HasForeignKey(e => e.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedBySubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.TokenHash).IsUnique();
    }
}

internal sealed class TeamTenantAccessEntityConfiguration : IEntityTypeConfiguration<TeamTenantAccessEntity>
{
    public void Configure(EntityTypeBuilder<TeamTenantAccessEntity> entity)
    {
        entity.HasOne(e => e.Team)
            .WithMany()
            .HasForeignKey(e => e.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.ApprovedBy)
            .WithMany()
            .HasForeignKey(e => e.ApprovedBySubjectId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.CreatedFromLink)
            .WithMany()
            .HasForeignKey(e => e.CreatedFromLinkId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasMany(e => e.AccessRoles)
            .WithOne(r => r.TeamTenantAccess)
            .HasForeignKey(r => r.TeamTenantAccessId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.TeamId, e.TenantId }).IsUnique();

        entity.HasIndex(e => e.TenantId);
    }
}

internal sealed class TeamTenantAccessRoleEntityConfiguration : IEntityTypeConfiguration<TeamTenantAccessRoleEntity>
{
    public void Configure(EntityTypeBuilder<TeamTenantAccessRoleEntity> entity)
    {
        entity.HasOne(e => e.TenantRole)
            .WithMany()
            .HasForeignKey(e => e.TenantRoleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.TeamTenantAccessId, e.TenantRoleId }).IsUnique();

        entity.HasIndex(e => e.TenantRoleId);
    }
}
