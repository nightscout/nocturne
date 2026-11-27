using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using NpgsqlTypes;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Rewrites every stored condition tree of every alert rule from condition schema 1 to schema 2
/// with the engine's own migration (docs/alerts/engine-semantics.md §1.5): the body, the
/// auto-resolve tree, each smart-snooze condition and the trees kept in <c>pre_repair</c>, of every
/// rule, enabled or not.
/// </summary>
/// <remarks>
/// Invoked inline at startup, after the database migrations and before the server accepts
/// requests or any hosted service evaluates a rule: the engine reads schema 2 only, so a rule not
/// yet rewritten would be skipped as unevaluable. A tree that cannot be read as schema 1 is left as
/// it is; it was not evaluable before either, and it now carries the unevaluable notice.
/// <para>
/// Only the trees and the review reason are written. The rule's version stamps, its re-arm hold
/// and its sustained timers stay as they are: the migration changes no answer the engine gives, and
/// a timer's key is a structural condition path, which it keeps. A tree that already names schema 2
/// is left alone, so a later run writes nothing. A row is written only while it still holds what was
/// read, so instances booting together, or a rule saved meanwhile, never lose a write.
/// </para>
/// </remarks>
internal static class ConditionSchemaMigration
{
    private const string ConditionScope = "condition";
    private const string SnoozeConfigurationSection = "snooze";
    private const string SnoozeConditionsProperty = "conditions";
    private const string PreRepairConditionType = "condition_type";
    private const string PreRepairConditionParams = "condition_params";
    private const string PreRepairAutoResolveParams = "auto_resolve_params";

    /// <summary>One rule's stored trees, read as the text the database holds.</summary>
    internal sealed class StoredRule
    {
        public Guid Id { get; init; }

        /// <summary>The stored wire string, read raw: a type no member names is not read as another.</summary>
        public string ConditionType { get; init; } = string.Empty;

        public string ConditionParams { get; init; } = "{}";
        public string? AutoResolveParams { get; init; }
        public string ClientConfiguration { get; init; } = "{}";
        public string? PreRepair { get; init; }
        public string? ReviewReason { get; init; }
    }

    /// <summary>A rule's trees after the migration; <see cref="Changed"/> is false when it has nothing to store.</summary>
    internal sealed record MigratedRule(
        string ConditionParams,
        string? AutoResolveParams,
        string ClientConfiguration,
        string? PreRepair,
        string? ReviewReason,
        bool Changed,
        bool Rounded,
        int Failed);

    /// <summary>What a run did, for its summary line.</summary>
    internal readonly record struct Summary(
        int TenantsChecked, int Rules, int Rewritten, int ForReview, int TreesLeft, int Raced, int TenantsFailed);

    /// <summary>Migrates every tenant's rules through the root-resolved context factory and the native engine.</summary>
    public static Task<Summary> RunAsync(IServiceProvider services, CancellationToken ct = default) =>
        RunAsync(
            services.GetRequiredService<ITenantRepository>(),
            services.GetRequiredService<INocturneDbContextFactory>(),
            RustAlertEngine.Migrate,
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ConditionSchemaMigration)),
            ct);

    /// <param name="contexts">
    /// A factory with no scope, whose contexts read as no reader: each tenant's rules are read and
    /// written through a context pinned to it, which FORCE ROW LEVEL SECURITY requires.
    /// </param>
    internal static async Task<Summary> RunAsync(
        ITenantRepository tenantRepository,
        INocturneDbContextFactory contexts,
        Func<RustMigrateRequest, RustMigrateResponse> migrate,
        ILogger logger,
        CancellationToken ct)
    {
        var tenants = await tenantRepository.ListIdsAsync(activeOnly: false, ct);

        int rules = 0, rewritten = 0, forReview = 0, treesLeft = 0, raced = 0, tenantsFailed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var db = await contexts.CreateForTenantAsync(tenant, ct);
                var stored = await db.Database.SqlQueryRaw<StoredRule>(ReadSql).ToListAsync(ct);
                foreach (var rule in stored)
                {
                    rules++;
                    var migrated = Migrate(rule, migrate, logger);
                    treesLeft += migrated.Failed;
                    if (!migrated.Changed)
                        continue;
                    if (await db.Database.ExecuteSqlRawAsync(WriteSql, WriteParameters(rule, migrated), ct) == 0)
                    {
                        raced++;
                        continue;
                    }
                    rewritten++;
                    if (migrated.Rounded && rule.ReviewReason is null)
                        forReview++;
                }
            }
            catch (DbException ex)
            {
                tenantsFailed++;
                LogTenantFailed(logger, ex, tenant);
            }
            catch (RetryLimitExceededException ex)
            {
                tenantsFailed++;
                LogTenantFailed(logger, ex, tenant);
            }
        }

        var summary = new Summary(tenants.Count, rules, rewritten, forReview, treesLeft, raced, tenantsFailed);
        logger.LogInformation(
            "Alert condition schema {Version}: checked {Rules} rules of {Tenants} tenants, rewrote {Rewritten}, asked for review of {ForReview}, left {TreesLeft} trees that cannot be read, found {Raced} changed meanwhile, failed on {TenantsFailed} tenants",
            ConditionSchema.Version, summary.Rules, summary.TenantsChecked, summary.Rewritten, summary.ForReview,
            summary.TreesLeft, summary.Raced, summary.TenantsFailed);
        return summary;
    }

    private static void LogTenantFailed(ILogger logger, Exception ex, Guid tenant) =>
        logger.LogError(ex,
            "Could not migrate the alert condition trees of tenant {TenantId} to schema 2; they are migrated on the next startup",
            tenant);

    private const string ReadSql = """
        SELECT id AS "Id",
               condition_type AS "ConditionType",
               condition_params::text AS "ConditionParams",
               auto_resolve_params::text AS "AutoResolveParams",
               client_configuration::text AS "ClientConfiguration",
               pre_repair::text AS "PreRepair",
               review_reason AS "ReviewReason"
        FROM alert_rules
        """;

    private const string WriteSql = """
        UPDATE alert_rules
        SET condition_params = @condition_params,
            auto_resolve_params = @auto_resolve_params,
            client_configuration = @client_configuration,
            pre_repair = @pre_repair,
            review_reason = COALESCE(review_reason, @review_reason)
        WHERE id = @id
          AND condition_params = @was_condition_params
          AND auto_resolve_params IS NOT DISTINCT FROM @was_auto_resolve_params
          AND client_configuration = @was_client_configuration
          AND pre_repair IS NOT DISTINCT FROM @was_pre_repair
        """;

    private static NpgsqlParameter[] WriteParameters(StoredRule rule, MigratedRule migrated) =>
    [
        new("id", rule.Id),
        Json("condition_params", migrated.ConditionParams),
        Json("auto_resolve_params", migrated.AutoResolveParams),
        Json("client_configuration", migrated.ClientConfiguration),
        Json("pre_repair", migrated.PreRepair),
        new("review_reason", NpgsqlDbType.Varchar) { Value = (object?)migrated.ReviewReason ?? DBNull.Value },
        Json("was_condition_params", rule.ConditionParams),
        Json("was_auto_resolve_params", rule.AutoResolveParams),
        Json("was_client_configuration", rule.ClientConfiguration),
        Json("was_pre_repair", rule.PreRepair),
    ];

    private static NpgsqlParameter Json(string name, string? json) =>
        new(name, NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value };

    /// <summary>
    /// <paramref name="rule"/>'s trees in schema 2. A rounded bound in a tree the engine evaluates
    /// asks for review (<see cref="AlertRuleReviewReasons.ConditionDurationRounded"/>) unless the rule
    /// already has a reason; one in <c>pre_repair</c>, which nothing evaluates, does not.
    /// </summary>
    internal static MigratedRule Migrate(
        StoredRule rule, Func<RustMigrateRequest, RustMigrateResponse> migrate, ILogger logger)
    {
        var changed = false;
        var rounded = false;
        var failed = 0;

        string? Live(string? json, Func<JsonElement, RustMigrateRequest> request, string scope)
        {
            if (json is null)
                return null;
            TreeOutcome outcome;
            try
            {
                outcome = MigrateTree(json, request, migrate);
            }
            catch (RustAlertEngineException ex)
            {
                failed++;
                logger.LogWarning(ex, "The {Scope} tree of alert rule {RuleId} could not be migrated to condition schema 2 and was left as it is",
                    scope, rule.Id);
                return json;
            }
            switch (outcome)
            {
                case { Tree: { } tree }:
                    changed = true;
                    rounded |= outcome.Rounded;
                    return tree;
                case { Issue: { } issue }:
                    failed++;
                    logger.LogWarning(
                        "The {Scope} tree of alert rule {RuleId} cannot be read as condition schema 1 and was left as it is: {Reason} at {Path}, field {Field}",
                        scope, rule.Id, issue.Reason, issue.Path, issue.Field);
                    return json;
                default:
                    return json;
            }
        }

        var body = Live(rule.ConditionParams, tree => RustMigrateRequest.Body(rule.ConditionType, tree), ConditionScope)!;
        var autoResolve = IsAbsent(rule.AutoResolveParams)
            ? rule.AutoResolveParams
            : Live(rule.AutoResolveParams, tree => RustMigrateRequest.Node(AlertConditionTypeNames.AutoResolvePathRoot, tree),
                AlertConditionTypeNames.AutoResolvePathRoot);

        var clientConfiguration = rule.ClientConfiguration;
        if (SnoozeConditions(rule.ClientConfiguration) is var (configuration, conditions))
        {
            var snoozeChanged = false;
            for (var i = 0; i < conditions.Count; i++)
            {
                var stored = conditions[i]?.ToJsonString() ?? "null";
                var tree = Live(stored, t => RustMigrateRequest.Node(AlertConditionTypeNames.SnoozePathRoot, t),
                    $"{AlertConditionTypeNames.SnoozePathRoot}[{i}]");
                if (ReferenceEquals(tree, stored))
                    continue;
                conditions[i] = JsonNode.Parse(tree!);
                snoozeChanged = true;
            }
            if (snoozeChanged)
                clientConfiguration = configuration.ToJsonString();
        }

        var preRepair = MigratePreRepair(rule, migrate, logger, ref changed);

        var reviewReason = rounded ? rule.ReviewReason ?? AlertRuleReviewReasons.ConditionDurationRounded : rule.ReviewReason;
        return new MigratedRule(body, autoResolve, clientConfiguration, preRepair, reviewReason, changed, rounded, failed);
    }

    private sealed record TreeOutcome(string? Tree, bool Rounded, RustMigrateIssue? Issue);

    private static TreeOutcome MigrateTree(
        string json, Func<JsonElement, RustMigrateRequest> request, Func<RustMigrateRequest, RustMigrateResponse> migrate)
    {
        JsonElement tree;
        try
        {
            using var document = JsonDocument.Parse(json);
            tree = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new TreeOutcome(null, false, new RustMigrateIssue("", "not_json", null));
        }
        if (ConditionSchema.Names(tree))
            return new TreeOutcome(null, false, null);

        var response = migrate(request(tree));
        return response.Outcome switch
        {
            RustMigrateOutcome.Migrated => new TreeOutcome(response.Tree!.Value.GetRawText(), response.Rounded, null),
            RustMigrateOutcome.Failed => new TreeOutcome(null, false, response.Issue),
            _ => new TreeOutcome(null, false, null),
        };
    }

    /// <summary>
    /// The <c>snooze.conditions</c> list of <paramref name="clientConfiguration"/> as nodes of the
    /// whole configuration, so a condition replaced in the list is written back with every other key
    /// as it was. Null when there is no list.
    /// </summary>
    private static (JsonNode Configuration, JsonArray Conditions)? SnoozeConditions(string clientConfiguration)
    {
        JsonNode? configuration;
        try
        {
            configuration = JsonNode.Parse(clientConfiguration);
        }
        catch (JsonException)
        {
            return null;
        }
        return configuration is JsonObject root
               && root[SnoozeConfigurationSection] is JsonObject snooze
               && snooze[SnoozeConditionsProperty] is JsonArray conditions
            ? (root, conditions)
            : null;
    }

    /// <summary>
    /// <c>pre_repair</c> with its body and auto-resolve tree migrated where they can be. Those trees
    /// predate the redesign and are kept only to undo a repair by hand, so one that cannot be read is
    /// left at Debug.
    /// </summary>
    private static string? MigratePreRepair(
        StoredRule rule, Func<RustMigrateRequest, RustMigrateResponse> migrate, ILogger logger, ref bool changed)
    {
        if (rule.PreRepair is null)
            return null;
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(rule.PreRepair);
        }
        catch (JsonException)
        {
            return rule.PreRepair;
        }
        if (parsed is not JsonObject preRepair)
            return rule.PreRepair;

        var preRepairChanged = false;
        if (preRepair[PreRepairConditionType] is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var conditionType)
            && preRepair[PreRepairConditionParams] is JsonObject body)
        {
            preRepairChanged |= Replace(
                preRepair, PreRepairConditionParams, body,
                tree => RustMigrateRequest.Body(conditionType, tree));
        }
        if (preRepair[PreRepairAutoResolveParams] is JsonObject autoResolve)
        {
            preRepairChanged |= Replace(
                preRepair, PreRepairAutoResolveParams, autoResolve,
                tree => RustMigrateRequest.Node(AlertConditionTypeNames.AutoResolvePathRoot, tree));
        }

        if (!preRepairChanged)
            return rule.PreRepair;
        changed = true;
        return preRepair.ToJsonString();

        bool Replace(JsonObject parent, string property, JsonObject tree, Func<JsonElement, RustMigrateRequest> request)
        {
            TreeOutcome outcome;
            try
            {
                outcome = MigrateTree(tree.ToJsonString(), request, migrate);
            }
            catch (RustAlertEngineException ex)
            {
                logger.LogDebug(ex, "The pre-repair {Property} of alert rule {RuleId} was left as it is", property, rule.Id);
                return false;
            }
            if (outcome.Issue is { } issue)
            {
                logger.LogDebug(
                    "The pre-repair {Property} of alert rule {RuleId} cannot be read as condition schema 1 and was left as it is: {Reason} at {Path}, field {Field}",
                    property, rule.Id, issue.Reason, issue.Path, issue.Field);
                return false;
            }
            if (outcome.Tree is not { } migrated)
                return false;
            parent[property] = JsonNode.Parse(migrated);
            return true;
        }
    }

    private static bool IsAbsent(string? json) =>
        json is null || json.Trim() == "null";
}
