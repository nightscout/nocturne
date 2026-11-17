using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class SystemEventEntityConfiguration : IEntityTypeConfiguration<SystemEventEntity>
{
    public void Configure(EntityTypeBuilder<SystemEventEntity> entity)
    {
        entity
            .HasIndex(e => e.Mills)
            .HasDatabaseName("ix_system_events_mills")
            .IsDescending();

        entity
            .HasIndex(e => e.EventType)
            .HasDatabaseName("ix_system_events_event_type");

        entity
            .HasIndex(e => e.Category)
            .HasDatabaseName("ix_system_events_category");

        entity
            .HasIndex(e => new { e.Category, e.Mills })
            .HasDatabaseName("ix_system_events_category_timestamp")
            .IsDescending(false, true);

        entity
            .HasIndex(e => e.Source)
            .HasDatabaseName("ix_system_events_source");

        entity
            .HasIndex(e => e.OriginalId)
            .HasDatabaseName("ix_system_events_original_id");

        NocturneDbContext.ScopeKeyToTenant(entity);
    }
}
