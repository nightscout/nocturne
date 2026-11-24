using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <param name="Entry">The food entry to import.</param>
/// <param name="CarbIntakeLegacyId">
///     The legacy id the carb intake of <paramref name="Entry"/> is stored under.
/// </param>
public sealed record GlookoFoodEntryImport(ConnectorFoodEntryImport Entry, string CarbIntakeLegacyId);
