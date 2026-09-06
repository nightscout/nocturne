namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// The members a V4 bulk create-or-update request carries in common with every other one: the
/// event time, and the (<see cref="DataSource"/>, <see cref="SyncIdentifier"/>) pair the upsert
/// keys on.
/// </summary>
/// <seealso cref="Controllers.V4.Base.V4BulkValidation.ValidateBulkAsync{TRequest}"/>
public interface IBulkUpsertRequest
{
    /// <summary>When the event this record describes happened.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>Upstream data source identifier; half of the upsert key.</summary>
    string? DataSource { get; }

    /// <summary>Upstream record identifier within <see cref="DataSource"/>; the other half.</summary>
    string? SyncIdentifier { get; }
}
