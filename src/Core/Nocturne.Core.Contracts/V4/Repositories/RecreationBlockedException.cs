namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// A single-record create or restore was refused because the external identity the record carries
/// is already held by a stored row: for a create, a live row or one the user deleted; for a
/// restore, a live row, which the restore must never replace.
/// </summary>
/// <remarks>
/// The bulk paths express the same rule by dropping the record from their write set, which a
/// caller holding one record cannot read as anything but success.
/// </remarks>
public sealed class RecreationBlockedException : Exception
{
    public RecreationBlockedException(string recordType, string heldIdentity)
        : base($"A stored {recordType} already holds {heldIdentity}.")
    {
    }

    private RecreationBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>The sync key's phrasing, shared by every path that refuses one.</summary>
    /// <param name="recordType">The refused record's type name.</param>
    /// <param name="dataSource">The sync key's data source.</param>
    /// <param name="syncIdentifier">The sync key's identifier.</param>
    /// <param name="heldBy">The stored row holding the key, when the refusing path read it (<see cref="HeldBy"/>).</param>
    public static RecreationBlockedException ForSyncKey(
        string recordType, string dataSource, string syncIdentifier, Guid? heldBy = null)
        => new(recordType, SyncKeyIdentity(dataSource, syncIdentifier)) { HeldBy = heldBy };

    public static RecreationBlockedException ForRestore(string recordType, string heldIdentity)
        => new($"A newer version of this {recordType} exists: a live {recordType} already holds {heldIdentity}, so this one cannot be restored.");

    public static string SyncKeyIdentity(string dataSource, string syncIdentifier)
        => $"sync identifier '{syncIdentifier}' from '{dataSource}'";

    public static string LegacyIdIdentity(string legacyId) => $"legacy id '{legacyId}'";

    /// <summary>The id of the stored row that holds the identity, when the refusing path read it.</summary>
    public Guid? HeldBy { get; init; }
}
