namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marks the writes under way as live (<c>WriteOrigin.Live</c>), so every
/// <see cref="IWriteBackTracked"/> row they insert or modify is saved with
/// <see cref="IWriteBackTracked.WrittenLive"/> set. Ambient for the reason
/// <see cref="UpstreamFingerprintScope"/> is: the origin is a repository parameter, and the rows are
/// saved by the context those repositories create.
/// </summary>
public static class LiveWriteScope
{
    private static readonly AsyncLocal<bool> Current = new();

    internal static bool IsOpen => Current.Value;

    /// <summary>Opens the scope when <paramref name="live"/>, and closes it for the write otherwise.</summary>
    public static IDisposable Open(bool live)
    {
        var prior = Current.Value;
        Current.Value = live;
        return new Closer(prior);
    }

    private sealed class Closer(bool prior) : IDisposable
    {
        public void Dispose() => Current.Value = prior;
    }
}
