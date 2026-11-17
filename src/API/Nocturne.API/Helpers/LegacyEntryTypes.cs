namespace Nocturne.API.Helpers;

/// <summary>
/// The v1 entry types the store holds.
/// </summary>
public static class LegacyEntryTypes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "sgv", "mbg", "cal",
    };
}
