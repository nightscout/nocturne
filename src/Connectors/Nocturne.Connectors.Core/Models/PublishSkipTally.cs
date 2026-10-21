namespace Nocturne.Connectors.Core.Models;

/// <summary>
/// The records the publishers in one scope left unwritten: because the user had deleted them, or
/// because a value in them could never be stored. Scoped with the publishers, so every publisher a
/// sync calls adds to the one count <see cref="Interfaces.IConnectorPublisher.SkippedDeleted"/> and
/// <see cref="Interfaces.IConnectorPublisher.Refused"/> report.
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

    private int _refused;

    public int Refused => Volatile.Read(ref _refused);

    public void AddRefused(int count)
    {
        if (count > 0)
            Interlocked.Add(ref _refused, count);
    }
}
