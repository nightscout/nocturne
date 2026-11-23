namespace Nocturne.Core.Constants;

/// <summary>
/// Lengths of the string columns of a record table. The entity attribute and the request validator
/// of a column that is not cut to length on save both read the same constant.
/// </summary>
public static class RecordColumnLengths
{
    /// <summary>The <c>device</c>, <c>app</c> and <c>data_source</c> columns.</summary>
    public const int Provenance = 256;

    /// <summary>The <c>sync_identifier</c> column.</summary>
    public const int SyncIdentifier = 256;

    /// <summary>The <c>pump_record_id</c> column.</summary>
    public const int PumpRecordId = 256;

    /// <summary>The <c>base_profile_name</c> column of a profile activation.</summary>
    public const int ProfileName = 256;
}
