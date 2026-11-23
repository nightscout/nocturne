using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.NocturneRemote;

/// <summary>
///     Prepares V4 records for import: the source Id becomes the LegacyId and the Id is cleared, so
///     the publish stamps a local one.
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
        }

        return list;
    }
}
