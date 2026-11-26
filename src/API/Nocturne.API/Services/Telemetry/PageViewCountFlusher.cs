using Nocturne.Core.Contracts.Telemetry;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Telemetry;

/// <summary>
/// Adds the counts <see cref="PageViewCounter"/> holds to <see cref="IPageViewCountStore"/> every
/// <see cref="FlushInterval"/> and once more on shutdown, and once a day deletes the days older
/// than <see cref="RetentionDays"/>.
/// </summary>
/// <remarks>
/// The deletion runs while page counts are off too, so turning them off still lets the stored
/// counts age out.
/// </remarks>
internal sealed class PageViewCountFlusher(
    PageViewCounter counter,
    IPageViewCountStore store,
    TimeProvider time,
    ILogger<PageViewCountFlusher> logger) : BackgroundService
{
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);

    /// <summary>How many UTC days of counts are kept, today included.</summary>
    internal const int RetentionDays = 400;

    /// <summary>
    /// Delay before the first pass, so it does not open a connection while the host is still
    /// starting: the API also boots for OpenAPI generation, where it may have no database to reach.
    /// </summary>
    internal TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    private DateOnly? _prunedOn;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(FlushInterval, time);
            do
            {
                await RunOnceAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    /// <summary>One pass: the flush, then the day's deletion if it has not run today.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await FlushAsync(ct);

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (_prunedOn == today)
            return;

        try
        {
            await store.DeleteBeforeAsync(today.AddDays(-(RetentionDays - 1)), ct);
            _prunedOn = today;
        }
        catch (Exception ex) when (StoreFailure.Is(ex))
        {
            logger.LogWarning(ex, "Could not delete page view counts past their retention");
        }
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        var counts = counter.Drain();
        if (counts.Count == 0)
            return;

        try
        {
            await store.AddAsync(counts, ct);
        }
        catch (Exception ex) when (StoreFailure.Is(ex))
        {
            counter.Restore(counts);
            logger.LogWarning(ex, "Could not store {Count} page view counts; keeping them for the next flush", counts.Count);
        }
    }
}
