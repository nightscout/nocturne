using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class TimezoneTimelineEntityConfiguration : IEntityTypeConfiguration<TimezoneTimelineEntity>
{
    public void Configure(EntityTypeBuilder<TimezoneTimelineEntity> entity)
    {
        // TimezoneTimeline: one zone-change boundary per tenant per instant (the ordered list is
        // inherently non-overlapping, so a duplicate effective_from is an authoring error).
        entity
            .HasIndex(e => new { e.TenantId, e.EffectiveFrom })
            .HasDatabaseName("ix_timezone_timeline_tenant_effective_from")
            .IsUnique();
    }
}

internal sealed class DeviceClockObservationEntityConfiguration : IEntityTypeConfiguration<DeviceClockObservationEntity>
{
    public void Configure(EntityTypeBuilder<DeviceClockObservationEntity> entity)
    {
        // DeviceClockObservations: an observation is keyed by what was observed and when — the same
        // evidence re-gathered on a later sync must upsert into the same row, not accumulate.
        entity
            .HasIndex(e => new { e.TenantId, e.Connector, e.Source, e.ObservedAt })
            .HasDatabaseName("ix_device_clock_observations_tenant_connector_source_observed")
            .IsUnique();
    }
}
