namespace Nocturne.Core.Models.Queries;

/// <summary>
/// One row of a v3 <c>history/{lastModified}</c> read. A soft-deleted row is read with the live ones,
/// stamped with its delete, so the client is told of the delete the way Nightscout tells it: the
/// document again, with <c>isValid: false</c>.
/// </summary>
/// <param name="Record">The record as stored.</param>
/// <param name="Deleted">Whether the row is soft-deleted.</param>
public readonly record struct HistoryRecord<T>(T Record, bool Deleted);
