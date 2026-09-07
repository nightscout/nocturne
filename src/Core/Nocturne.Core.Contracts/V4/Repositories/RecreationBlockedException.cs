namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// A single-record create was refused because the external identity the record carries is already
/// held by a stored row — a live row, or one the user deleted.
/// </summary>
/// <remarks>
/// The bulk paths express the same rule by dropping the record from their insert set, which a
/// caller holding one record cannot read as anything but success.
/// </remarks>
public sealed class RecreationBlockedException(string recordType, string heldIdentity)
    : Exception($"A stored {recordType} already holds {heldIdentity}.")
{
    /// <summary>The sync key's phrasing, shared by every path that refuses one.</summary>
    public static RecreationBlockedException ForSyncKey(
        string recordType, string dataSource, string syncIdentifier)
        => new(recordType, $"sync identifier '{syncIdentifier}' from '{dataSource}'");
}
