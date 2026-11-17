using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class AuthAuditLogEntityConfiguration : IEntityTypeConfiguration<AuthAuditLogEntity>
{
    public void Configure(EntityTypeBuilder<AuthAuditLogEntity> entity)
    {
        entity
            .HasIndex(a => a.TargetSubjectId)
            .HasDatabaseName("ix_auth_audit_log_target_subject_id");

        entity
            .HasIndex(a => a.EventType)
            .HasDatabaseName("ix_auth_audit_log_event_type");

        entity
            .HasIndex(a => a.CreatedAt)
            .HasDatabaseName("ix_auth_audit_log_created_at")
            .IsDescending();

        entity
            .HasIndex(a => a.IpAddress)
            .HasDatabaseName("ix_auth_audit_log_ip_address");

        entity
            .HasIndex(a => new { a.TargetSubjectId, a.CreatedAt })
            .HasDatabaseName("ix_auth_audit_log_target_subject_created")
            .IsDescending(false, true);

        entity
            .HasIndex(a => new { a.ActorSubjectId, a.CreatedAt })
            .HasDatabaseName("ix_auth_audit_log_actor_subject_created")
            .IsDescending(false, true);

        entity
            .HasIndex(a => new { a.ActorCredential, a.CreatedAt })
            .HasDatabaseName("ix_auth_audit_log_actor_credential_created")
            .IsDescending(false, true);

        entity
            .HasIndex(a => new { a.TenantId, a.CreatedAt })
            .HasDatabaseName("ix_auth_audit_log_tenant_created")
            .IsDescending(false, true);

        entity.Property(e => e.Success).HasDefaultValue(true);

        entity
            .HasOne(e => e.RefreshToken)
            .WithMany()
            .HasForeignKey(e => e.RefreshTokenId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class MutationAuditLogEntityConfiguration : IEntityTypeConfiguration<MutationAuditLogEntity>
{
    public void Configure(EntityTypeBuilder<MutationAuditLogEntity> entity)
    {
        entity.HasIndex(e => new { e.TenantId, e.EntityType, e.EntityId })
            .HasDatabaseName("ix_mutation_audit_log_entity");

        entity.HasIndex(e => new { e.TenantId, e.ActorSubjectId, e.CreatedAt })
            .HasDatabaseName("ix_mutation_audit_log_actor_subject");

        entity.HasIndex(e => new { e.TenantId, e.CreatedAt })
            .HasDatabaseName("ix_mutation_audit_log_created");
    }
}

internal sealed class ReadAccessLogEntityConfiguration : IEntityTypeConfiguration<ReadAccessLogEntity>
{
    public void Configure(EntityTypeBuilder<ReadAccessLogEntity> entity)
    {
        entity.HasIndex(e => new { e.TenantId, e.ActorSubjectId, e.CreatedAt })
            .HasDatabaseName("ix_read_access_log_actor_subject");

        entity.HasIndex(e => new { e.TenantId, e.EntityType, e.CreatedAt })
            .HasDatabaseName("ix_read_access_log_entity_type");

        entity.HasIndex(e => new { e.TenantId, e.CreatedAt })
            .HasDatabaseName("ix_read_access_log_created");
    }
}

internal sealed class TenantAuditConfigEntityConfiguration : IEntityTypeConfiguration<TenantAuditConfigEntity>
{
    public void Configure(EntityTypeBuilder<TenantAuditConfigEntity> entity)
    {
        entity.Property(e => e.ReadAuditEnabled).HasDefaultValue(false);

        entity.HasIndex(e => e.TenantId)
            .IsUnique()
            .HasDatabaseName("ix_tenant_audit_config_tenant_id");
    }
}

internal sealed class TenantDataRetentionConfigEntityConfiguration : IEntityTypeConfiguration<TenantDataRetentionConfigEntity>
{
    public void Configure(EntityTypeBuilder<TenantDataRetentionConfigEntity> entity)
    {
        entity.HasIndex(e => e.TenantId)
            .IsUnique()
            .HasDatabaseName("ix_tenant_data_retention_config_tenant_id");
    }
}
