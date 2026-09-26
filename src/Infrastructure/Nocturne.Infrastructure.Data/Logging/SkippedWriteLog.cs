using Microsoft.Extensions.Logging;

namespace Nocturne.Infrastructure.Data.Logging;

/// <summary>
/// The one report of records a write left out because the user had deleted them. Counts and the
/// record type only: the identities are health records' keys.
/// </summary>
public static partial class SkippedWriteLog
{
    /// <summary>Logs once per write, and nothing when <paramref name="count"/> is zero.</summary>
    public static void LogSkippedDeleted(this ILogger logger, string recordType, int count)
    {
        if (count > 0)
            SkippedDeleted(logger, count, recordType);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipped {Count} {RecordType} records: the user deleted them, and a re-import does not bring deleted records back")]
    private static partial void SkippedDeleted(ILogger logger, int count, string recordType);
}
