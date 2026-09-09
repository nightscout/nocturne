namespace Nocturne.Core.Contracts.Entries;

/// <summary>
/// One entry's identity for the upload duplicate check: the fields
/// <see cref="IEntryStore.CheckDuplicatesAsync"/> matches against stored readings.
/// </summary>
/// <param name="Device">Device identifier, or <c>null</c> to match any device.</param>
/// <param name="Type">Entry type ("sgv", "mbg", "cal"); other types are never duplicates.</param>
/// <param name="Sgv">Glucose value in mg/dL, or <c>null</c> to match any value.</param>
/// <param name="Mills">Entry timestamp in Unix milliseconds.</param>
public readonly record struct EntryDuplicateProbe(string? Device, string Type, double? Sgv, long Mills);
