using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// A connector poller that also holds a real-time listener per tenant (webhook, SSE, WebSocket),
/// which asks for an immediate sync when the upstream data changes.
/// </summary>
public interface IConnectorRealtimeListeners
{
    /// <summary>
    /// Starts a listener for each active tenant that has none or whose listener has died. Must leave a
    /// tenant with a live listener untouched; <see cref="ConnectorBackgroundService{TConfig}.ListenerNeedsStartAsync{TClient}"/>
    /// enforces that.
    /// </summary>
    Task StartRealtimeListenersAsync(CancellationToken cancellationToken);

    /// <summary>Tears down every listener <see cref="StartRealtimeListenersAsync"/> started.</summary>
    Task StopRealtimeListenersAsync();
}

/// <summary>
/// Keeps each <see cref="IConnectorRealtimeListeners"/> poller's listeners up: it starts them when
/// the host starts, replaces the dead ones every <see cref="SupervisionInterval"/>, and stops them at
/// shutdown.
/// </summary>
/// <remarks>
/// A listener is a connection this process holds, so supervising it is in-process work, not a job
/// ledger row. The interval is coarse so a permanently unreachable upstream is not reconnected often.
/// </remarks>
public sealed class ConnectorRealtimeSupervisor(
    IEnumerable<IScheduledJob> jobs,
    TimeProvider timeProvider,
    ILogger<ConnectorRealtimeSupervisor> logger) : BackgroundService
{
    internal static readonly TimeSpan SupervisionInterval = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<IConnectorRealtimeListeners> _pollers =
        [.. jobs.OfType<IConnectorRealtimeListeners>()];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_pollers.Count == 0)
            return;

        // Detach from the sequential hosted-service startup chain before the first connect.
        await Task.Yield();

        try
        {
            using var timer = new PeriodicTimer(SupervisionInterval, timeProvider);
            do
            {
                await SuperviseAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var poller in _pollers)
            {
                try
                {
                    await poller.StopRealtimeListenersAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to stop the real-time listeners of {Poller}", poller.GetType().Name);
                }
            }
        }
    }

    internal async Task SuperviseAsync(CancellationToken stoppingToken)
    {
        foreach (var poller in _pollers)
        {
            try
            {
                await poller.StartRealtimeListenersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Failed to start the real-time listeners of {Poller}, falling back to polling",
                    poller.GetType().Name);
            }
        }
    }
}
