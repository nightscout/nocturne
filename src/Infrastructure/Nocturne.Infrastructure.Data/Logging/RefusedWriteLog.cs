using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Nocturne.Infrastructure.Data.Logging;

/// <summary>
/// The reports of submitted values the store would not take as sent: records refused outright, and
/// free-text values cut to fit their column. Each has its own event id and counter, so a run of them
/// caused by a Nocturne bug rather than by clients shows up as such.
/// </summary>
/// <remarks>
/// No tenant or record identity is a tag or a logged value: the record type, the reason and the
/// column are.
/// </remarks>
public static partial class RefusedWriteLog
{
    public const string MeterName = "Nocturne.Writes";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Refused = Meter.CreateCounter<long>(
        "writes.refused", description: "Submitted records not stored because a value could never be stored as sent.");

    private static readonly Counter<long> Truncated = Meter.CreateCounter<long>(
        "writes.truncated", description: "Submitted free-text values cut to fit their column.");

    /// <summary>Logs and counts one refused record.</summary>
    public static void LogRefused(this ILogger logger, string recordType, string reason, string origin, Exception cause)
    {
        Refused.Add(1, new("record_type", recordType), new("origin", origin));
        RecordRefused(logger, cause, recordType, origin, reason);
    }

    /// <summary>Logs and counts one value cut to fit its column.</summary>
    public static void LogTruncated(this ILogger logger, string table, string column, int length)
    {
        Truncated.Add(1, new("table", table), new("column", column));
        ValueTruncated(logger, table, column, length);
    }

    [LoggerMessage(
        EventId = 4190,
        EventName = "RecordRefused",
        Level = LogLevel.Warning,
        Message = "Refused a {RecordType} from {Origin}: {Reason}")]
    private static partial void RecordRefused(ILogger logger, Exception cause, string recordType, string origin, string reason);

    [LoggerMessage(
        EventId = 4191,
        EventName = "ValueTruncated",
        Level = LogLevel.Warning,
        Message = "Cut a value of {Table}.{Column} to its {Length} characters")]
    private static partial void ValueTruncated(ILogger logger, string table, string column, int length);
}
