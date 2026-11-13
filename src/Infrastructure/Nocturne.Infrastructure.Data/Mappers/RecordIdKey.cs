namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// The primary key a record's string id maps to, so a writer outside the mappers can find the row a
/// mapper stored under that id.
/// </summary>
public static class RecordIdKey
{
    /// <summary>
    /// The id itself when it is a GUID, a SHA1-derived GUID for any other string, or a new GUID for
    /// null or empty input.
    /// </summary>
    public static Guid ParseIdToGuid(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.CreateVersion7();

        if (Guid.TryParse(id, out var guid))
            return guid;

        var hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(id)
        );
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }
}
