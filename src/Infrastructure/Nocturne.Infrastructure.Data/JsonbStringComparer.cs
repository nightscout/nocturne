using System.Text.Json;

using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Semantic equality for string properties stored in PostgreSQL <c>jsonb</c> columns. Postgres
/// normalizes jsonb on write (key order, whitespace, duplicate keys), so a value read back never
/// matches the app's compact serialization byte-for-byte. Without this comparer, every upsert that
/// re-serializes an unchanged model marks the property modified — issuing a no-op UPDATE, a false
/// audit diff, and a false "updated" broadcast on each connector re-sync.
/// </summary>
public sealed class JsonbStringComparer : ValueComparer<string?>
{
    public static readonly JsonbStringComparer Instance = new();

    private JsonbStringComparer()
        : base(
            (a, b) => JsonEquals(a, b),
            // Ordinal string hash: NOT consistent with JsonEquals across serialization variants.
            // Acceptable because jsonb columns are never keys, and change detection uses only the
            // equality expression; a JSON-canonicalizing hash would parse on every snapshot.
            v => v == null ? 0 : StringComparer.Ordinal.GetHashCode(v),
            v => v)
    {
    }

    private static bool JsonEquals(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        try
        {
            // JsonElement (not JsonNode): JsonNode.DeepEquals materializes objects into a
            // dictionary and throws ArgumentException on duplicate keys, which are legal in
            // both JSON input and Postgres jsonb (last-wins). JsonElement.DeepEquals compares
            // duplicate-key objects without throwing (unequal, i.e. falls back to an UPDATE).
            using var docA = JsonDocument.Parse(a);
            using var docB = JsonDocument.Parse(b);
            return JsonElement.DeepEquals(docA.RootElement, docB.RootElement);
        }
        catch (JsonException)
        {
            // Not valid JSON — the ordinal comparison above already said the strings differ.
            return false;
        }
    }
}
