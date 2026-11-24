using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Tells a failure of the database, which a caller answers with its own failed response, from a
/// defect, which reaches the caller's pipeline.
/// </summary>
/// <remarks>
/// Npgsql raises <see cref="DbException"/>. EF raises a transient one that outlasts the retries
/// the context registers as <see cref="RetryLimitExceededException"/> and a failed save as
/// <see cref="DbUpdateException"/>, and neither derives from <see cref="DbException"/>.
/// </remarks>
public static class StoreFailure
{
    /// <summary>Whether <paramref name="ex"/> is a failure of the database.</summary>
    public static bool Is(Exception ex) =>
        ex is DbException or DbUpdateException or RetryLimitExceededException;
}
