using System.Text.Json;

namespace Nocturne.Core.Alerts.Native;

/// <summary>
/// Thrown when the Rust alert engine returns an <c>ok: false</c> envelope, or a response that
/// is unparseable, of another <c>schema_version</c>, or missing a field its shape requires.
/// </summary>
/// <remarks>
/// The message names the operation and the offending field or JSON path, never the response
/// body: that body carries the tenant's glucose context, and this message is logged.
/// </remarks>
public sealed class RustAlertEngineException : Exception
{
    public RustAlertEngineException(string message) : base(message)
    {
    }

    public RustAlertEngineException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <param name="rejection">The <c>error</c> of the engine's <c>ok: false</c> envelope.</param>
    public RustAlertEngineException(string message, string? rejection) : base(message)
    {
        Rejection = rejection;
    }

    /// <summary>
    /// The <c>error</c> the engine answered with when it rejected the request, or
    /// <see langword="null"/> when the host refused the engine's response or the call failed.
    /// </summary>
    public string? Rejection { get; }

    /// <summary>
    /// The engine rejected the request's condition tree, node or kind as one it cannot evaluate
    /// (docs/alerts/engine-semantics.md §1.4). The fault is in the stored rule, not the engine.
    /// </summary>
    public bool IsConditionRejection =>
        Rejection is { } error
        && (error.StartsWith("malformed condition", StringComparison.Ordinal)
            || error.StartsWith("unknown condition_type", StringComparison.Ordinal));

    /// <summary>
    /// The rule a replay stopped at because it changed more often between two ticks than the
    /// engine steps (docs/alerts/engine-semantics.md §8), else <see langword="null"/>.
    /// </summary>
    public Guid? ReplayStepLimitRule =>
        Rejection is { } error
        && error.StartsWith("rule ", StringComparison.Ordinal)
        && error.Length >= 41
        && error.Contains(" changes more than ", StringComparison.Ordinal)
        && Guid.TryParse(error.AsSpan(5, 36), out var rule)
            ? rule
            : null;
}

/// <summary>
/// Typed wrapper over <see cref="AlertsInterop"/>: serialises the request
/// envelope, calls the native library and deserialises the response. All
/// evaluation state (timers, tracker) is carried as data — the caller
/// persists what comes back and threads it into the next call.
/// </summary>
public static partial class RustAlertEngine
{
    /// <summary>The native crate version (e.g. <c>"0.1.0"</c>).</summary>
    public static string Version() => AlertsInterop.GetVersion();

    /// <inheritdoc cref="AlertsInterop.GetTzdbVersion"/>
    public static string TzdbVersion() => AlertsInterop.GetTzdbVersion();

    private static readonly Lazy<TimeSpan> Tolerance = new(AlertsInterop.GetClockTolerance);

    /// <summary>
    /// How far ahead of now an anchor may be and still read as now
    /// (docs/alerts/engine-semantics.md §5.4); the bound a host selects an alert context's
    /// reading by.
    /// </summary>
    public static TimeSpan ClockTolerance() => Tolerance.Value;

    private static readonly Lazy<TimeSpan> Bound = new(AlertsInterop.GetHoldBound);

    /// <summary>
    /// How long a rule may hold before its hold is reported (docs/alerts/engine-semantics.md
    /// §6.4); a held rule is evaluated on the sweep once its hold has lasted this long.
    /// </summary>
    public static TimeSpan HoldBound() => Bound.Value;

    private static readonly Lazy<bool> NextChangeReported = new(() => ReportsNextChange(AlertsInterop.Evaluate));

    /// <summary>
    /// Whether the loaded library reports <c>next_change_at</c> (docs/alerts/engine-semantics.md
    /// §5.3). The field is omitted when there is none, so from a library that predates it every
    /// result would read as "nothing changes"; a host records a next change only when this holds.
    /// </summary>
    public static bool ReportsNextChange() => NextChangeReported.Value;

    /// <summary>
    /// Whether <paramref name="evaluate"/> reports a next change for a signal loss due in ten
    /// minutes, which has one.
    /// </summary>
    internal static bool ReportsNextChange(Func<string, string> evaluate)
    {
        var now = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
        var request = Serialize(new RustEvaluateRequest
        {
            Rule = new RustAlertRule
            {
                Id = Guid.Empty,
                ConditionType = "signal_loss",
                ConditionParams = JsonSerializer.SerializeToElement(new { timeout_minutes = 15 }),
            },
            Context = JsonSerializer.SerializeToElement(new { last_reading_at = now.AddMinutes(-5) }),
            Now = now,
        });
        try
        {
            return GetRuleResult(ParseEvaluateResponse(evaluate(request)), leavesRequested: false).NextChangeAt is not null;
        }
        catch (RustAlertEngineException)
        {
            return false;
        }
    }

    /// <summary>
    /// Evaluates one rule for one tick through the Rust engine.
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>) or returned a response
    /// <see cref="ParseEvaluateResponse"/> refuses.
    /// </exception>
    public static RustEvaluateResponse Evaluate(RustEvaluateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ParseEvaluateResponse(AlertsInterop.Evaluate(Serialize(request)));
    }

    /// <summary>
    /// A successful evaluate envelope carries <c>result</c>, <c>timers</c>, <c>not_true</c> and <c>tracker</c>,
    /// and a tracker holding a <c>state</c> carries its <c>updated_at</c>.
    /// </summary>
    internal static RustEvaluateResponse ParseEvaluateResponse(string responseJson)
    {
        var response = ParseResponse<RustEvaluateResponse>(responseJson, "evaluate");
        Require(response.Result is not null, "evaluate", "result");
        Require(response.Timers is not null, "evaluate", "timers");
        Require(response.NotTrue is not null, "evaluate", "not_true");
        Require(response.Tracker is not null, "evaluate", "tracker");
        Require(response.Tracker!.State is null || response.Tracker.UpdatedAt is not null, "evaluate", "tracker.updated_at");
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
        RustTrackerState? tracker = null,
        bool includeLeaves = false,
        IReadOnlyDictionary<string, DateTime>? notTrue = null)
    {
        return Evaluate(new RustEvaluateRequest
        {
            Rule = rule,
            Context = context,
            Now = now,
            Timers = timers is null ? null : new Dictionary<string, DateTime>(timers),
            NotTrue = notTrue is null ? null : new Dictionary<string, DateTime>(notTrue),
            Tracker = tracker,
            IncludeLeaves = includeLeaves,
        });
    }

    /// <summary>
    /// Deserializes the typed rule result out of an evaluate response, requiring the fields a
    /// result that was not skipped always carries, and its leaves when
    /// <paramref name="leavesRequested"/>.
    /// </summary>
    /// <exception cref="RustAlertEngineException">The result payload was unparseable or incomplete.</exception>
    public static RustRuleResult GetRuleResult(RustEvaluateResponse response, bool leavesRequested = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        Require(response.Result is not null, "evaluate", "result");
        RustRuleResult? result;
        try
        {
            result = response.Result!.Value.Deserialize<RustRuleResult>(AlertEnvelopeJson.Options);
        }
        catch (JsonException ex)
        {
            throw new RustAlertEngineException(
                $"Rust alert engine returned an unparseable evaluate result at {ex.Path ?? "$"}", ex);
        }
        Require(result is not null, "evaluate", "result");
        if (result!.Skipped)
            return result;

        Require(result.Root is not null, "evaluate", "result.root");
        Require(!leavesRequested || result.Leaves is not null, "evaluate", "result.leaves");
        Require(result.Transition is not null, "evaluate", "result.transition");
        Require((result.Transition == RustTransition.Closed) == (result.CloseReason is not null), "evaluate", "result.close_reason");
        return result;
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
        return ParseEvaluateNodeResponse(AlertsInterop.EvaluateNode(Serialize(request)));
    }

    /// <summary>A successful evaluate_node envelope carries <c>value</c>, <c>timers</c> and <c>not_true</c>.</summary>
    internal static RustEvaluateNodeResponse ParseEvaluateNodeResponse(string responseJson)
    {
        var response = ParseResponse<RustEvaluateNodeResponse>(responseJson, "evaluate_node");
        Require(response.Value is not null, "evaluate_node", "value");
        Require(response.Timers is not null, "evaluate_node", "timers");
        Require(response.NotTrue is not null, "evaluate_node", "not_true");
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

        var response = ParseResponse<RustLeafPathsResponse>(AlertsInterop.LeafPaths(inputJson), "leaf_paths");
        Require(response.Root is not null, "leaf_paths", "root");
        Require(response.Paths is not null, "leaf_paths", "paths");
        Require(response.Leaves is not null, "leaf_paths", "leaves");
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

        var response = ParseResponse<RustClassifyResponse>(AlertsInterop.Classify(Serialize(request)), "classify");
        Require(response.ScopeClass is not null, "classify", "scope_class");
        return response.ScopeClass!;
    }

    /// <summary>
    /// Whether the rule must also be evaluated on the wall clock (engine-semantics.md §5.1). An
    /// unknown type or unevaluable body is false, not an error.
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>, a malformed envelope) or returned an
    /// unparseable response.
    /// </exception>
    public static bool ReferencesWallClock(RustRuleConditionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = ParseResponse<RustWallClockResponse>(
            AlertsInterop.ReferencesWallClock(Serialize(request)), "references_wall_clock");
        Require(response.ReferencesWallClock is not null, "references_wall_clock", "references_wall_clock");
        return response.ReferencesWallClock!.Value;
    }

    /// <summary>
    /// The context facts a stored rule reads, by wire name (docs/alerts/engine-semantics.md §5.2).
    /// An unknown type or unevaluable body reads none, not an error.
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>, a malformed envelope) or returned an
    /// unparseable response.
    /// </exception>
    public static IReadOnlyList<string> RuleReads(RustRuleConditionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = ParseResponse<RustRuleReadsResponse>(
            AlertsInterop.RuleReads(Serialize(request)), "rule_reads");
        Require(response.Reads is not null, "rule_reads", "reads");
        return response.Reads!;
    }

    /// <summary>
    /// The rule's condition trees as the engine reads them: every node's path, and each leaf's
    /// resolved kind and decoded operands (crates/nocturne-alerts-ffi/README.md, "Describe").
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>: an unknown condition type or a
    /// malformed envelope) or returned an unparseable response.
    /// </exception>
    public static RustDescribeResponse Describe(RustDescribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ParseResponse<RustDescribeResponse>(AlertsInterop.Describe(Serialize(request)), "describe");
    }

    /// <summary>
    /// Every problem saving the rule's condition trees should reject, each with its scope,
    /// condition path and reason code, in <see cref="RustValidateResponse.Issues"/>; empty when
    /// the rule is valid.
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>, a malformed envelope) or returned an
    /// unparseable response.
    /// </exception>
    public static RustValidateResponse Validate(RustValidateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = ParseResponse<RustValidateResponse>(AlertsInterop.Validate(Serialize(request)), "validate");
        Require(response.Issues is not null, "validate", "issues");
        return response;
    }

    /// <summary>Replays a rule set over a series of ticks through the Rust engine.</summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request (<c>ok: false</c>) or returned a response
    /// <see cref="ParseReplayResponse"/> refuses.
    /// </exception>
    public static RustReplayResponse Replay(RustReplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ParseReplayResponse(AlertsInterop.Replay(Serialize(request)), request.IncludeTicks);
    }

    /// <summary>
    /// A successful replay envelope carries <c>order</c>, <c>events</c> and
    /// <c>leaf_transitions</c>, and <c>ticks</c> exactly when they were asked for, with
    /// <c>met</c> and <c>firing</c> on every rule tick that was not skipped.
    /// </summary>
    internal static RustReplayResponse ParseReplayResponse(string responseJson, bool ticksRequested)
    {
        var response = ParseResponse<RustReplayResponse>(responseJson, "replay");
        Require(response.Order is not null, "replay", "order");
        Require(response.Events is not null, "replay", "events");
        Require(response.LeafTransitions is not null, "replay", "leaf_transitions");
        Require(!ticksRequested || response.Ticks is not null, "replay", "ticks");
        foreach (var rule in response.Ticks?.SelectMany(t => t.Rules) ?? [])
            Require(rule.Skipped || (rule.Met is not null && rule.Firing is not null), "replay", "ticks.rules.met");
        return response;
    }

    private static string Serialize<T>(T request) => JsonSerializer.Serialize(request, AlertEnvelopeJson.Options);

    /// <summary>
    /// Parses a response envelope, requiring <c>schema_version</c> to be
    /// <see cref="AlertEnvelopeJson.SchemaVersion"/> and <c>ok</c> to be true.
    /// </summary>
    internal static T ParseResponse<T>(string responseJson, string operation) where T : IRustResponseEnvelope
    {
        var response = Deserialize<T>(responseJson, operation);
        if (response.SchemaVersion != AlertEnvelopeJson.SchemaVersion)
            throw new RustAlertEngineException(
                $"Rust alert engine answered {operation} with schema_version {response.SchemaVersion}, expected {AlertEnvelopeJson.SchemaVersion}");
        if (!response.Ok)
            throw new RustAlertEngineException(
                $"Rust alert engine rejected the {operation} request: {response.Error ?? "(no error message)"}",
                response.Error);
        return response;
    }

    private static void Require(bool present, string operation, string field)
    {
        if (!present)
            throw new RustAlertEngineException($"Rust alert engine {operation} response is missing '{field}'");
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
            throw new RustAlertEngineException(
                $"Rust alert engine returned an unparseable {operation} response at {ex.Path ?? "$"}", ex);
        }
    }
}
