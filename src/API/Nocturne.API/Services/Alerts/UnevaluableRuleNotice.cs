using System.Security.Cryptography;
using System.Text;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using NoticeRule = Nocturne.API.Services.Alerts.RuleNoticeDispatch.NoticeRule;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Tells a rule's recipients, once per version of its condition, that the rule cannot fire until
/// it is edited, or that its auto-resolve or smart snooze does nothing until it is edited
/// (docs/alerts/engine-semantics.md §1.4), and takes the notice back once it can.
/// </summary>
/// <remarks>
/// <para>
/// Every problem that keeps one of a rule's trees from being evaluated is logged once per version
/// of its condition, by whichever of <see cref="Report"/> and <see cref="CheckAsync"/> sees the
/// version first. A body that cannot be evaluated skips the rule, and is sent as
/// <see cref="AlertPayloadKind.Unevaluable"/> for the version of the body
/// (<see cref="VersionOf(AlertRuleSnapshot)"/>). An auto-resolve tree or snooze conditions that
/// cannot be evaluated leave the rule firing, so with a body that can be they are sent as
/// <see cref="AlertPayloadKind.PartlyUnevaluable"/> for the version of the trees that cannot
/// (<see cref="PartsVersionOf"/>); with one that cannot, the body's notice stands for both. Since
/// such a rule still fires, its notice must wake no one: it is sent at
/// <see cref="AlertRuleSeverity.Info"/> and never through Do Not Disturb, whatever the rule's own
/// severity and <see cref="AlertRuleSnapshot.AllowThroughDnd"/> (<see cref="PartlyNoticeRule"/>). A rule
/// whose stored condition type, severity or scope class no engine member has (<see cref="UnreadableAlertRule"/>)
/// is skipped too, and is sent the same notice through its channels with no condition type, at
/// critical severity when the severity is the unreadable part.
/// </para>
/// <para>
/// Only <see cref="AlertSweepJob"/> sends, as the system, so that no upload waits on a webhook
/// before its rules are evaluated. The notice goes through the rule's own channels at the rule's
/// severity, behind the same Do Not Disturb gate as its excursions (<see cref="DndGatedDispatch"/>).
/// Its row (<see cref="IAlertRepository.RecordUnevaluableNoticeAsync"/>), not
/// <see cref="ConditionVersionLog"/>, decides whether it is sent: the row is recorded owing its
/// dispatch, which is settled once the dispatch returns, and each run reads the tenant's standing
/// notices and dispatches one still owed. So a run that fails, is cancelled or dies anywhere between
/// the row and the last send is made good by the next, which sends only the channels still owed
/// (<see cref="IAlertDeliveryService.DispatchAsync"/>), and a restart or a second instance does not
/// send it again. A channel whose provider fails is recorded failed and not retried, as for an
/// excursion. Two runs for one tenant are kept apart by the job ledger's lease; a run that loses
/// its lease while it dispatches can send a notice twice, the safe way to fail.
/// </para>
/// <para>
/// A standing notice is resolved through <see cref="IExcursionResolutionHandler"/>, as a closed
/// excursion is, when the rule's condition can be evaluated again, when the condition changes to
/// another that cannot, and when the rule is disabled or deleted (<see cref="ResolveAsync"/>).
/// Each run is also the backstop for those: it resolves every standing notice whose rule is not
/// enabled or no longer has that broken version, whatever path missed it (a disable that raced the
/// run, a retirement that failed), so a rule enabled again while still broken is told again.
/// </para>
/// </remarks>
public interface IUnevaluableRuleNotice
{
    /// <summary>
    /// Logs the problems with each rule's conditions the first time this process sees the version,
    /// and does nothing else: no read, no write, no send. For evaluation, which must not wait.
    /// </summary>
    void Report(IReadOnlyList<AlertRuleSnapshot> rules);

    /// <summary>
    /// Sends the notice for each rule in <paramref name="enabled"/> (every enabled rule of the
    /// tenant) with a tree that cannot be evaluated or that cannot be read at all, unless it was
    /// dispatched for this version, and resolves every other standing notice of the tenant. A data
    /// or provider failure (<see cref="ProviderFailure"/>) for one notice is logged and never stops
    /// the others; a defect reaches the caller.
    /// </summary>
    Task CheckAsync(EnabledAlertRules enabled, CancellationToken ct);

    /// <summary>
    /// Resolves the rule's standing notices, this one and <see cref="IHeldRuleNotice"/>'s, for a
    /// rule being disabled or deleted.
    /// </summary>
    Task ResolveAsync(Guid ruleId, ExcursionCloseReason reason, CancellationToken ct);
}

internal sealed class UnevaluableRuleNotice(
    IAlertRepository repository,
    IAlertRuleRepository ruleRepository,
    ITenantAccessor tenantAccessor,
    IAlertDeliveryService deliveryService,
    ISensorContextEnricher contextEnricher,
    IExcursionResolutionHandler resolutionHandler,
    ConditionVersionLog conditionLog,
    IAlertRuleConditionValidator conditionValidator,
    TimeProvider timeProvider,
    ILogger<UnevaluableRuleNotice> logger)
    : IUnevaluableRuleNotice
{
    private const string BodyScope = "condition";
    private const string AutoResolveScope = "auto_resolve";
    private const string SnoozeScope = "snooze";

    /// <summary>A half of <see cref="PartsVersionOf"/> for a tree that can be evaluated.</summary>
    private static readonly string NoPart = new('-', 31);

    /// <summary>The version of a rule's condition a notice is sent for.</summary>
    internal static string VersionOf(AlertRuleSnapshot rule) =>
        Hash($"{AlertConditionTypeNames.ToWireString(rule.ConditionType)}\n{rule.ConditionParams}");

    /// <summary>
    /// The version a <see cref="AlertPayloadKind.PartlyUnevaluable"/> notice is sent for:
    /// <c>r</c> and 31 hex digits of the auto-resolve tree, then <c>s</c> and 31 of the smart-snooze
    /// conditions, each hashed only when <paramref name="issues"/> has an issue in its scope and a
    /// run of <c>-</c> otherwise. So only an edit to a tree that cannot be evaluated changes it: not
    /// one to the body, to a tree that can be, or to the rest of the client configuration. Snooze
    /// conditions that cannot be read count as the whole client configuration. Neither letter is a
    /// hex digit, so it never equals a <see cref="VersionOf(AlertRuleSnapshot)"/>, and it is as long
    /// as one, which the column holds.
    /// </summary>
    internal static string PartsVersionOf(AlertRuleSnapshot rule, IReadOnlyList<RustValidationIssue> issues)
    {
        var autoResolve = issues.Any(i => i.Scope == AutoResolveScope)
            ? Hash($"auto_resolve\n{rule.AutoResolveParams}")[..31]
            : NoPart;
        var snooze = issues.Any(i => i.Scope == SnoozeScope)
            ? Hash($"snooze\n{SnoozeConditionsOf(rule.ClientConfiguration)}")[..31]
            : NoPart;
        return $"r{autoResolve}s{snooze}";
    }

    private static string SnoozeConditionsOf(string clientConfiguration)
    {
        var snooze = SmartSnoozeConfig.Parse(clientConfiguration);
        return snooze.ConditionsUnreadable ? clientConfiguration : snooze.Conditions?.GetRawText() ?? "";
    }

    /// <summary>
    /// Whether the notice standing for parts version <paramref name="standing"/> still holds for
    /// <paramref name="current"/>: every tree that cannot be evaluated now is one it was sent for,
    /// unchanged. So fixing one of two broken trees leaves the notice for the other standing rather
    /// than resolving and sending it again; a tree that newly cannot be evaluated sends it again.
    /// </summary>
    internal static bool PartsVersionCovers(string standing, string current)
    {
        if (standing.Length != current.Length || standing[0] != 'r' || standing[32] != 's')
            return false;
        return Holds(standing[1..32], current[1..32]) && Holds(standing[33..], current[33..]);

        static bool Holds(string standingHalf, string currentHalf) => currentHalf == NoPart || currentHalf == standingHalf;
    }

    /// <summary>
    /// The rule as its <see cref="AlertPayloadKind.PartlyUnevaluable"/> notice is sent: at the
    /// lowest severity and never through Do Not Disturb, under the rule's own scope class, so a Do
    /// Not Disturb window that would silence the rule's warnings silences it too.
    /// </summary>
    internal static NoticeRule PartlyNoticeRule(AlertRuleSnapshot rule) =>
        NoticeRule.Of(rule) with { Severity = AlertRuleSeverity.Info, AllowThroughDnd = false };

    /// <summary>The version of an unreadable rule a notice is sent for: whichever of its stored columns changes.</summary>
    internal static string VersionOf(UnreadableAlertRule rule) =>
        Hash($"unreadable\n{rule.StoredConditionType}\n{rule.ConditionParams}\n{rule.StoredSeverity}\n{rule.StoredScopeClass}");

    private static string Hash(string version) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version)));

    public void Report(IReadOnlyList<AlertRuleSnapshot> rules)
    {
        var tenantId = tenantAccessor.TenantId;
        foreach (var rule in rules)
            Issues(rule, tenantId);
    }

    public async Task CheckAsync(EnabledAlertRules enabled, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty) return;

        var standing = await repository.GetStandingUnevaluableNoticesAsync(null, ct);
        var enabledIds = enabled.Rules.Select(r => r.Id).Concat(enabled.Unreadable.Select(r => r.Id)).ToHashSet();

        foreach (var rule in enabled.Rules)
        {
            if (Issues(rule, tenantId) is not { } issues)
                continue;

            var (version, kind, noticeRule) = issues.Any(i => i.Scope == BodyScope)
                ? (VersionOf(rule), AlertPayloadKind.Unevaluable, NoticeRule.Of(rule))
                : issues.Count > 0
                    ? (PartsVersion(rule, issues, standing), AlertPayloadKind.PartlyUnevaluable, PartlyNoticeRule(rule))
                    : ((string?)null, AlertPayloadKind.Unevaluable, NoticeRule.Of(rule));
            await SendOnceAsync(
                noticeRule,
                version,
                kind,
                at => repository.RecordUnevaluableNoticeAsync(rule, version!, at, ct),
                standing, tenantId, ct);
        }

        foreach (var rule in enabled.Unreadable)
        {
            var version = VersionOf(rule);
            await SendOnceAsync(
                new NoticeRule(rule.Id, rule.Name, null, rule.Severity, rule.AllowThroughDnd, rule.ScopeClass),
                version,
                AlertPayloadKind.Unevaluable,
                at => repository.RecordUnreadableRuleNoticeAsync(rule, version, at, ct),
                standing, tenantId, ct);
        }

        foreach (var orphan in standing.Where(n => !enabledIds.Contains(n.AlertRuleId)))
            await TryResolveAsync(orphan, ExcursionCloseReason.RuleDisabled, ct);
    }

    /// <summary>
    /// The version of the rule's standing parts notice when it still holds
    /// (<see cref="PartsVersionCovers"/>), else the rule's current <see cref="PartsVersionOf"/>.
    /// </summary>
    private static string PartsVersion(
        AlertRuleSnapshot rule, IReadOnlyList<RustValidationIssue> issues, IReadOnlyList<StandingUnevaluableNotice> standing)
    {
        var current = PartsVersionOf(rule, issues);
        return standing.FirstOrDefault(n => n.AlertRuleId == rule.Id && PartsVersionCovers(n.ConditionVersion, current))
            ?.ConditionVersion ?? current;
    }

    /// <summary>
    /// Resolves the rule's standing notices for any other version, then sends the notice for
    /// <paramref name="version"/> unless it was dispatched; a null version is a rule that can be
    /// evaluated, which has no notice.
    /// </summary>
    private async Task SendOnceAsync(
        NoticeRule rule,
        string? version,
        AlertPayloadKind kind,
        Func<DateTime, Task<NoticeRecorded?>> record,
        IReadOnlyList<StandingUnevaluableNotice> standing,
        Guid tenantId,
        CancellationToken ct)
    {
        foreach (var stale in standing.Where(n => n.AlertRuleId == rule.Id && n.ConditionVersion != version))
            await TryResolveAsync(stale, ExcursionCloseReason.Manual, ct);

        if (version is null
            || standing.Any(n => n.AlertRuleId == rule.Id && n.ConditionVersion == version && n.Dispatched))
            return;

        try
        {
            await NotifyAsync(rule, kind, record, tenantId, ct);
        }
        catch (Exception ex) when (ProviderFailure.Is(ex))
        {
            logger.LogError(ex,
                "Sending the notice that {Part} of alert rule {AlertRuleId} for tenant {TenantId} cannot be evaluated failed",
                PartOf(kind), rule.Id, tenantId);
        }
    }

    public async Task ResolveAsync(Guid ruleId, ExcursionCloseReason reason, CancellationToken ct)
    {
        foreach (var notice in await repository.GetStandingUnevaluableNoticesAsync(ruleId, ct))
            await ResolveNoticeAsync(notice.ExcursionId, reason, ct);
        foreach (var notice in await repository.GetStandingHeldNoticesAsync(ruleId, ct))
            await ResolveNoticeAsync(notice.ExcursionId, reason, ct);
    }

    /// <summary>Resolves one notice, logging a failure so it never stops the rest of the run.</summary>
    private async Task TryResolveAsync(StandingUnevaluableNotice notice, ExcursionCloseReason reason, CancellationToken ct)
    {
        try
        {
            await ResolveNoticeAsync(notice.ExcursionId, reason, ct);
        }
        catch (Exception ex) when (ProviderFailure.Is(ex))
        {
            logger.LogError(ex,
                "Resolving notice {ExcursionId} that alert rule {AlertRuleId} cannot be evaluated failed",
                notice.ExcursionId, notice.AlertRuleId);
        }
    }

    /// <summary>
    /// The problems with the rule's trees at this version of its condition, logged the first time
    /// this process sees the version; null when the check failed.
    /// </summary>
    private IReadOnlyList<RustValidationIssue>? Issues(AlertRuleSnapshot rule, Guid tenantId)
    {
        IReadOnlyList<RustValidationIssue>? issues;
        bool validated;
        try
        {
            issues = conditionLog.IssuesFor(rule,
                () => conditionValidator.Validate(
                    rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled,
                    rule.AutoResolveParams, rule.ClientConfiguration),
                out validated);
        }
        catch (RustAlertEngineException ex)
        {
            logger.LogError(ex, "Checking the conditions of alert rule {AlertRuleId} for tenant {TenantId} failed",
                rule.Id, tenantId);
            return null;
        }

        if (validated && issues is { Count: > 0 })
        {
            logger.LogWarning(
                "Alert rule {AlertRuleId} for tenant {TenantId} has conditions that cannot be evaluated until it is edited: {Issues}",
                rule.Id,
                tenantId,
                string.Join("; ", issues.Select(i => $"{i.Scope} {i.Path}: {i.Reason}{(i.Field is null ? "" : $" ({i.Field})")}")));
        }
        return issues;
    }

    private async Task NotifyAsync(
        NoticeRule rule,
        AlertPayloadKind kind,
        Func<DateTime, Task<NoticeRecorded?>> record,
        Guid tenantId,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await record(now) is not { Dispatched: false } notice)
            return;

        await RuleNoticeDispatch.DispatchAsync(
            rule,
            notice,
            (tenant, activeExcursionCount) => AlertPayloads.Unevaluable(
                rule.Name, rule.ConditionType, rule.Severity, tenantId, notice.ExcursionId, notice.InstanceId,
                tenant, activeExcursionCount, now, kind),
            tenantId, repository, ruleRepository, deliveryService, contextEnricher, resolutionHandler, logger, ct);

        logger.LogInformation(
            "Notice {InstanceId} sent that {Part} of alert rule {AlertRuleId} cannot be evaluated until it is edited",
            notice.InstanceId, PartOf(kind), rule.Id);
    }

    /// <summary>What of the rule a notice of <paramref name="kind"/> says cannot be evaluated, for the log.</summary>
    private static string PartOf(AlertPayloadKind kind) =>
        kind == AlertPayloadKind.PartlyUnevaluable ? "the auto-resolve or smart-snooze conditions" : "the condition";

    private Task ResolveNoticeAsync(Guid excursionId, ExcursionCloseReason reason, CancellationToken ct) =>
        RuleNoticeDispatch.ResolveAsync(
            excursionId, reason, tenantAccessor.TenantId, repository, resolutionHandler, timeProvider, ct);
}
