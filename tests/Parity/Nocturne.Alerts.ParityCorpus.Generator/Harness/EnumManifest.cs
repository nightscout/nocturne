using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// The wire names, in ordinal order, of every enum the Rust engine mirrors as an
/// ordinal-indexed table, plus the wall-clock condition kinds it mirrors as a set. The Rust suite asserts each of its tables equals the committed
/// manifest. A reordered, inserted or removed member then fails that test instead of
/// silently shifting integer-form payloads onto the wrong member.
/// <para>
/// <c>PayloadFields</c> is each condition kind's payload property names as the evaluators'
/// models read them. The Rust engine strips a property its payload does not declare from a
/// saved tree (docs/alerts/engine-semantics.md §1.4), so its tables must match these.
/// </para>
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
            ["WallClockConditionTypes"] = WallClockWireNames(),
            ["PayloadFields"] = PayloadFields(),
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

    /// <summary>The wire names of <see cref="WallClockConditions.Kinds"/>, in ordinal order.</summary>
    private static JsonArray WallClockWireNames()
    {
        var wire = WireNames<AlertConditionType>();
        return new JsonArray(ContiguousValues<AlertConditionType>()
            .Where(WallClockConditions.Kinds.Contains)
            .Select(v => (JsonNode)wire[(int)v]!.GetValue<string>())
            .ToArray());
    }

    /// <summary>
    /// Per payload property of <see cref="ConditionNode"/>, the JSON property names of its model
    /// under <see cref="EvaluatorJson.Options"/>, sorted.
    /// </summary>
    private static JsonObject PayloadFields()
    {
        var options = new JsonSerializerOptions(EvaluatorJson.Options);
        options.MakeReadOnly(populateMissingResolver: true);
        var fields = new JsonObject();
        foreach (var payload in options.GetTypeInfo(typeof(ConditionNode)).Properties
                     .Where(p => p.Name != "type")
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            fields[payload.Name] = new JsonArray(options.GetTypeInfo(payload.PropertyType).Properties
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => (JsonNode)n)
                .ToArray());
        }
        return fields;
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
