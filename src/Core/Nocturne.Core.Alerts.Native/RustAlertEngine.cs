using System.Text.Json;

namespace Nocturne.Core.Alerts.Native;

/// <summary>
/// Thrown when the Rust alert engine returns an <c>ok: false</c> envelope or
/// produces an unintelligible response.
/// </summary>
public sealed class RustAlertEngineException : Exception
{
    public RustAlertEngineException(string message) : base(message)
    {
    }

    public RustAlertEngineException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Typed wrapper over <see cref="AlertsInterop"/>: serialises the request
/// envelope, calls the native library and deserialises the response. All
/// evaluation state (timers, tracker) is carried as data — the caller
/// persists what comes back and threads it into the next call.
/// </summary>
public static class RustAlertEngine
{
    /// <summary>The native crate version (e.g. <c>"0.1.0"</c>).</summary>
    public static string Version() => AlertsInterop.GetVersion();

    /// <summary>
    /// Evaluates one rule for one tick through the Rust engine.
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>) or returned an
    /// unparseable response.
    /// </exception>
    public static RustEvaluateResponse Evaluate(RustEvaluateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestJson = JsonSerializer.Serialize(request, AlertEnvelopeJson.Options);
        var responseJson = AlertsInterop.Evaluate(requestJson);
        var response = Deserialize<RustEvaluateResponse>(responseJson, "evaluate");

        if (!response.Ok)
            throw new RustAlertEngineException($"Rust alert engine rejected the evaluate request: {response.Error ?? "(no error message)"}");
        if (response.Result is null)
            throw new RustAlertEngineException("Rust alert engine returned ok without a result");
        return response;
    }

    /// <summary>
    /// Convenience overload building the request envelope from its parts.
    /// </summary>
    public static RustEvaluateResponse Evaluate(
        RustAlertRule rule,
        JsonElement context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime>? timers = null,
        RustTrackerState? tracker = null)
    {
        return Evaluate(new RustEvaluateRequest
        {
            Rule = rule,
            Context = context,
            Now = now,
            Timers = timers is null ? null : new Dictionary<string, DateTime>(timers),
            Tracker = tracker,
        });
    }

    /// <summary>
    /// Deserializes the typed rule result out of an evaluate response.
    /// </summary>
    /// <exception cref="RustAlertEngineException">The result payload was unparseable.</exception>
    public static RustRuleResult GetRuleResult(RustEvaluateResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Result is null)
            throw new RustAlertEngineException("Rust alert engine response has no result payload");
        try
        {
            return response.Result.Value.Deserialize<RustRuleResult>(AlertEnvelopeJson.Options)
                ?? throw new RustAlertEngineException("Rust alert engine returned a null rule result");
        }
        catch (JsonException ex)
        {
            throw new RustAlertEngineException(
                $"Rust alert engine returned an unparseable rule result: {response.Result.Value.GetRawText()}", ex);
        }
    }

    /// <summary>
    /// Evaluates one condition node for one instant outside the per-rule driver
    /// (no tracker, no auto-resolve) — auxiliary scopes such as smart-snooze
    /// conditions (<c>root: "snooze"</c>) and the sweep's periodic auto-resolve
    /// (<c>root: "auto_resolve"</c>).
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>, e.g. a structurally
    /// malformed node) or returned an unparseable response.
    /// </exception>
    public static RustEvaluateNodeResponse EvaluateNode(RustEvaluateNodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestJson = JsonSerializer.Serialize(request, AlertEnvelopeJson.Options);
        var responseJson = AlertsInterop.EvaluateNode(requestJson);
        var response = Deserialize<RustEvaluateNodeResponse>(responseJson, "evaluate_node");

        if (!response.Ok)
            throw new RustAlertEngineException($"Rust alert engine rejected the evaluate_node request: {response.Error ?? "(no error message)"}");
        return response;
    }

    /// <summary>
    /// Enumerates the canonical condition paths and leaf ids of a condition
    /// tree (a full ConditionNode object). <paramref name="root"/> overrides
    /// the root path segment (e.g. <c>"auto_resolve"</c>); by default the
    /// node's verbatim <c>type</c> string is used.
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The node was malformed (<c>ok: false</c>) or the response was unparseable.
    /// </exception>
    public static RustLeafPathsResponse LeafPaths(JsonElement conditionNode, string? root = null)
    {
        string inputJson;
        if (root is null)
        {
            inputJson = conditionNode.GetRawText();
        }
        else
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("root", root);
                writer.WritePropertyName("node");
                conditionNode.WriteTo(writer);
                writer.WriteEndObject();
            }
            inputJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        var responseJson = AlertsInterop.LeafPaths(inputJson);
        var response = Deserialize<RustLeafPathsResponse>(responseJson, "leaf_paths");

        if (!response.Ok)
            throw new RustAlertEngineException($"Rust alert engine rejected the leaf_paths request: {response.Error ?? "(no error message)"}");
        return response;
    }

    /// <summary>
    /// Derives a rule's scope class (<c>low | high | composite | undirected</c>)
    /// for scoped Do Not Disturb from its condition type + params. Returns the
    /// wire string; the host maps it onto its <c>RuleScopeClass</c> enum. An
    /// unclassifiable rule comes back as <c>"undirected"</c> (the engine's
    /// all-only safe default), not an error.
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>, e.g. a malformed
    /// envelope) or returned an unparseable response.
    /// </exception>
    public static string Classify(RustClassifyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestJson = JsonSerializer.Serialize(request, AlertEnvelopeJson.Options);
        var responseJson = AlertsInterop.Classify(requestJson);
        var response = Deserialize<RustClassifyResponse>(responseJson, "classify");

        if (!response.Ok)
            throw new RustAlertEngineException($"Rust alert engine rejected the classify request: {response.Error ?? "(no error message)"}");
        if (response.ScopeClass is null)
            throw new RustAlertEngineException("Rust alert engine returned ok without a scope_class");
        return response.ScopeClass;
    }

    private static T Deserialize<T>(string responseJson, string operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(responseJson, AlertEnvelopeJson.Options)
                ?? throw new RustAlertEngineException($"Rust alert engine returned a null {operation} response");
        }
        catch (JsonException ex)
        {
            throw new RustAlertEngineException($"Rust alert engine returned an unparseable {operation} response: {responseJson}", ex);
        }
    }
}
