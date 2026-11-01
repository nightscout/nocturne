using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;

namespace Nocturne.API.Services.SetupHub;

/// <summary>
/// The setup hub's Alerts item: four starter alert rules sent to one place, and a test alert whose
/// arrival the owner confirms. The rules are ordinary alert rules, evaluated and edited like any
/// other; their <see cref="StarterAlertKind"/> only lets this find them again.
/// </summary>
/// <remarks>
/// "This device" is an <c>in_app</c> channel addressed to the caller: it is the one channel routed
/// to a person, and an open, signed-in Nocturne page raises it as a system notification. It shows
/// nothing while Nocturne is closed, so a caregiver needs a channel that delivers without it, and a
/// patient who keeps only this device confirms they understand that
/// (<see cref="ConfirmReceivedAsync"/>). A test confirmed received is what makes the item done
/// (<see cref="AlertDeliveryCheck"/>).
/// </remarks>
public class AlertSetupService(
    INocturneDbContextFactory contexts,
    ITenantRepository tenants,
    ITenantGlucoseUnits tenantUnits,
    ITenantMembershipRepository members,
    ISubjectRepository subjects,
    IAlertRuleRepository rules,
    IRuleScopeClassifier scopeClassifier,
    ISecretEncryptionService encryption,
    IAlertDeliveryService delivery,
    AlertRuleRearm rearm,
    AlertRuleRetirement retirement,
    TimeProvider time)
{
    private const decimal MinThresholdMgdl = 40;
    private const decimal MaxThresholdMgdl = 400;
    private const int NoReadingsMinutes = 20;

    private static readonly IReadOnlyDictionary<StarterAlertKind, (string Name, AlertRuleSeverity Severity, string? Direction, decimal DefaultMgdl)> Starters =
        new Dictionary<StarterAlertKind, (string, AlertRuleSeverity, string?, decimal)>
        {
            [StarterAlertKind.UrgentLow] = ("Urgent low", AlertRuleSeverity.Critical, "below", 55),
            [StarterAlertKind.Low] = ("Low", AlertRuleSeverity.Warning, "below", 70),
            [StarterAlertKind.High] = ("High", AlertRuleSeverity.Warning, "above", 250),
            [StarterAlertKind.NoReadings] = ("No readings for 20 minutes", AlertRuleSeverity.Warning, null, 0),
        };

    /// <summary>What can be chosen instead of this device.</summary>
    private static readonly IReadOnlyList<ChannelType> ChannelTypesOffered = ChannelDestinations.Offered
        .Where(AlertDeliveryCheck.DeliversWhileClosed)
        .ToList();

    private readonly AlertRuleChannelWriter _channels = new(encryption);

    public async Task<AlertSetupStatus> GetAsync(Guid callerId, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var routing = await RoutingAsync(db, ct);
        var units = await UnitsAsync(callerId, ct);
        var starters = await rules.GetStarterRulesAsync(ct);

        var startingPoints = StartingPoints(starters.Count == 0 ? await MigratedAlarmSettingsAsync(db, ct) : null);
        var starterRules = Enum.GetValues<StarterAlertKind>().Select(kind =>
        {
            var rule = starters.GetValueOrDefault(kind);
            var start = startingPoints[kind];
            var mgdl = rule is null ? start.Mgdl : ThresholdOf(kind, rule);
            return new StarterAlertRule(
                kind, rule?.Rule.Id, rule?.IsEnabled ?? start.IsEnabled,
                Starters[kind].Direction is null ? null : Display(mgdl, units));
        }).ToList();
        var thresholdsFrom = startingPoints.Values.Any(p => p.FromNightscout)
            ? AlertThresholdSource.Nightscout
            : AlertThresholdSource.Defaults;

        var primary = starters.Values.FirstOrDefault() is { } first
            ? PrimaryChannels(first, callerId).ToList()
            : [];
        var toThisDevice = starters.Count == 0
            ? routing != AlertRouting.ToYouAsCaregiver
            : primary is [{ ChannelType: ChannelType.InApp } only] && only.Destination == callerId.ToString();

        var members = routing == AlertRouting.ToYou
            ? await MembersAsync(db, callerId, starters.GetValueOrDefault(StarterAlertKind.UrgentLow), ct)
            : [];

        return new AlertSetupStatus(
            routing, units, starters.Count > 0, starterRules, thresholdsFrom, toThisDevice,
            toThisDevice ? [] : primary.Select(ToResponse).ToList(),
            ChannelTypesOffered,
            primary.Any(c => AlertDeliveryCheck.DeliversWhileClosed(c.ChannelType)),
            routing == AlertRouting.ToYouAsCaregiver,
            await AlertDeliveryCheck.VerifiedAsync(db, tenants, rules, ct),
            members);
    }

    /// <summary>
    /// Creates the starter rules, or updates the ones already made, with the thresholds, switches
    /// and destination given. A threshold is read in the caller's units and stored in mg/dL; one
    /// that reads the same as the stored value keeps the stored value, so a round trip through
    /// mmol/L never moves it.
    /// </summary>
    /// <remarks>
    /// Urgent low always sounds through Do Not Disturb, and every rule does for a caregiver. Two
    /// saves racing to create the rules meet at the one-per-kind index; the loser saves again over
    /// the winner's rules.
    /// </remarks>
    /// <exception cref="ArgumentException">A threshold is missing or out of range, or a channel cannot deliver.</exception>
    /// <exception cref="InvalidOperationException">The onboarder is a helper, who leaves alerts to the recipient.</exception>
    public async Task<AlertSetupStatus> SaveAsync(Guid callerId, SaveAlertSetupRequest request, CancellationToken ct)
    {
        try
        {
            return await SaveOnceAsync(callerId, request, ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return await SaveOnceAsync(callerId, request, ct);
        }
    }

    private async Task<AlertSetupStatus> SaveOnceAsync(Guid callerId, SaveAlertSetupRequest request, CancellationToken ct)
    {
        await using (var unit = await DbUnitOfWork.OpenAsync(contexts, ct))
            await SaveStartersAsync(unit, callerId, request, ct);

        return await GetAsync(callerId, ct);
    }

    private async Task SaveStartersAsync(
        DbUnitOfWork unit, Guid callerId, SaveAlertSetupRequest request, CancellationToken ct)
    {
        var db = unit.Context;
        var routing = await RoutingAsync(db, ct);
        if (routing == AlertRouting.LeftForRecipient)
            throw new InvalidOperationException("The person this is handed over to chooses where alerts go.");
        if (request.Rules.GroupBy(r => r.Kind).Any(g => g.Count() > 1))
            throw new ArgumentException("Each starter rule may be given once.");

        var channelRequests = ChannelsFor(callerId, request.Channels);
        if (await _channels.ResolveAndValidateAsync(
            channelRequests, AlertRuleChannelWriter.NoStoredRecipients, db, members, callerId, ct) is { } badChannel)
            throw new ArgumentException(badChannel);

        var units = await UnitsAsync(callerId, ct);
        var writer = new AlertRuleRepository(unit, time);
        var starters = await writer.GetStarterRulesAsync(ct);
        var tenantId = db.TenantId;
        var channelWrites = _channels.ToWrites(channelRequests);
        var rearmed = new List<Guid>();
        var retired = new List<Guid>();

        foreach (var kind in Enum.GetValues<StarterAlertKind>())
        {
            var (name, severity, direction, defaultMgdl) = Starters[kind];
            var asked = request.Rules.FirstOrDefault(r => r.Kind == kind);
            var rule = starters.GetValueOrDefault(kind);

            var enabled = asked?.IsEnabled ?? rule?.IsEnabled ?? true;
            var stored = rule is null ? defaultMgdl : ThresholdOf(kind, rule);
            var mgdl = direction is null || asked is null || (asked.Threshold is null && !enabled)
                ? stored
                : ToMgdl(kind, asked.Threshold, stored, units);
            var conditionType = direction is null ? AlertConditionType.SignalLoss : AlertConditionType.Threshold;
            var conditionParams = direction is null
                ? JsonSerializer.Serialize(new { timeout_minutes = NoReadingsMinutes })
                : JsonSerializer.Serialize(new { direction, value = mgdl });

            var throughDnd = kind == StarterAlertKind.UrgentLow || routing == AlertRouting.ToYouAsCaregiver;
            if (rule is null)
            {
                await writer.CreateAsync(
                    new NewAlertRule(
                        new AlertRuleWrite(
                            name, null, conditionType, conditionParams,
                            scopeClassifier.Classify(conditionType, conditionParams), severity,
                            IsEnabled: enabled, SortOrder: (int)kind, AllowThroughDnd: throughDnd,
                            AutoResolveEnabled: false, AutoResolveParams: null, ClientConfiguration: "{}"),
                        channelWrites,
                        StarterKind: kind),
                    ct);
                continue;
            }

            var ruleId = rule.Rule.Id;
            if (rule.IsEnabled != enabled || !ConditionTreeEquality.Same(rule.Rule.ConditionParams, conditionParams))
                rearmed.Add(ruleId);
            if (rule.IsEnabled && !enabled)
                retired.Add(ruleId);
            await writer.UpdateAsync(ruleId, AlertRuleWrite.Of(rule) with
            {
                ConditionParams = conditionParams,
                ScopeClass = scopeClassifier.Classify(rule.Rule.ConditionType, conditionParams),
                IsEnabled = enabled,
                AllowThroughDnd = rule.Rule.AllowThroughDnd || throughDnd,
            }, ct);
            await writer.ReplaceChannelsAsync(ruleId, channelWrites, kept: c => IsMemberChannel(c, callerId), ct);
        }

        if (rearmed.Count > 0)
            await rearm.SaveAndClearAsync(unit, rearmed, ct);
        else
            await db.SaveChangesAsync(ct);
        if (retired.Count > 0)
            await retirement.CloseAsync(retired, tenantId, CancellationToken.None);
    }

    /// <summary>
    /// Sends a test alert through the first enabled starter rule's destination, the same way a
    /// real alert is sent, and not to anyone added for urgent lows only.
    /// </summary>
    /// <exception cref="InvalidOperationException">No starter rule is enabled, or the onboarder is a helper.</exception>
    public async Task<AlertSetupTest> SendTestAsync(Guid callerId, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        if (await RoutingAsync(db, ct) == AlertRouting.LeftForRecipient)
            throw new InvalidOperationException("The person this is handed over to chooses where alerts go.");

        var starters = await rules.GetStarterRulesAsync(ct);
        var rule = starters.Values.FirstOrDefault(r => r.IsEnabled)
            ?? throw new InvalidOperationException("No starter alert is switched on.");

        var channels = PrimaryChannels(rule, callerId).ToList();
        var instanceId = await delivery.TestFireAsync(
            rule.Rule.Id, channels, AlertRulesController.BuildTestPayload(rule.Rule, db.TenantId), ct);

        return await GetTestAsync(instanceId, ct);
    }

    /// <exception cref="KeyNotFoundException">No test of a starter rule has this id.</exception>
    public async Task<AlertSetupTest> GetTestAsync(Guid instanceId, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var name = await StarterTests(db)
            .Where(i => i.Id == instanceId)
            .Select(i => i.AlertExcursion!.AlertRule!.Name)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException();

        var deliveries = await db.AlertDeliveries.AsNoTracking()
            .Where(d => d.AlertInstanceId == instanceId)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new AlertSetupDelivery(d.ChannelType, d.Status, d.LastError))
            .ToListAsync(ct);

        return new AlertSetupTest(instanceId, name, deliveries);
    }

    /// <summary>
    /// Records that the test alert arrived. Only a test Nocturne sent to the rule's channels as they
    /// are now counts (<see cref="AlertDeliveryCheck.Matches"/>). A caregiver's must have reached a
    /// channel that delivers while Nocturne is closed; anyone else's that reached none needs
    /// <paramref name="acknowledgedOpenPageOnly"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No test of a starter rule has this id.</exception>
    /// <exception cref="InvalidOperationException">The test cannot show that alerts reach the caller, or the onboarder is a helper.</exception>
    public async Task<AlertSetupStatus> ConfirmReceivedAsync(
        Guid callerId, Guid instanceId, bool acknowledgedOpenPageOnly, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var routing = await RoutingAsync(db, ct);
        if (routing == AlertRouting.LeftForRecipient)
            throw new InvalidOperationException("The person this is handed over to chooses where alerts go.");

        var instance = await db.AlertInstances
            .Include(i => i.AlertExcursion)
            .Where(i => i.IsTest && i.AlertExcursion!.AlertRule!.StarterKind != null)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct)
            ?? throw new KeyNotFoundException();

        var sent = await AlertDeliveryCheck.SentToAsync(db, instanceId, ct);
        var ruleId = instance.AlertExcursion!.AlertRuleId;
        var current = (await rules.GetChannelsAsync([ruleId], ct)).GetValueOrDefault(ruleId) ?? [];
        if (!AlertDeliveryCheck.Matches(sent, current)
            || !(await AlertDeliveryCheck.EnabledStarterChannelsAsync(rules, ct)).All(rule => AlertDeliveryCheck.Matches(sent, rule)))
            throw new InvalidOperationException("This test did not reach every place alerts now go. Send another.");

        var whileClosed = sent.Any(s => AlertDeliveryCheck.DeliversWhileClosed(s.Type));
        if (!whileClosed && routing == AlertRouting.ToYouAsCaregiver)
            throw new InvalidOperationException("A caregiver's alerts need a destination that works while Nocturne is closed.");
        if (!whileClosed && !acknowledgedOpenPageOnly)
            throw new InvalidOperationException("Alerts on this device show only while Nocturne is open, which needs acknowledging.");

        instance.ReceiptConfirmedAt ??= time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return await GetAsync(callerId, ct);
    }

    /// <summary>Adds a member to, or takes them off, the urgent low rule, on their own account.</summary>
    /// <exception cref="KeyNotFoundException">The subject is not another member of this tenant.</exception>
    /// <exception cref="InvalidOperationException">The starter rules are not saved yet, or the onboarder is not the patient.</exception>
    public async Task<AlertSetupStatus> SetUrgentLowRecipientAsync(
        Guid callerId, Guid memberId, bool alerted, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        if (await RoutingAsync(db, ct) != AlertRouting.ToYou)
            throw new InvalidOperationException("Only a patient setting up their own alerts adds someone to urgent lows.");
        if (!(await MembersAsync(db, callerId, null, ct)).Any(m => m.SubjectId == memberId))
            throw new KeyNotFoundException();

        var starters = await rules.GetStarterRulesAsync(ct);
        var urgentLow = starters.GetValueOrDefault(StarterAlertKind.UrgentLow)
            ?? throw new InvalidOperationException("The starter rules are not saved yet.");

        var destination = memberId.ToString();
        var existing = urgentLow.Channels.FirstOrDefault(c => c.ChannelType == ChannelType.InApp && c.Destination == destination);
        if (alerted && existing is null)
            await rules.AddChannelAsync(urgentLow.Rule.Id, new AlertRuleChannelWrite(ChannelType.InApp, destination), ct);
        else if (!alerted && existing is not null)
            await rules.RemoveChannelAsync(urgentLow.Rule.Id, existing.Id, ct);

        return await GetAsync(callerId, ct);
    }

    /// <summary>
    /// The channels a save asks for. None asks for this device. <c>web_push</c> reaches no browser,
    /// and an <c>in_app</c> channel reaches the caller or no one.
    /// </summary>
    private static List<CreateAlertRuleChannelRequest> ChannelsFor(Guid callerId, List<CreateAlertRuleChannelRequest>? requested)
    {
        if (requested is null)
            return [new CreateAlertRuleChannelRequest { ChannelType = ChannelType.InApp, Destination = callerId.ToString() }];
        if (requested.Count == 0)
            throw new ArgumentException("Choose where alerts go.");

        foreach (var channel in requested)
        {
            if (channel.ChannelType == ChannelType.WebPush)
                throw new ArgumentException("Browser push reaches no browser yet. Choose another destination.");
            if (channel.ChannelType == ChannelType.DeviceAction)
                throw new ArgumentException("A device alert is set up in the rule builder. Choose another destination.");
            if (channel.ChannelType != ChannelType.InApp)
                continue;
            if (string.IsNullOrWhiteSpace(channel.Destination))
                channel.Destination = callerId.ToString();
            else if (channel.Destination != callerId.ToString())
                throw new ArgumentException("An in-app alert set up here goes to you.");
        }

        return requested;
    }

    private static IQueryable<AlertInstanceEntity> StarterTests(NocturneDbContext db) =>
        db.AlertInstances.AsNoTracking().Where(i => i.IsTest && i.AlertExcursion!.AlertRule!.StarterKind != null);

    /// <summary>
    /// The alarm settings of the tenant's newest completed migration that read any. Only the run
    /// reads the source, so a setup page never waits on the site it came from.
    /// </summary>
    private static async Task<NightscoutAlarmSettings?> MigratedAlarmSettingsAsync(NocturneDbContext db, CancellationToken ct)
    {
        var json = await db.MigrationRuns.AsNoTracking()
            .Where(r => r.TenantId == db.TenantId
                        && r.State == nameof(MigrationJobState.Completed)
                        && r.SourceAlarmSettings != null)
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => r.SourceAlarmSettings)
            .FirstOrDefaultAsync(ct);
        return json is null ? null : NightscoutAlarmSettings.FromJson(json);
    }

    /// <param name="Mgdl">The threshold; 0 for a kind with none.</param>
    /// <param name="FromNightscout">Whether the threshold or the switch was read from the settings.</param>
    internal record StartingPoint(decimal Mgdl, bool IsEnabled, bool FromNightscout);

    /// <summary>
    /// What each starter rule shows before it is saved: the settings' threshold and switch when it
    /// states them, else the rule's default. A threshold outside what a rule accepts is not used.
    /// </summary>
    internal static IReadOnlyDictionary<StarterAlertKind, StartingPoint> StartingPoints(NightscoutAlarmSettings? settings)
    {
        StartingPoint Of(StarterAlertKind kind, decimal? threshold, bool? enabled)
        {
            var inRange = threshold is >= MinThresholdMgdl and <= MaxThresholdMgdl;
            return new StartingPoint(
                inRange ? threshold!.Value : Starters[kind].DefaultMgdl,
                enabled ?? true,
                inRange || enabled is not null);
        }

        return new Dictionary<StarterAlertKind, StartingPoint>
        {
            [StarterAlertKind.UrgentLow] = Of(StarterAlertKind.UrgentLow, settings?.BgLow, settings?.AlarmUrgentLow),
            [StarterAlertKind.Low] = Of(StarterAlertKind.Low, settings?.BgTargetBottom, settings?.AlarmLow),
            [StarterAlertKind.High] = Of(StarterAlertKind.High, settings?.BgTargetTop, settings?.AlarmHigh),
            [StarterAlertKind.NoReadings] = Of(StarterAlertKind.NoReadings, null, settings?.AlarmTimeagoWarn),
        };
    }

    private async Task<AlertRouting> RoutingAsync(NocturneDbContext db, CancellationToken ct) =>
        (await tenants.GetAsync(db.TenantId, ct))?.PatientRelationship switch
            {
                PatientRelationship.Helper => AlertRouting.LeftForRecipient,
                PatientRelationship.Caregiver => AlertRouting.ToYouAsCaregiver,
                _ => AlertRouting.ToYou,
            };

    private async Task<string> UnitsAsync(Guid callerId, CancellationToken ct)
    {
        var preferences = (await subjects.GetAsync(callerId, ct))?.Preferences;
        return UserDisplayPreferences.Deserialize(preferences).GlucoseUnits ?? await tenantUnits.ResolveAsync(ct);
    }

    private async Task<List<AlertSetupMember>> MembersAsync(
        NocturneDbContext db, Guid callerId, AlertRuleRecord? urgentLow, CancellationToken ct)
    {
        var alerted = urgentLow?.Channels
            .Where(c => c.ChannelType == ChannelType.InApp)
            .Select(c => c.Destination)
            .ToHashSet() ?? [];

        var people = await members.ListPeopleAsync(db.TenantId, ct);

        return people
            .Where(m => m.SubjectId != callerId)
            .Select(m => new AlertSetupMember(m.SubjectId, m.Label ?? m.SubjectName ?? string.Empty, alerted.Contains(m.SubjectId.ToString())))
            .ToList();
    }

    /// <summary>
    /// The rule's channels other than a member added for urgent lows: every channel but an
    /// <c>in_app</c> one addressed to someone other than the caller.
    /// </summary>
    private static IEnumerable<AlertRuleChannelSnapshot> PrimaryChannels(AlertRuleRecord rule, Guid callerId) =>
        rule.Channels
            .Where(c => !IsMemberChannel(c, callerId))
            .OrderBy(c => c.SortOrder);

    private static bool IsMemberChannel(AlertRuleChannelSnapshot channel, Guid callerId) =>
        channel.ChannelType == ChannelType.InApp
        && Guid.TryParse(channel.Destination, out var subjectId)
        && subjectId != callerId;

    private static decimal ThresholdOf(StarterAlertKind kind, AlertRuleRecord rule)
    {
        using var json = JsonDocument.Parse(rule.Rule.ConditionParams);
        return json.RootElement.TryGetProperty("value", out var value) && value.TryGetDecimal(out var mgdl)
            ? mgdl
            : Starters[kind].DefaultMgdl;
    }

    private static decimal Display(decimal mgdl, string units) =>
        units == GlucoseUnitDefaults.Mmol
            ? Math.Round(mgdl / (decimal)GlucoseConstants.MgdlPerMmol, 1, MidpointRounding.AwayFromZero)
            : Math.Round(mgdl, 0, MidpointRounding.AwayFromZero);

    private static decimal ToMgdl(StarterAlertKind kind, decimal? threshold, decimal stored, string units)
    {
        if (threshold is not { } value)
            throw new ArgumentException($"The {Starters[kind].Name} alert needs a threshold.");
        if (value == Display(stored, units))
            return stored;

        var mgdl = units == GlucoseUnitDefaults.Mmol
            ? Math.Round(value * (decimal)GlucoseConstants.MgdlPerMmol, 0, MidpointRounding.AwayFromZero)
            : Math.Round(value, 0, MidpointRounding.AwayFromZero);
        return mgdl is >= MinThresholdMgdl and <= MaxThresholdMgdl
            ? mgdl
            : throw new ArgumentException(
                $"The {Starters[kind].Name} alert's threshold must be between "
                + $"{Display(MinThresholdMgdl, units)} and {Display(MaxThresholdMgdl, units)}.");
    }

    private static AlertRuleChannelResponse ToResponse(AlertRuleChannelSnapshot c) => new()
    {
        Id = c.Id,
        ChannelType = c.ChannelType,
        Destination = c.Destination,
        DestinationLabel = c.DestinationLabel,
        SortOrder = c.SortOrder,
        Metadata = c.Metadata is null ? null : JsonSerializer.Deserialize<JsonElement>(c.Metadata),
        HasSecret = c.Secret is not null,
    };
}
