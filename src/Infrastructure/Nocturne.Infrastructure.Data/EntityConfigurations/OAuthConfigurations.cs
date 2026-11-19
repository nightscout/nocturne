using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class OAuthClientEntityConfiguration : IEntityTypeConfiguration<OAuthClientEntity>
{
    public void Configure(EntityTypeBuilder<OAuthClientEntity> entity)
    {
        entity
            .HasIndex(c => new { c.TenantId, c.ClientId })
            .HasDatabaseName("ix_oauth_clients_tenant_client_id")
            .IsUnique();

        entity
            .HasIndex(c => new { c.TenantId, c.SoftwareId })
            .HasDatabaseName("ix_oauth_clients_tenant_software_id")
            .HasFilter("\"software_id\" IS NOT NULL");

        entity.Property(e => e.RedirectUris).HasDefaultValue("[]");
        entity.Property(e => e.IsKnown).HasDefaultValue(false);
    }
}

internal sealed class OAuthGrantEntityConfiguration : IEntityTypeConfiguration<OAuthGrantEntity>
{
    public void Configure(EntityTypeBuilder<OAuthGrantEntity> entity)
    {
        entity
            .HasIndex(g => g.ClientEntityId)
            .HasDatabaseName("ix_oauth_grants_client_id");

        entity
            .HasIndex(g => g.SubjectId)
            .HasDatabaseName("ix_oauth_grants_subject_id");

        entity
            .HasIndex(g => new { g.ClientEntityId, g.SubjectId })
            .HasDatabaseName("ix_oauth_grants_client_subject");

        entity
            .HasIndex(g => new { g.TenantId, g.SubjectId })
            .HasDatabaseName("ix_oauth_grants_tenant_subject");

        entity
            .HasIndex(g => g.RevokedAt)
            .HasDatabaseName("ix_oauth_grants_revoked_at")
            .HasFilter("revoked_at IS NULL");

        // Every request carrying an opaque credential hashes it and looks for this, so without the
        // index each one scans the tenant's grants. Mirrors ix_oauth_grants_tenant_legacy_secret_hash,
        // which answers the same question for the api-secret spelling of the same credential.
        // Not unique: one Nightscout token imported into two tenants is two grants sharing a hash,
        // and the lookup is tenant-scoped anyway.
        entity
            .HasIndex(g => new { g.TenantId, g.TokenHash })
            .HasDatabaseName("ix_oauth_grants_tenant_token_hash")
            .HasFilter("token_hash IS NOT NULL");

        // A legacy Nightscout subject token is matched by digest prefix, not equality. A default
        // btree on a collated text column cannot answer LIKE 'abc%', so without varchar_pattern_ops
        // this index is unusable and an unauthenticated ?token= miss becomes a sequential scan of
        // every grant on the instance. Filtered to the small imported-token population.
        entity
            .HasIndex(g => g.LegacyTokenDigest)
            .HasDatabaseName("ix_oauth_grants_legacy_token_digest")
            .HasOperators("varchar_pattern_ops")
            .HasFilter("legacy_token_digest IS NOT NULL");

        entity.Property(e => e.GrantType).HasDefaultValue(OAuthGrantTypes.App);

        entity
            .HasOne(e => e.Client)
            .WithMany(c => c.Grants)
            .HasForeignKey(e => e.ClientEntityId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired(false);

        entity
            .HasOne(e => e.Subject)
            .WithMany()
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedBySubjectId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
    }
}

internal sealed class OAuthRefreshTokenEntityConfiguration : IEntityTypeConfiguration<OAuthRefreshTokenEntity>
{
    public void Configure(EntityTypeBuilder<OAuthRefreshTokenEntity> entity)
    {
        entity
            .HasIndex(t => t.TokenHash)
            .HasDatabaseName("ix_oauth_refresh_tokens_token_hash")
            .IsUnique();

        entity
            .HasIndex(t => t.GrantId)
            .HasDatabaseName("ix_oauth_refresh_tokens_grant_id");

        entity
            .HasIndex(t => t.ExpiresAt)
            .HasDatabaseName("ix_oauth_refresh_tokens_expires_at");

        entity
            .HasIndex(t => t.RevokedAt)
            .HasDatabaseName("ix_oauth_refresh_tokens_revoked_at")
            .HasFilter("revoked_at IS NULL");

        entity
            .HasOne<TenantEntity>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(e => e.Grant)
            .WithMany(g => g.RefreshTokens)
            .HasForeignKey(e => e.GrantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(e => e.ReplacedBy)
            .WithMany()
            .HasForeignKey(e => e.ReplacedById)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class OAuthDeviceCodeEntityConfiguration : IEntityTypeConfiguration<OAuthDeviceCodeEntity>
{
    public void Configure(EntityTypeBuilder<OAuthDeviceCodeEntity> entity)
    {
        entity
            .HasIndex(d => d.DeviceCodeHash)
            .HasDatabaseName("ix_oauth_device_codes_device_code_hash")
            .IsUnique();

        entity
            .HasIndex(d => d.UserCode)
            .HasDatabaseName("ix_oauth_device_codes_user_code")
            .IsUnique();

        entity
            .HasIndex(d => d.ExpiresAt)
            .HasDatabaseName("ix_oauth_device_codes_expires_at");

        entity.Property(e => e.Interval).HasDefaultValue(5);

        entity
            .HasOne(e => e.Grant)
            .WithMany()
            .HasForeignKey(e => e.GrantId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class OAuthAuthorizationCodeEntityConfiguration : IEntityTypeConfiguration<OAuthAuthorizationCodeEntity>
{
    public void Configure(EntityTypeBuilder<OAuthAuthorizationCodeEntity> entity)
    {
        entity
            .HasIndex(c => c.CodeHash)
            .HasDatabaseName("ix_oauth_authorization_codes_code_hash")
            .IsUnique();

        entity
            .HasIndex(c => c.ExpiresAt)
            .HasDatabaseName("ix_oauth_authorization_codes_expires_at");

        entity
            .HasIndex(c => c.SubjectId)
            .HasDatabaseName("ix_oauth_authorization_codes_subject_id");

        entity
            .HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        entity
            .HasOne(e => e.Subject)
            .WithMany()
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
