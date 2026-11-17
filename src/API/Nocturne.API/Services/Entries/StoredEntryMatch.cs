using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// A stored reading an upload duplicates, as <see cref="IEntryService.UpsertUploadAsync"/> found it.
/// </summary>
/// <param name="Entry">The stored reading in the legacy shape.</param>
/// <param name="LegacyId">The stored row's legacy id, or <c>null</c> for a row that has none and is
/// served under its own uuid.</param>
internal sealed record StoredEntryMatch(Entry Entry, string? LegacyId);
