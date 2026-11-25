using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class RejectedUploadEntityConfiguration : IEntityTypeConfiguration<RejectedUploadEntity>
{
    public void Configure(EntityTypeBuilder<RejectedUploadEntity> entity)
    {
        entity
            .HasIndex(e => new { e.TenantId, e.RecordType, e.Identity })
            .HasDatabaseName("ux_rejected_uploads_tenant_record_identity")
            .IsUnique();

        entity
            .HasIndex(e => new { e.TenantId, e.LastSeenAt })
            .HasDatabaseName("ix_rejected_uploads_tenant_last_seen_at");
    }
}
