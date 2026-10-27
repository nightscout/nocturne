using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Keeps an APS snapshot's prediction curves in one place, its <c>predicted_*</c> columns. The
/// decomposer and the v4 upload leave a curve out of the stored command or loop blob when the
/// columns already hold it, and every reader that rebuilds a command or loop status from those
/// blobs puts it back.
/// </summary>
/// <remarks>
/// A command's <c>predBGs</c> key is removed only when its value equals the curves the columns
/// rebuild, and a command whose key is absent gets those curves back. A command written without
/// curves beside a sibling that carried them (Trio does this) is stored with an explicit
/// <c>"predBGs": null</c>, so it reads back without them. A command with no key, beside no
/// sibling that carried curves, is left alone so it reads back with them.
/// <c>ApsSnapshotCurvesRewriter</c> applies the same rule to rows stored before it, in the
/// background.
/// </remarks>
internal static class ApsSnapshotCurves
{
    private const string PredBGsKey = "predBGs";
    private const string LoopPredictedKey = "predicted";
    private const string LoopValuesKey = "values";

    private static readonly JsonSerializerOptions CompareOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Uploaders re-send the last enacted command with every status while the loop holds a temp
    /// basal, so the later of enacted and suggested is the current one. Enacted wins a tie, and
    /// wins when neither timestamp parses.
    /// </summary>
    public static OpenApsSuggested? FreshestOpenApsCommand(OpenApsStatus openAps)
    {
        if (openAps.Enacted is null || openAps.Suggested is null)
            return openAps.Enacted ?? openAps.Suggested;

        var enactedTime = UploaderTimestamp.ParseUtcDateTime(openAps.Enacted.Timestamp);
        var suggestedTime = UploaderTimestamp.ParseUtcDateTime(openAps.Suggested.Timestamp);
        var suggestedIsLater = enactedTime is null ? suggestedTime is not null : suggestedTime > enactedTime;
        return suggestedIsLater ? openAps.Suggested : openAps.Enacted;
    }

    /// <summary>
    /// Fills the <c>predicted_*</c> columns <paramref name="aps"/> leaves empty from
    /// <paramref name="command"/>, the freshest OpenAPS command. Trio keeps no default curve.
    /// </summary>
    public static void FillColumns(ApsSnapshot aps, OpenApsSuggested? command)
    {
        var predBGs = command?.PredBGs;
        aps.PredictedIobJson ??= SerializeCurve(predBGs?.IOB);
        aps.PredictedZtJson ??= SerializeCurve(predBGs?.ZT);
        aps.PredictedCobJson ??= SerializeCurve(predBGs?.COB);
        aps.PredictedUamJson ??= SerializeCurve(predBGs?.UAM);
        if (aps.AidAlgorithm != AidAlgorithm.Trio)
            aps.PredictedDefaultJson ??= aps.PredictedIobJson;
        aps.PredictedStartTimestamp ??= UploaderTimestamp.ParseUtcDateTime(command?.Timestamp);
    }

    /// <summary>Fills the <c>predicted_*</c> columns <paramref name="aps"/> leaves empty from <paramref name="loop"/>.</summary>
    public static void FillColumns(ApsSnapshot aps, LoopStatus loop)
    {
        aps.PredictedDefaultJson ??= SerializeCurve(loop.Predicted?.Values);
        aps.PredictedStartTimestamp ??= UploaderTimestamp.ParseUtcDateTime(loop.Predicted?.StartDate);
    }

    /// <summary>
    /// Serializes <paramref name="command"/>, leaving out its curves when <paramref name="aps"/> holds them.
    /// <paramref name="aps"/> holds curves only because a command of this write carried them.
    /// </summary>
    public static string? SerializeCommand(OpenApsSuggested? command, ApsSnapshot aps, JsonSerializerOptions options)
    {
        if (command is null)
            return null;

        var node = JsonSerializer.SerializeToNode(command, command.GetType(), options)!.AsObject();
        StripCurves(node, command, aps, siblingCarried: true);
        return node.ToJsonString(options);
    }

    /// <summary>
    /// Serializes <paramref name="loop"/> without its predicted values, which
    /// <see cref="ApsSnapshot.PredictedDefaultJson"/> holds.
    /// </summary>
    public static string? SerializeLoop(LoopStatus? loop, ApsSnapshot aps, JsonSerializerOptions options)
    {
        if (loop is null)
            return null;

        var node = JsonSerializer.SerializeToNode(loop, options)!.AsObject();
        StripLoopValues(node, aps);
        return node.ToJsonString(options);
    }

    /// <summary>
    /// Brings a snapshot whose blobs arrive as JSON text into the shape the decomposer stores. A
    /// blob that cannot be rewritten (malformed, or with a repeated key) is stored as given, which
    /// the readers handle.
    /// </summary>
    public static void Normalize(ApsSnapshot aps)
    {
        var suggested = Parse<OpenApsSuggested>(aps.SuggestedJson);
        var enacted = Parse<OpenApsEnacted>(aps.EnactedJson);
        var loop = Parse<LoopStatus>(aps.LoopJson);

        if (loop is not null)
            FillColumns(aps, loop);
        else if (string.IsNullOrEmpty(aps.LoopJson))
            FillColumns(aps, FreshestOpenApsCommand(new OpenApsStatus { Suggested = suggested, Enacted = enacted }));

        var siblingCarried = suggested?.PredBGs is not null || enacted?.PredBGs is not null;
        aps.SuggestedJson = NormalizeCommand(aps.SuggestedJson, suggested, aps, siblingCarried);
        aps.EnactedJson = NormalizeCommand(aps.EnactedJson, enacted, aps, siblingCarried);
        aps.LoopJson = Edit(aps.LoopJson, node =>
        {
            StripNulls(node);
            StripLoopValues(node, aps);
        });
    }

    /// <summary>Puts <paramref name="aps"/>'s curves back into a stored command whose <c>predBGs</c> key is absent.</summary>
    public static string? RestoreCommand(string? commandJson, ApsSnapshot aps)
    {
        if (string.IsNullOrEmpty(commandJson) || OpenApsCurves(aps) is not { } curves)
            return commandJson;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(commandJson);
        }
        catch (JsonException)
        {
            return commandJson;
        }

        if (node is not JsonObject command || command.ContainsKey(PredBGsKey))
            return commandJson;

        command[PredBGsKey] = JsonSerializer.SerializeToNode(curves, CompareOptions);
        return command.ToJsonString();
    }

    /// <summary>Puts <paramref name="aps"/>'s predicted values back into a loop status stored without them.</summary>
    public static LoopStatus? RestoreLoop(LoopStatus? loop, ApsSnapshot aps)
    {
        if (loop?.Predicted is { Values: null } predicted)
            predicted.Values = Parse<double[]>(aps.PredictedDefaultJson);

        return loop;
    }

    private static OpenApsPredBGs? OpenApsCurves(ApsSnapshot aps)
    {
        var curves = new OpenApsPredBGs
        {
            IOB = Parse<List<double?>>(aps.PredictedIobJson),
            ZT = Parse<List<double?>>(aps.PredictedZtJson),
            COB = Parse<List<double?>>(aps.PredictedCobJson),
            UAM = Parse<List<double?>>(aps.PredictedUamJson),
        };

        return curves is { IOB: null, ZT: null, COB: null, UAM: null } ? null : curves;
    }

    private static void StripCurves(JsonObject node, OpenApsSuggested command, ApsSnapshot aps, bool siblingCarried)
    {
        if (OpenApsCurves(aps) is not { } curves)
            return;

        if (command.PredBGs is null)
        {
            if (siblingCarried)
                node[PredBGsKey] = null;
        }
        else if (SameCurves(command.PredBGs, curves))
        {
            node.Remove(PredBGsKey);
        }
    }

    private static void StripLoopValues(JsonObject node, ApsSnapshot aps)
    {
        if (node[LoopPredictedKey] is JsonObject predicted
            && predicted[LoopValuesKey] is { } values
            && ParseNode(aps.PredictedDefaultJson) is { } held
            && JsonNode.DeepEquals(values, held))
        {
            predicted.Remove(LoopValuesKey);
        }
    }

    private static string? NormalizeCommand(string? json, OpenApsSuggested? command, ApsSnapshot aps, bool siblingCarried) =>
        command is null
            ? json
            : Edit(json, node =>
            {
                StripNulls(node, keep: PredBGsKey);
                StripCurves(node, command, aps, siblingCarried);
            });

    private static string? Edit(string? json, Action<JsonObject> edit)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject node)
                return json;

            edit(node);
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            return json;
        }
        catch (ArgumentException)
        {
            return json;
        }
    }

    private static void StripNulls(JsonNode? node, string? keep = null)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToList())
            {
                if (value is not null)
                    StripNulls(value);
                else if (key != keep)
                    obj.Remove(key);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                StripNulls(item);
        }
    }

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SerializeCurve<T>(T? curve) where T : class =>
        curve is null ? null : JsonSerializer.Serialize(curve);

    private static bool SameCurves(OpenApsPredBGs a, OpenApsPredBGs b) =>
        JsonSerializer.Serialize(a, CompareOptions) == JsonSerializer.Serialize(b, CompareOptions);

    // A column or blob a client sent may be any text, so text that does not parse reads as absent.
    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
