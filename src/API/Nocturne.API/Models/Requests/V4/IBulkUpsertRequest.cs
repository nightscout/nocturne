namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// The members a V4 bulk create-or-update request carries in common with every other one: the
/// <see cref="IV4RecordHeader"/>, and the (<see cref="IV4RecordHeader.DataSource"/>, <see cref="SyncIdentifier"/>)
/// pair the upsert keys on.
/// </summary>
/// <seealso cref="Controllers.V4.Base.V4BulkValidation.ValidateBulkAsync{TRequest}"/>
public interface IBulkUpsertRequest : IV4RecordHeader
{
    /// <summary>Upstream record identifier within <see cref="IV4RecordHeader.DataSource"/>; the other half of the upsert key.</summary>
    string? SyncIdentifier { get; }
}
