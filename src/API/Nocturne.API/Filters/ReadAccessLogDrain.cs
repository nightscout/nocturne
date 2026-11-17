namespace Nocturne.API.Filters;

/// <summary>Runs <see cref="ReadAccessLogWriter.DrainAsync"/> for the life of the host.</summary>
/// <remarks>
/// A plain <see cref="IHostedService"/>: the drain takes no stopping token, so only completing the
/// queue ends it and it writes what is queued after the stop signal. The host's shutdown timeout
/// bounds the wait; entries still queued when it fires are counted and logged.
/// </remarks>
public sealed class ReadAccessLogDrain(ReadAccessLogWriter writer) : IHostedService
{
    private Task? drain;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        drain = Task.Run(writer.DrainAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        writer.Complete();
        if (drain is null)
            return;

        try
        {
            await drain.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            writer.ReportDiscarded();
        }
    }
}
