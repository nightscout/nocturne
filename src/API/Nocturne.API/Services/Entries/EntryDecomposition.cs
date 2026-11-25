using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Entries;

/// <summary>The decomposition of a batch of entries, with the entries that deleted a stored reading.</summary>
internal sealed class EntryDecomposition : DecompositionResult
{
    private readonly HashSet<Entry> _invalidated = new(ReferenceEqualityComparer.Instance);

    /// <summary>The entries sent with <c>isValid: false</c> that deleted the live reading their id names.</summary>
    internal IReadOnlySet<Entry> Invalidated => _invalidated;

    internal void AddInvalidated(Entry entry) => _invalidated.Add(entry);
}
