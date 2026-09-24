using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// The wire names, in ordinal order, of every enum the Rust engine mirrors as an
/// ordinal-indexed table. The Rust suite asserts each of its tables equals the committed
/// manifest. A reordered, inserted or removed member then fails that test instead of
/// silently shifting integer-form payloads onto the wrong member.
/// </summary>
public static class EnumManifest
{
    public const string FileName = "AlertEngineEnums.json";

    /// <summary>Sibling of the corpus directory, so no corpus consumer mistakes it for a scenario.</summary>
    public static string PathFor(string corpusDir) =>
        Path.Combine(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpusDir)))!,
            FileName);

    public static string Render()
    {
        var root = new JsonObject
        {
            ["AlertComparisonOperator"] = WireNames<AlertComparisonOperator>(),
            ["AlertConditionType"] = WireNames<AlertConditionType>(),
            ["AlertConditionTypeMembers"] = MemberNames<AlertConditionType>(),
            ["DayOfWeek"] = WireNames<DayOfWeek>(),
            ["GlucoseBucket"] = WireNames<GlucoseBucket>(),
            ["PumpModeState"] = WireNames<PumpModeState>(),
            ["StateSpanCategory"] = WireNames<StateSpanCategory>(),
            ["TempBasalMetric"] = WireNames<TempBasalMetric>(),
            ["TrendBucket"] = WireNames<TrendBucket>(),
        };
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
        return json.ReplaceLineEndings("\n");
    }

    private static JsonArray WireNames<TEnum>() where TEnum : struct, Enum
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<TEnum>());
        return new JsonArray(ContiguousValues<TEnum>()
            .Select(v => (JsonNode)JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(v, options))!)
            .ToArray());
    }

    private static JsonArray MemberNames<TEnum>() where TEnum : struct, Enum =>
        new(ContiguousValues<TEnum>().Select(v => (JsonNode)Enum.GetName(v)!).ToArray());

    /// <summary>
    /// The Rust tables are indexed by ordinal, which is only sound while the C# values are
    /// exactly <c>0..n-1</c>.
    /// </summary>
    private static TEnum[] ContiguousValues<TEnum>() where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>().OrderBy(v => Convert.ToInt64(v)).ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            if (Convert.ToInt64(values[i]) != i)
            {
                throw new InvalidOperationException(
                    $"{typeof(TEnum).Name} values are not contiguous from 0 ({values[i]} = {Convert.ToInt64(values[i])})");
            }
        }
        return values;
    }
}
