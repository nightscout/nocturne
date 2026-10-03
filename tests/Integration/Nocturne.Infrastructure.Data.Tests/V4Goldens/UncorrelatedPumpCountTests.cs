using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// <see cref="IPumpSnapshotRepository.CountUncorrelatedAsync"/> against real Postgres: only an APS
/// snapshot the list would also read (same window, same device) makes a pump correlated.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class UncorrelatedPumpCountTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly V4GoldenFixture _fx;

    public UncorrelatedPumpCountTests(V4GoldenFixture fx) => _fx = fx;

    [Fact]
    public async Task ApsOutsideTheWindow_DoesNotHideAPumpInsideIt()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var (aps, pumps) = Repos(scope);
        var correlation = Guid.NewGuid();
        await aps.CreateAsync(Aps(T0.AddHours(-2), correlation), WriteOrigin.Backfill);
        await pumps.CreateAsync(Pump(T0, correlation), WriteOrigin.Backfill);

        (await pumps.CountUncorrelatedAsync(T0.AddHours(-1), null, null)).Should().Be(1);
        (await pumps.CountUncorrelatedAsync(null, null, null)).Should().Be(0);
    }

    [Fact]
    public async Task PumpOutsideTheWindow_IsNotCountedWhenItsApsIsInside()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var (aps, pumps) = Repos(scope);
        var correlation = Guid.NewGuid();
        await aps.CreateAsync(Aps(T0, correlation), WriteOrigin.Backfill);
        await pumps.CreateAsync(Pump(T0.AddHours(-2), correlation), WriteOrigin.Backfill);

        (await pumps.CountUncorrelatedAsync(T0.AddHours(-1), null, null)).Should().Be(0);
        (await pumps.CountUncorrelatedAsync(null, T0.AddHours(-1), null)).Should().Be(1);
        (await aps.CountAsync(T0.AddHours(-1), null, null)).Should().Be(1);
        (await aps.CountAsync(null, T0.AddHours(-1), null)).Should().Be(0);
    }

    [Fact]
    public async Task ApsOnAnotherDevice_DoesNotHideThePump()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var (aps, pumps) = Repos(scope);
        var correlation = Guid.NewGuid();
        await aps.CreateAsync(Aps(T0, correlation, "synthetic://a"), WriteOrigin.Backfill);
        await pumps.CreateAsync(Pump(T0, correlation, "synthetic://b"), WriteOrigin.Backfill);
        await pumps.CreateAsync(Pump(T0.AddMinutes(5), correlation: null, "synthetic://b"), WriteOrigin.Backfill);

        (await pumps.CountUncorrelatedAsync(null, null, "synthetic://b")).Should().Be(2);
        (await pumps.CountUncorrelatedAsync(null, null, null)).Should().Be(1);
        (await aps.CountAsync(null, null, "synthetic://b")).Should().Be(0);
    }

    private static (IApsSnapshotRepository Aps, IPumpSnapshotRepository Pumps) Repos(IServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>(),
            scope.ServiceProvider.GetRequiredService<IPumpSnapshotRepository>());

    private static ApsSnapshot Aps(DateTime at, Guid correlation, string? device = null) =>
        new() { Timestamp = at, CorrelationId = correlation, Device = device, DataSource = "loop" };

    private static PumpSnapshot Pump(DateTime at, Guid? correlation, string? device = null) =>
        new() { Timestamp = at, CorrelationId = correlation, Device = device, DataSource = "loop" };
}
