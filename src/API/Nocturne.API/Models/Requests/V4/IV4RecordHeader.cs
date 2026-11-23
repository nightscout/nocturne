namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// The members every V4 record request carries for the header of the record it writes: when it
/// happened, the clock offset, and who reported it. <see cref="Controllers.V4.Base.V4RecordHeaderMapping.WithHeaderFrom{TModel}"/>
/// is the one place they are copied onto a model.
/// </summary>
/// <remarks>
/// <c>SyncIdentifier</c> and <c>CorrelationId</c> are not here: whether an update takes them from the
/// request or keeps the stored value differs per record type, so each type maps them itself.
/// </remarks>
public interface IV4RecordHeader
{
    /// <summary>When the event this record describes happened.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>UTC offset in minutes at the time of the event.</summary>
    int? UtcOffset { get; }

    /// <summary>Identifier of the device that produced the record.</summary>
    string? Device { get; }

    /// <summary>Name of the application that submitted the record.</summary>
    string? App { get; }

    /// <summary>Upstream data source identifier.</summary>
    string? DataSource { get; }
}
