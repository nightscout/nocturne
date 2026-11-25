namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// The correlation ids the writes under way kept on stored rows the history clamp hides from the writer,
/// each keyed by the id the writer sent for that row. Internal to the write: no answer or broadcast
/// carries it, and only a decomposer reads it, to write the parts of a group that have no legacy id of
/// their own under the id the group's stored rows hold.
/// </summary>
/// <remarks>
/// A decomposer takes a group's correlation id from the stored rows of its legacy id, through a read the
/// clamp also limits, so for a hidden group it mints a fresh one. The hidden rows keep their stored id
/// (<see cref="V4RepositoryBase{TModel,TEntity}.KeepCorrelationId"/>), and a status's extras row and
/// sensor state report are held off a re-send only by that id. Ambient for the reason
/// <see cref="Services.UpstreamFingerprintScope"/> is.
/// </remarks>
public sealed class KeptCorrelationIds : IDisposable
{
    private static readonly AsyncLocal<KeptCorrelationIds?> Current = new();

    private readonly KeptCorrelationIds? _prior;
    private readonly Dictionary<Guid, Guid> _kept = [];

    private KeptCorrelationIds(KeptCorrelationIds? prior) => _prior = prior;

    /// <summary>Collects the ids kept by the writes made until the scope is disposed.</summary>
    /// <remarks>
    /// Open it only where the writer is not answered with the rows created under a resolved id: a created
    /// row is answered from the stored row, so its kept id would reach a writer the clamp hides it from.
    /// </remarks>
    public static KeptCorrelationIds Open()
    {
        var scope = new KeptCorrelationIds(Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>The id a hidden stored row kept in place of <paramref name="sent"/>, else <paramref name="sent"/>.</summary>
    public Guid Resolve(Guid sent) => _kept.GetValueOrDefault(sent, sent);

    /// <remarks>
    /// The first id recorded for <paramref name="sent"/> stays: a decomposer writes a group's APS row first
    /// and anchors the group on it, so a group whose stored rows hold different ids resolves to the APS one.
    /// </remarks>
    internal static void Record(Guid sent, Guid kept)
    {
        Current.Value?._kept.TryAdd(sent, kept);
    }

    public void Dispose() => Current.Value = _prior;
}
