namespace Nocturne.Core.Models.V4;

/// <summary>
/// The decomposition of a batch of legacy <typeparamref name="TRecord"/> records, with the records that
/// deleted a stored one.
/// </summary>
public sealed class InvalidatingDecomposition<TRecord> : DecompositionResult
    where TRecord : class
{
    private readonly HashSet<TRecord> _invalidated = new(ReferenceEqualityComparer.Instance);

    /// <summary>The records sent with <c>isValid: false</c> that deleted the live record their id names.</summary>
    public IReadOnlySet<TRecord> Invalidated => _invalidated;

    public void AddInvalidated(TRecord record) => _invalidated.Add(record);
}
