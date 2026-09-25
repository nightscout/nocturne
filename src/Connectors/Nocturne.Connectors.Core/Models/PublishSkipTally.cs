namespace Nocturne.Connectors.Core.Models;

/// <summary>
/// The records the publishers in one scope left unwritten because the user had deleted them.
/// Scoped with the publishers, so every publisher a sync calls adds to the one count
/// <see cref="Interfaces.IConnectorPublisher.SkippedDeleted"/> reports.
/// </summary>
public sealed class PublishSkipTally
{
    private int _skippedDeleted;

    public int SkippedDeleted => Volatile.Read(ref _skippedDeleted);

    public void AddSkippedDeleted(int count)
    {
        if (count > 0)
            Interlocked.Add(ref _skippedDeleted, count);
    }
}
