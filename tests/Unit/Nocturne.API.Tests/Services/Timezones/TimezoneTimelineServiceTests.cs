using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Timezones;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Timezones;

[Trait("Category", "Unit")]
public class TimezoneTimelineServiceTests : IDisposable
{
    private readonly NocturneDbContext _db;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 4, 12, 3, 30, 0, TimeSpan.Zero));
    private readonly TimezoneTimelineService _service;

    public TimezoneTimelineServiceTests()
    {
        var tenantId = Guid.CreateVersion7();
        _db = TestDbContextFactory.CreateInMemoryContext();
        _db.TenantId = tenantId;

        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.Context)
            .Returns(new TenantContext(tenantId, "test", "Test", IsActive: true, IsDemo: false));

        _service = new TimezoneTimelineService(
            _db.AsFactory(), accessor.Object, _clock, NullLogger<TimezoneTimelineService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task EnsureOrigin_StampsTheOriginWithTheClock()
    {
        (await _service.EnsureOriginAsync("America/New_York")).Should().BeTrue();

        var origin = await _db.TimezoneTimeline.SingleAsync();
        origin.CreatedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        origin.UpdatedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task UpsertingAnExistingEntry_MovesItsCurrentZoneAndStampsUpdatedAtOnly()
    {
        await _service.EnsureOriginAsync("America/New_York");
        var created = _clock.GetUtcNow().UtcDateTime;
        var origin = (await _service.GetTimelineAsync()).Single();
        _clock.Advance(TimeSpan.FromHours(5));

        var updated = await _service.UpsertAsync(new TimezoneTimelineEntry
        {
            Id = origin.Id,
            EffectiveFrom = origin.EffectiveFrom,
            Timezone = "Australia/Sydney",
        });

        updated.Timezone.Should().Be("Australia/Sydney");
        updated.UpdatedAt.Should().Be(created.AddHours(5));
        updated.CreatedAt.Should().Be(created);
    }
}
