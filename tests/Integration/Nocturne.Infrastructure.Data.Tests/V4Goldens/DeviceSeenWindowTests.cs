using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// <see cref="IDeviceRepository.WidenSeenWindowAsync"/> against real Postgres: the monotonic
/// comparison lives in the UPDATE, so only a real database can show a stale caller losing to the
/// stored row.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class DeviceSeenWindowTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly V4GoldenFixture _fx;

    public DeviceSeenWindowTests(V4GoldenFixture fx) => _fx = fx;

    [Fact]
    public async Task EarlierThanStoredLastSeen_LeavesLastSeenUnchanged()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var id = await CreateDeviceAsync(repo, firstSeen: T0, lastSeen: T0.AddMinutes(5));

        await repo.WidenSeenWindowAsync(id, T0.AddMinutes(1), WriteOrigin.Backfill);

        var (first, last) = await ReadWindowAsync(tenant, id);
        first.Should().Be(T0);
        last.Should().Be(T0.AddMinutes(5), "a stale scope's earlier time must not move last seen back");
    }

    [Fact]
    public async Task NewestFirstBackfill_MovesFirstSeenToTheOldestTime()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var newest = T0.AddHours(3);
        var id = await CreateDeviceAsync(repo, firstSeen: newest, lastSeen: newest);

        await repo.WidenSeenWindowAsync(id, T0.AddHours(2), WriteOrigin.Backfill);
        await repo.WidenSeenWindowAsync(id, T0, WriteOrigin.Backfill);
        await repo.WidenSeenWindowAsync(id, T0.AddHours(1), WriteOrigin.Backfill);

        var (first, last) = await ReadWindowAsync(tenant, id);
        first.Should().Be(T0, "first seen is the oldest status the backfill met, whatever the order");
        last.Should().Be(newest);
    }

    [Fact]
    public async Task LaterThanStoredLastSeen_AdvancesLastSeenOnly()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var id = await CreateDeviceAsync(repo, firstSeen: T0, lastSeen: T0.AddMinutes(5));

        await repo.WidenSeenWindowAsync(id, T0.AddMinutes(10), WriteOrigin.Live);

        var (first, last) = await ReadWindowAsync(tenant, id);
        first.Should().Be(T0);
        last.Should().Be(T0.AddMinutes(10));
    }

    private static async Task<Guid> CreateDeviceAsync(IDeviceRepository repo, DateTime firstSeen, DateTime lastSeen)
    {
        var created = await repo.CreateAsync(new Device
        {
            Id = Guid.CreateVersion7(),
            Category = DeviceCategory.InsulinPump,
            Type = "Test Pump",
            Serial = $"SN-{Guid.NewGuid():N}",
            FirstSeenTimestamp = firstSeen,
            LastSeenTimestamp = lastSeen,
        }, WriteOrigin.Live);
        return created.Id;
    }

    private Task<(DateTime First, DateTime Last)> ReadWindowAsync(Guid tenant, Guid id) =>
        _fx.QueryAsync(tenant, async ctx =>
        {
            var row = await ctx.Devices.AsNoTracking().SingleAsync(d => d.Id == id);
            return (row.FirstSeenTimestamp, row.LastSeenTimestamp);
        });
}
