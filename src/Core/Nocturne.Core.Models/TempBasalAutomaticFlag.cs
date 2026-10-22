using System.Text.Json;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the <c>automatic</c> flag a legacy upload sent on a temp basal into the V4 record, so the
/// projected treatment returns it as Nightscout would, and returns none when the uploader sent none.
/// </summary>
/// <remarks>
/// <see cref="V4.TempBasal.Origin"/> cannot stand in for it: an origin the upload did not state is a
/// default or an inference (<see cref="V4.TempBasalOriginProvenance"/>), and deriving the flag from it
/// would serve Nocturne's guess as the uploader's statement. Where the origin is a reported fact
/// (a connector, or a v4 request that names it) <see cref="Of(TempBasalOrigin)"/> gives the flag to keep.
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

    public static bool? Of(TempBasalOrigin origin) =>
        origin switch
        {
            TempBasalOrigin.Algorithm => true,
            TempBasalOrigin.Manual => false,
            _ => null,
        };

    public static bool? Of(IReadOnlyDictionary<string, object?>? record) =>
        record?.GetValueOrDefault(Field) switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };
}
