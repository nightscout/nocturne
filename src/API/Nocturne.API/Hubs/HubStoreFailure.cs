using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Nocturne.API.Hubs;

/// <summary>
/// Tells a failure of the database a hub reads or writes, which a hub method answers with its failed
/// response, from a defect, which reaches the hub pipeline.
/// </summary>
/// <remarks>
/// Npgsql raises <see cref="DbException"/>. EF raises a transient one that outlasts its retries as
/// <see cref="RetryLimitExceededException"/> and a failed save as <see cref="DbUpdateException"/>,
/// and neither derives from <see cref="DbException"/>.
/// </remarks>
internal static class HubStoreFailure
{
    /// <summary>Whether <paramref name="ex"/> is a failure of the database.</summary>
    internal static bool Is(Exception ex) =>
        ex is DbException or DbUpdateException or RetryLimitExceededException;
}
