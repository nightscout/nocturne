namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on an entry and reads back (<c>units</c>, <c>isReadOnly</c>), and
/// the <c>created_at</c> a v3 create stamps from <c>date</c>, into the V4 glucose record, so the
/// projected entry returns them as uploaded.
/// </summary>
public static class EntryUploadedFields
{
    private const string Units = "units";
    private const string CreatedAt = "created_at";

    public static Dictionary<string, object?>? Keep(Entry entry)
    {
        var record = KeptField.Add(null, Units, entry.Units);
        record = KeptField.Add(record, UploadedAppFields.IsReadOnlyKey, entry.IsReadOnly);
        return KeptField.Add(record, CreatedAt, entry.CreatedAtIsStored ? entry.CreatedAt : null);
    }

    public static void Restore(Entry entry, IReadOnlyDictionary<string, object?>? record)
    {
        entry.Units = KeptField.String(record, Units);
        entry.IsReadOnly = KeptField.Bool(record, UploadedAppFields.IsReadOnlyKey);
        entry.CreatedAt = KeptField.String(record, CreatedAt);
        entry.CreatedAtIsStored = entry.CreatedAt is not null;
    }
}
