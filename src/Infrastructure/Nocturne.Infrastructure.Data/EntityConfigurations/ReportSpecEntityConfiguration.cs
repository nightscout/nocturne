using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class ReportSpecEntityConfiguration : IEntityTypeConfiguration<ReportSpecEntity>
{
    public void Configure(EntityTypeBuilder<ReportSpecEntity> entity)
    {
        entity
            .HasIndex(e => new { e.TenantId, e.Name })
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ix_report_specs_tenant_name");
    }
}
