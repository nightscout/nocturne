using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Maps between <see cref="DeviceClockObservationEntity"/> and the domain
/// <see cref="DeviceClockObservation"/>.
/// </summary>
public static class DeviceClockObservationMapper
{
    public static DeviceClockObservation ToDomainModel(DeviceClockObservationEntity entity) => new()
    {
        Connector = entity.Connector,
        Source = (DeviceClockObservationSource)entity.Source,
        ObservedAtUtc = DateTime.SpecifyKind(entity.ObservedAt, DateTimeKind.Utc),
        OffsetMinutes = entity.OffsetMinutes,
        IsEstimate = entity.IsEstimate,
        SampleCount = entity.SampleCount,
        CoversFromUtc = entity.CoversFrom is { } covers ? DateTime.SpecifyKind(covers, DateTimeKind.Utc) : null,
        DeclaredTimezone = entity.DeclaredTimezone,
        AppliedAtUtc = entity.AppliedAt is { } applied ? DateTime.SpecifyKind(applied, DateTimeKind.Utc) : null,
    };

    public static DeviceClockObservationEntity ToEntity(DeviceClockObservation model, Guid tenantId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Connector = model.Connector,
        Source = (int)model.Source,
        ObservedAt = DateTime.SpecifyKind(model.ObservedAtUtc, DateTimeKind.Utc),
        OffsetMinutes = model.OffsetMinutes,
        IsEstimate = model.IsEstimate,
        SampleCount = model.SampleCount,
        CoversFrom = model.CoversFromUtc is { } covers ? DateTime.SpecifyKind(covers, DateTimeKind.Utc) : null,
        DeclaredTimezone = model.DeclaredTimezone,
        CreatedAt = DateTime.UtcNow,
    };
}
