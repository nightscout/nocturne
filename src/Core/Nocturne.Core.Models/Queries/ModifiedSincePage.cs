namespace Nocturne.Core.Models.Queries;

/// <summary>
/// One page of a v3 <c>history/{lastModified}</c> read.
/// </summary>
/// <param name="Records">The records delivered, oldest modification first.</param>
/// <param name="CursorMills">
/// The modification stamp, in Unix milliseconds, the next request resumes after; <c>null</c> when
/// nothing was modified after the request cursor. It can pass every delivered record's stamp when
/// the read skipped rows the caller never sees, so a page of skipped rows still advances the client.
/// </param>
public sealed record ModifiedSincePage<T>(IReadOnlyList<T> Records, long? CursorMills);
