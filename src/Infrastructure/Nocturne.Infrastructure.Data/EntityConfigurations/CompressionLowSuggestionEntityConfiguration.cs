using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class CompressionLowSuggestionEntityConfiguration : IEntityTypeConfiguration<CompressionLowSuggestionEntity>
{
    public void Configure(EntityTypeBuilder<CompressionLowSuggestionEntity> entity)
    {
        entity
            .HasIndex(e => e.NightOf)
            .HasDatabaseName("ix_compression_low_suggestions_night_of");

        entity
            .HasIndex(e => e.Status)
            .HasDatabaseName("ix_compression_low_suggestions_status");
    }
}
