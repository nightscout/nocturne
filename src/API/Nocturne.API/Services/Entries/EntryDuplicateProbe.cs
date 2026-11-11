using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// One entry's identity for the upload duplicate check: the fields
/// <see cref="IEntryService.UpsertUploadAsync"/> matches against stored readings.
/// </summary>
/// <param name="Device">Device identifier, or <c>null</c> to match any device.</param>
/// <param name="Type">Entry type ("sgv", "mbg", "cal"); other types are never duplicates.</param>
/// <param name="Mills">Entry timestamp in Unix milliseconds.</param>
internal readonly record struct EntryDuplicateProbe(string? Device, string Type, long Mills);
