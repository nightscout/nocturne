using FluentAssertions;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// The wire names of every enum the Rust engine mirrors, in declaration order.
/// <see cref="EnumManifestTests"/> keeps the committed manifest equal to the C# enums, and the
/// Rust suite asserts each of its tables equals the manifest, so a member renamed, added or
/// removed on one side only fails a test instead of leaving a value one side cannot read.
/// <para>
/// <c>PayloadFields</c> is each condition kind's payload property names as the condition models
/// read them. A property the Rust engine's payload does not declare keeps a tree from being
/// evaluated (docs/alerts/engine-semantics.md §1.4), so its tables must match these.
/// </para>
/// </summary>
internal static class EnumManifest
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
            ["AlertWeekday"] = WireNames<AlertWeekday>(),
            ["GlucoseBucket"] = WireNames<GlucoseBucket>(),
            ["PumpModeState"] = WireNames<PumpModeState>(),
            ["StateSpanCategory"] = WireNames<StateSpanCategory>(),
            ["TempBasalMetric"] = WireNames<TempBasalMetric>(),
            ["TrendBucket"] = WireNames<TrendBucket>(),
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
        return new JsonArray(Enum.GetValues<TEnum>()
            .Select(v => (JsonNode)JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(v, options))!)
            .ToArray());
    }

    /// <summary>
    /// Per payload property of <see cref="ConditionNode"/>, the JSON property names of its model
    /// under <see cref="ConditionJson.Options"/>, sorted.
    /// </summary>
    private static JsonObject PayloadFields()
    {
        var options = new JsonSerializerOptions(ConditionJson.Options);
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
}

public class EnumManifestTests
{
    /// <summary>
    /// Under <c>NOCTURNE_CORPUS_BLESS=1</c> the manifest is rewritten from the enums instead, for
    /// review in the diff alongside the Rust tables it binds.
    /// </summary>
    [Fact]
    public void The_committed_manifest_matches_the_csharp_enums()
    {
        var path = EnumManifest.PathFor(CorpusLocator.CorpusDirectory());
        if (Environment.GetEnvironmentVariable("NOCTURNE_CORPUS_BLESS") == "1")
        {
            File.WriteAllText(path, EnumManifest.Render());
            return;
        }

        File.ReadAllText(path).ReplaceLineEndings("\n").Should().Be(
            EnumManifest.Render(),
            "a C# enum or condition model changed: update {0} and the Rust tables that mirror it", path);
    }
}
