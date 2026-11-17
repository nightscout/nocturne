using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class DiscrepancyAnalysisEntityConfiguration : IEntityTypeConfiguration<DiscrepancyAnalysisEntity>
{
    public void Configure(EntityTypeBuilder<DiscrepancyAnalysisEntity> entity)
    {
        entity
            .HasIndex(d => d.AnalysisTimestamp)
            .HasDatabaseName("ix_discrepancy_analyses_timestamp")
            .IsDescending(); // Most recent first

        entity
            .HasIndex(d => d.TraceId)
            .HasDatabaseName("ix_discrepancy_analyses_correlation_id");

        entity
            .HasIndex(d => d.RequestPath)
            .HasDatabaseName("ix_discrepancy_analyses_request_path");

        entity
            .HasIndex(d => d.OverallMatch)
            .HasDatabaseName("ix_discrepancy_analyses_overall_match");

        entity
            .HasIndex(d => new { d.RequestPath, d.AnalysisTimestamp })
            .HasDatabaseName("ix_discrepancy_analyses_path_timestamp")
            .IsDescending(false, true); // Path asc, Timestamp desc
    }
}

internal sealed class DiscrepancyDetailEntityConfiguration : IEntityTypeConfiguration<DiscrepancyDetailEntity>
{
    public void Configure(EntityTypeBuilder<DiscrepancyDetailEntity> entity)
    {
        entity
            .HasIndex(d => d.AnalysisId)
            .HasDatabaseName("ix_discrepancy_details_analysis_id");

        entity
            .HasIndex(d => d.Severity)
            .HasDatabaseName("ix_discrepancy_details_severity");

        entity
            .HasIndex(d => d.DiscrepancyType)
            .HasDatabaseName("ix_discrepancy_details_type");
    }
}
