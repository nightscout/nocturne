using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the <c>automatic</c> flag a legacy upload sent on a temp basal into the V4 record, so the
/// projected treatment returns it as Nightscout would, and returns none when the uploader sent none.
/// </summary>
/// <remarks>
/// <see cref="V4.TempBasal.Origin"/> cannot stand in for it: a temp basal without the flag falls back
/// to Manual, and Trio, iAPS and AAPS never send the flag, so deriving it from the origin would state
/// <c>automatic: false</c> for temp basals their algorithms set.
/// </remarks>
public static class TempBasalAutomaticFlag
{
    public const string Field = "automatic";

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, bool? automatic)
    {
        if (automatic is { } flag)
            (record ??= new())[Field] = flag;

        return record;
    }

    public static bool? Of(IReadOnlyDictionary<string, object?>? record) =>
        record?.GetValueOrDefault(Field) switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };
}
