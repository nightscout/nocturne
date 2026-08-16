using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Pins the index backing <c>GetLatestByEventTypeAsync</c>/<c>GetLatestByEventTypesAsync</c>.
/// Those lookups run once per device-age slot on every properties request, and the tenant/event-type
/// prefix is what lets Postgres return immediately for an event type the tenant has never logged
/// instead of walking its whole history backwards.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceEventIndexTests
{
    [Fact]
    public void LatestByEventTypeLookup_IsBackedByATenantEventTypeTimestampIndex()
    {
        using var ctx = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
                .Options)
        { TenantId = Guid.NewGuid() };

        // Column sort direction only survives on the design-time model; the runtime model drops it.
        var index = ctx.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(DeviceEventEntity))!
            .GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "ix_device_events_tenant_event_type_timestamp");

        index.Should().NotBeNull();
        index!.Properties.Select(p => p.Name).Should().Equal(
            nameof(DeviceEventEntity.TenantId),
            nameof(DeviceEventEntity.EventType),
            nameof(DeviceEventEntity.Timestamp));
        index.IsDescending.Should().Equal(false, false, true);
    }
}
