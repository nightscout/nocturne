namespace Nocturne.Core.Contracts.Jobs;

/// <summary>
/// Where a scheduled job resumes: a keyset position (<paramref name="At"/>, <paramref name="Id"/>)
/// or a page number, whichever the job walks by.
/// </summary>
/// <remarks>
/// <c>job_ledger</c> holds no health data, and this is the only thing a job writes there, so it is
/// a position and nothing else: <c>JobCursorShapeGuardTests</c> pins these three members.
/// </remarks>
public sealed record JobCursor(DateTimeOffset? At = null, Guid? Id = null, int? Page = null);
