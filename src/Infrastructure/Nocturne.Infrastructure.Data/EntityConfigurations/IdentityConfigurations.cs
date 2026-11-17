using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class SubjectEntityConfiguration : IEntityTypeConfiguration<SubjectEntity>
{
    public void Configure(EntityTypeBuilder<SubjectEntity> entity)
    {
        entity
            .HasIndex(s => s.Name)
            .HasDatabaseName("ix_subjects_name");

        entity
            .HasIndex(s => s.Email)
            .HasDatabaseName("ix_subjects_email");

        entity.Property(e => e.IsActive).HasDefaultValue(true);

        // Per-user display preferences stored as a JSONB blob (semantic comparison is
        // applied by the relational jsonb-string value-comparer NocturneDbContext configures).
        entity.Property(e => e.Preferences).HasColumnType("jsonb");
    }
}

internal sealed class SubjectAvatarEntityConfiguration : IEntityTypeConfiguration<SubjectAvatarEntity>
{
    public void Configure(EntityTypeBuilder<SubjectAvatarEntity> entity)
    {
        entity.ToTable("subject_avatars");
        entity.HasIndex(e => e.SubjectId).IsUnique().HasDatabaseName("ix_subject_avatars_subject_id");
        entity.HasOne(e => e.Subject).WithMany().HasForeignKey(e => e.SubjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoleEntityConfiguration : IEntityTypeConfiguration<RoleEntity>
{
    public void Configure(EntityTypeBuilder<RoleEntity> entity)
    {
        entity
            .HasIndex(r => r.Name)
            .HasDatabaseName("ix_roles_name")
            .IsUnique();

        entity.Property(e => e.IsSystemRole).HasDefaultValue(false);
    }
}

internal sealed class SubjectRoleEntityConfiguration : IEntityTypeConfiguration<SubjectRoleEntity>
{
    public void Configure(EntityTypeBuilder<SubjectRoleEntity> entity)
    {
        entity.HasKey(e => new { e.SubjectId, e.RoleId });

        entity
            .HasOne(e => e.Subject)
            .WithMany(s => s.SubjectRoles)
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(e => e.Role)
            .WithMany(r => r.SubjectRoles)
            .HasForeignKey(e => e.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(e => e.AssignedBy)
            .WithMany()
            .HasForeignKey(e => e.AssignedById)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class OidcProviderEntityConfiguration : IEntityTypeConfiguration<OidcProviderEntity>
{
    public void Configure(EntityTypeBuilder<OidcProviderEntity> entity)
    {
        entity
            .HasIndex(o => o.IssuerUrl)
            .HasDatabaseName("ix_oidc_providers_issuer_url")
            .IsUnique();

        entity
            .HasIndex(o => o.IsEnabled)
            .HasDatabaseName("ix_oidc_providers_is_enabled");

        entity.Property(e => e.ClaimMappingsJson).HasDefaultValue("{}");
        entity.Property(e => e.IsEnabled).HasDefaultValue(true);
        entity.Property(e => e.DisplayOrder).HasDefaultValue(0);
    }
}

internal sealed class SubjectOidcIdentityEntityConfiguration : IEntityTypeConfiguration<SubjectOidcIdentityEntity>
{
    public void Configure(EntityTypeBuilder<SubjectOidcIdentityEntity> entity)
    {
        // Subject OIDC identities — join table for multi-provider OIDC linking
        entity.HasKey(x => x.Id);

        entity.HasIndex(x => new { x.OidcSubjectId, x.Issuer }).IsUnique()
            .HasDatabaseName("ix_subject_oidc_identities_external");
        entity.HasIndex(x => x.SubjectId)
            .HasDatabaseName("ix_subject_oidc_identities_subject_id");

        entity.HasOne(x => x.Subject)
            .WithMany(s => s.OidcIdentities)
            .HasForeignKey(x => x.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.Provider)
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenEntityConfiguration : IEntityTypeConfiguration<RefreshTokenEntity>
{
    public void Configure(EntityTypeBuilder<RefreshTokenEntity> entity)
    {
        entity
            .HasIndex(t => t.TokenHash)
            .HasDatabaseName("ix_refresh_tokens_token_hash")
            .IsUnique();

        entity
            .HasIndex(t => t.SubjectId)
            .HasDatabaseName("ix_refresh_tokens_subject_id");

        entity
            .HasIndex(t => t.OidcSessionId)
            .HasDatabaseName("ix_refresh_tokens_oidc_session_id");

        entity
            .HasIndex(t => t.ExpiresAt)
            .HasDatabaseName("ix_refresh_tokens_expires_at");

        entity
            .HasIndex(t => t.RevokedAt)
            .HasDatabaseName("ix_refresh_tokens_revoked_at")
            .HasFilter("revoked_at IS NULL");

        entity
            .HasOne(e => e.Subject)
            .WithMany(s => s.RefreshTokens)
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PasskeyCredentialEntityConfiguration : IEntityTypeConfiguration<PasskeyCredentialEntity>
{
    public void Configure(EntityTypeBuilder<PasskeyCredentialEntity> entity)
    {
        entity.HasIndex(e => e.CredentialId).IsUnique();
        entity.HasOne(e => e.Subject).WithMany(s => s.PasskeyCredentials).HasForeignKey(e => e.SubjectId);
    }
}

internal sealed class RecoveryCodeEntityConfiguration : IEntityTypeConfiguration<RecoveryCodeEntity>
{
    public void Configure(EntityTypeBuilder<RecoveryCodeEntity> entity)
    {
        entity.HasIndex(e => e.SubjectId);
        entity.HasOne(e => e.Subject).WithMany().HasForeignKey(e => e.SubjectId);
    }
}

internal sealed class TotpStepUpTokenEntityConfiguration : IEntityTypeConfiguration<TotpStepUpTokenEntity>
{
    public void Configure(EntityTypeBuilder<TotpStepUpTokenEntity> entity)
    {
        entity.HasIndex(e => e.SubjectId);
        // The cleanup sweep deletes by expiry.
        entity.HasIndex(e => e.ExpiresAt);
        entity.HasOne(e => e.Subject).WithMany().HasForeignKey(e => e.SubjectId);
    }
}

internal sealed class LoginCodeEntityConfiguration : IEntityTypeConfiguration<LoginCodeEntity>
{
    public void Configure(EntityTypeBuilder<LoginCodeEntity> entity)
    {
        entity.HasOne(e => e.Subject)
            .WithMany()
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(e => e.Purpose).HasConversion<string>();

        entity.HasIndex(e => e.CodeHash).IsUnique();
        entity.HasIndex(e => e.ExpiresAt);
    }
}
