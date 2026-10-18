using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Runs <see cref="ISensorSessionInferenceService"/> over the
/// <see cref="SensorSessionInferenceService.LiveWindow"/> of every active tenant.
/// </summary>
/// <remarks>
/// Single-instance deployment only, as <see cref="DeduplicationReconciliationBackgroundService"/> is.
/// </remarks>
internal sealed class SensorSessionInferenceBackgroundService(
    ISensorSessionInferenceRunner runner,
    ActiveTenantSnapshot activeTenants,
    TimeProvider timeProvider,
    ILogger<SensorSessionInferenceBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);

            using var timer = new PeriodicTimer(Interval, timeProvider);
            do
            {
                try
                {
                    await InferAllTenantsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error during sensor session inference cycle");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task InferAllTenantsAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var tenant in await activeTenants.GetAsync(ct))
        {
            try
            {
                await runner.RunAsync(
                    new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
                    (inference, token) => inference.InferAsync(now - SensorSessionInferenceService.LiveWindow, now, token),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error inferring sensor sessions for tenant {TenantSlug}", tenant.Slug);
            }
        }
    }
}
