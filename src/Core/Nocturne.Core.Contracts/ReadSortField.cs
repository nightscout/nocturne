namespace Nocturne.Core.Contracts;

/// <summary>The field a read orders by before its direction applies.</summary>
public enum ReadSortField
{
    /// <summary>The record's time.</summary>
    Time,

    /// <summary>The record's modification stamp, <c>srvModified</c>.</summary>
    SrvModified,

    /// <summary>The sensor glucose value, for entries; those without one order as having none.</summary>
    Sgv,
}
