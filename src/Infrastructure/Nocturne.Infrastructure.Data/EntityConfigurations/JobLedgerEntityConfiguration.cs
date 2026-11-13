using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class JobLedgerEntityConfiguration : IEntityTypeConfiguration<JobLedgerEntity>
{
    public void Configure(EntityTypeBuilder<JobLedgerEntity> entity)
    {
        // NULLS NOT DISTINCT, so a platform job's row (no tenant) is one row too.
        entity
            .HasIndex(j => new { j.Job, j.TenantId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_job_ledger_job_tenant");

        entity
            .HasIndex(j => j.NextDueAt)
            .HasFilter("next_due_at IS NOT NULL")
            .HasDatabaseName("ix_job_ledger_due");

        entity
            .HasOne<TenantEntity>()
            .WithMany()
            .HasForeignKey(j => j.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
