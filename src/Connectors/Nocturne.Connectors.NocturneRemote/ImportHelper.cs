using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.NocturneRemote;

/// <summary>
///     Stamps V4 records for import: preserves the source Id as LegacyId,
///     clears Id for fresh UUID v7 generation, and sets DataSource.
/// </summary>
public static class ImportHelper
{
    /// <summary>
    ///     Prepares a collection of V4 records for import into the local instance.
    /// </summary>
    public static List<T> PrepareForImport<T>(IEnumerable<T> records) where T : IV4Record
    {
        var list = records.ToList();
        foreach (var record in list)
        {
            record.LegacyId = record.Id.ToString();
            record.Id = Guid.Empty;
            record.DataSource = DataSources.NocturneRemoteConnector;
        }

        return list;
    }
}
