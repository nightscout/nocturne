using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Keeps an APS snapshot's prediction curves in one place, its <c>predicted_*</c> columns. The
/// decomposer leaves a curve out of the stored command or loop blob when the columns already hold
/// it, and every reader that rebuilds a command or loop status from those blobs puts it back.
/// </summary>
/// <remarks>
/// A command's <c>predBGs</c> key is removed only when its value equals the curves the columns
/// rebuild, and a command whose key is absent gets those curves back. A command uploaded without
/// curves beside a sibling that carried them (Trio does this) is stored with an explicit
/// <c>"predBGs": null</c>, so it reads back without them. <c>ApsSnapshotCurvesRewriter</c>
/// applies the same rule to rows stored before it, in the background.
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

    /// <summary>Serializes <paramref name="command"/>, leaving out its curves when <paramref name="aps"/> holds them.</summary>
    public static string? SerializeCommand(OpenApsSuggested? command, ApsSnapshot aps, JsonSerializerOptions options)
    {
        if (command is null)
            return null;

        var node = JsonSerializer.SerializeToNode(command, command.GetType(), options)!.AsObject();
        if (OpenApsCurves(aps) is { } curves)
        {
            if (command.PredBGs is null)
                node[PredBGsKey] = null;
            else if (SameCurves(command.PredBGs, curves))
                node.Remove(PredBGsKey);
        }

        return node.ToJsonString(options);
    }

    /// <summary>
    /// Serializes <paramref name="loop"/> without its predicted values, which
    /// <see cref="ApsSnapshot.PredictedDefaultJson"/> holds.
    /// </summary>
    public static string? SerializeLoop(LoopStatus? loop, JsonSerializerOptions options)
    {
        if (loop is null)
            return null;

        var node = JsonSerializer.SerializeToNode(loop, options)!.AsObject();
        (node[LoopPredictedKey] as JsonObject)?.Remove(LoopValuesKey);
        return node.ToJsonString(options);
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

    private static bool SameCurves(OpenApsPredBGs a, OpenApsPredBGs b) =>
        JsonSerializer.Serialize(a, CompareOptions) == JsonSerializer.Serialize(b, CompareOptions);

    // The v4 upsert endpoint stores these columns as the client sent them.
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
