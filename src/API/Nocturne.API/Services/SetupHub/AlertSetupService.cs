using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.SetupHub;

/// <summary>
/// The setup hub's Alerts item: four starter alert rules sent to one place, and a test alert whose
/// arrival the owner confirms. The rules are ordinary alert rules, evaluated and edited like any
/// other; <see cref="AlertRuleEntity.StarterKind"/> only lets this find them again.
/// </summary>
/// <remarks>
/// "This device" is an <c>in_app</c> channel addressed to the caller: it is the one channel routed
/// to a person, so a caregiver's alerts reach the caregiver, and the browser shows it as a
/// notification. A test confirmed received is what makes the item done
/// (<see cref="Items.AlertsItem"/>).
/// </remarks>
public class AlertSetupService(
    NocturneDbContext db,
    IRuleScopeClassifier scopeClassifier,
    ISecretEncryptionService encryption,
    IAlertDeliveryService delivery,
    AlertRuleRearm rearm,
    AlertRuleRetirement retirement)
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

    private readonly AlertRuleChannelWriter _channels = new(encryption);

    public async Task<AlertSetupStatus> GetAsync(Guid callerId, CancellationToken ct)
    {
        var routing = await RoutingAsync(ct);
        var units = await UnitsAsync(callerId, ct);
        var starters = await StartersAsync(tracking: false, ct);

        var rules = Enum.GetValues<StarterAlertKind>().Select(kind =>
        {
            var rule = starters.GetValueOrDefault(kind);
            var mgdl = rule is null ? Starters[kind].DefaultMgdl : ThresholdOf(rule);
            return new StarterAlertRule(
                kind, rule?.Id, rule?.IsEnabled ?? true,
                Starters[kind].Direction is null ? null : Display(mgdl, units));
        }).ToList();

        var primary = starters.Values.FirstOrDefault() is { } first
            ? PrimaryChannels(first, callerId).ToList()
            : [];
        var toThisDevice = starters.Count == 0
            || primary is [{ ChannelType: ChannelType.InApp } only] && only.Destination == callerId.ToString();

        var starterIds = starters.Values.Where(r => r.IsEnabled).Select(r => r.Id).ToList();
        var verified = await db.AlertInstances.AsNoTracking().AnyAsync(
            i => i.ReceiptConfirmedAt != null && starterIds.Contains(i.AlertExcursion!.AlertRuleId), ct);

        var members = routing == AlertRouting.ToYou
            ? await MembersAsync(callerId, starters.GetValueOrDefault(StarterAlertKind.UrgentLow), ct)
            : [];

        return new AlertSetupStatus(
            routing, units, starters.Count > 0, rules, toThisDevice,
            toThisDevice ? [] : primary.Select(ToResponse).ToList(),
            verified, members);
    }

    /// <summary>
    /// Creates the starter rules, or updates the ones already made, with the thresholds, switches
    /// and destination given. A threshold is read in the caller's units and stored in mg/dL; one
    /// that reads the same as the stored value keeps the stored value, so a round trip through
    /// mmol/L never moves it.
    /// </summary>
    /// <exception cref="ArgumentException">A threshold is missing or out of range, or a channel cannot deliver.</exception>
    /// <exception cref="InvalidOperationException">The onboarder is a helper, who leaves alerts to the recipient.</exception>
    public async Task<AlertSetupStatus> SaveAsync(Guid callerId, SaveAlertSetupRequest request, CancellationToken ct)
    {
        var routing = await RoutingAsync(ct);
        if (routing == AlertRouting.LeftForRecipient)
            throw new InvalidOperationException("The person this is handed over to chooses where alerts go.");
        if (request.Rules.GroupBy(r => r.Kind).Any(g => g.Count() > 1))
            throw new ArgumentException("Each starter rule may be given once.");

        var channelRequests = request.Channels
            ?? [new CreateAlertRuleChannelRequest { ChannelType = ChannelType.InApp, Destination = callerId.ToString() }];
        if (await _channels.ResolveAndValidateAsync(channelRequests, db, callerId, ct) is { } badChannel)
            throw new ArgumentException(badChannel);

        var units = await UnitsAsync(callerId, ct);
        var starters = await StartersAsync(tracking: true, ct);
        var tenantId = db.TenantId;
        var now = DateTime.UtcNow;
        var rearmed = new List<Guid>();
        var retired = new List<Guid>();

        foreach (var kind in Enum.GetValues<StarterAlertKind>())
        {
            var (name, severity, direction, defaultMgdl) = Starters[kind];
            var asked = request.Rules.FirstOrDefault(r => r.Kind == kind);
            var rule = starters.GetValueOrDefault(kind);

            var enabled = asked?.IsEnabled ?? rule?.IsEnabled ?? true;
            var stored = rule is null ? defaultMgdl : ThresholdOf(rule);
            var mgdl = direction is null || asked is null ? stored : ToMgdl(kind, asked.Threshold, stored, units);
            var conditionType = direction is null ? AlertConditionType.SignalLoss : AlertConditionType.Threshold;
            var conditionParams = direction is null
                ? JsonSerializer.Serialize(new { timeout_minutes = NoReadingsMinutes })
                : JsonSerializer.Serialize(new { direction, value = mgdl });

            if (rule is null)
            {
                rule = new AlertRuleEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Name = name,
                    ConditionType = conditionType,
                    ConditionParams = conditionParams,
                    ScopeClass = scopeClassifier.Classify(conditionType, conditionParams),
                    Severity = severity,
                    IsEnabled = enabled,
                    SortOrder = (int)kind,
                    StarterKind = kind,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.AlertRules.Add(rule);
            }
            else
            {
                if (rule.IsEnabled != enabled || !ConditionTreeEquality.Same(rule.ConditionParams, conditionParams))
                    rearmed.Add(rule.Id);
                if (rule.IsEnabled && !enabled)
                    retired.Add(rule.Id);
                rule.ConditionParams = conditionParams;
                rule.ScopeClass = scopeClassifier.Classify(rule.ConditionType, conditionParams);
                rule.IsEnabled = enabled;
                rule.UpdatedAt = now;
            }

            if (routing == AlertRouting.ToYouAsCaregiver)
                rule.AllowThroughDnd = true;

            ReplacePrimaryChannels(rule, callerId, channelRequests);
        }

        if (rearmed.Count > 0)
            await rearm.SaveAndClearAsync(db, rearmed, ct);
        else
            await db.SaveChangesAsync(ct);
        if (retired.Count > 0)
            await retirement.CloseAsync(retired, tenantId, CancellationToken.None);

        return await GetAsync(callerId, ct);
    }

    /// <summary>
    /// Sends a test alert through the first enabled starter rule's destination, the same way a
    /// real alert is sent, and not to anyone added for urgent lows only.
    /// </summary>
    /// <exception cref="InvalidOperationException">No starter rule is enabled, or the onboarder is a helper.</exception>
    public async Task<AlertSetupTest> SendTestAsync(Guid callerId, CancellationToken ct)
    {
        if (await RoutingAsync(ct) == AlertRouting.LeftForRecipient)
            throw new InvalidOperationException("The person this is handed over to chooses where alerts go.");

        var starters = await StartersAsync(tracking: false, ct);
        var rule = starters.Values.FirstOrDefault(r => r.IsEnabled)
            ?? throw new InvalidOperationException("No starter alert is switched on.");

        var channels = PrimaryChannels(rule, callerId)
            .Select(c => new AlertRuleChannelSnapshot(
                c.Id, c.AlertRuleId, c.ChannelType, c.Destination, c.DestinationLabel, c.SortOrder, c.Metadata, c.Secret))
            .ToList();
        var instanceId = await delivery.TestFireAsync(
            rule.Id, channels, AlertRulesController.BuildTestPayload(rule, db.TenantId), ct);

        return await GetTestAsync(instanceId, ct);
    }

    /// <exception cref="KeyNotFoundException">No test of a starter rule has this id.</exception>
    public async Task<AlertSetupTest> GetTestAsync(Guid instanceId, CancellationToken ct)
    {
        var name = await StarterTests()
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
    /// Records that the test alert arrived. Only a test Nocturne did send counts: one whose every
    /// delivery failed cannot have arrived, whatever the answer.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No test of a starter rule has this id.</exception>
    /// <exception cref="InvalidOperationException">Every delivery of the test failed, or the onboarder is a helper.</exception>
    public async Task<AlertSetupStatus> ConfirmReceivedAsync(Guid callerId, Guid instanceId, CancellationToken ct)
    {
        if (await RoutingAsync(ct) == AlertRouting.LeftForRecipient)
            throw new InvalidOperationException("The person this is handed over to chooses where alerts go.");

        var instance = await db.AlertInstances
            .Where(i => i.IsTest && i.AlertExcursion!.AlertRule!.StarterKind != null)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct)
            ?? throw new KeyNotFoundException();

        var sent = await db.AlertDeliveries.AnyAsync(d => d.AlertInstanceId == instanceId && d.Status != "failed", ct);
        if (!sent)
            throw new InvalidOperationException("Every delivery of this test failed.");

        instance.ReceiptConfirmedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(callerId, ct);
    }

    /// <summary>Adds a member to, or takes them off, the urgent low rule, on their own account.</summary>
    /// <exception cref="KeyNotFoundException">The subject is not another member of this tenant.</exception>
    /// <exception cref="InvalidOperationException">The starter rules are not saved yet, or the onboarder is not the patient.</exception>
    public async Task<AlertSetupStatus> SetUrgentLowRecipientAsync(
        Guid callerId, Guid memberId, bool alerted, CancellationToken ct)
    {
        if (await RoutingAsync(ct) != AlertRouting.ToYou)
            throw new InvalidOperationException("Only a patient setting up their own alerts adds someone to urgent lows.");
        if (!(await MembersAsync(callerId, null, ct)).Any(m => m.SubjectId == memberId))
            throw new KeyNotFoundException();

        var starters = await StartersAsync(tracking: true, ct);
        var urgentLow = starters.GetValueOrDefault(StarterAlertKind.UrgentLow)
            ?? throw new InvalidOperationException("The starter rules are not saved yet.");

        var destination = memberId.ToString();
        var existing = urgentLow.Channels.FirstOrDefault(c => c.ChannelType == ChannelType.InApp && c.Destination == destination);
        if (alerted && existing is null)
        {
            urgentLow.Channels.Add(new AlertRuleChannelEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = db.TenantId,
                AlertRuleId = urgentLow.Id,
                ChannelType = ChannelType.InApp,
                Destination = destination,
                SortOrder = urgentLow.Channels.Count,
                CreatedAt = DateTime.UtcNow,
            });
        }
        else if (!alerted && existing is not null)
        {
            db.AlertRuleChannels.Remove(existing);
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(callerId, ct);
    }

    private IQueryable<AlertInstanceEntity> StarterTests() =>
        db.AlertInstances.AsNoTracking().Where(i => i.IsTest && i.AlertExcursion!.AlertRule!.StarterKind != null);

    private async Task<Dictionary<StarterAlertKind, AlertRuleEntity>> StartersAsync(bool tracking, CancellationToken ct)
    {
        var query = db.AlertRules.Include(r => r.Channels).Where(r => r.StarterKind != null);
        var rules = await (tracking ? query : query.AsNoTracking()).ToListAsync(ct);
        return rules
            .GroupBy(r => r.StarterKind!.Value)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CreatedAt).First());
    }

    private async Task<AlertRouting> RoutingAsync(CancellationToken ct) =>
        await db.Tenants.AsNoTracking()
                .Where(t => t.Id == db.TenantId)
                .Select(t => t.PatientRelationship)
                .FirstOrDefaultAsync(ct) switch
            {
                PatientRelationship.Helper => AlertRouting.LeftForRecipient,
                PatientRelationship.Caregiver => AlertRouting.ToYouAsCaregiver,
                _ => AlertRouting.ToYou,
            };

    private async Task<string> UnitsAsync(Guid callerId, CancellationToken ct)
    {
        var preferences = await db.Subjects.AsNoTracking()
            .Where(s => s.Id == callerId)
            .Select(s => s.Preferences)
            .FirstOrDefaultAsync(ct);
        var tenantUnits = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == db.TenantId)
            .Select(t => t.DefaultGlucoseUnits)
            .FirstOrDefaultAsync(ct);
        return UserDisplayPreferences.Deserialize(preferences).GlucoseUnits ?? tenantUnits ?? GlucoseUnitDefaults.MgDl;
    }

    private async Task<List<AlertSetupMember>> MembersAsync(Guid callerId, AlertRuleEntity? urgentLow, CancellationToken ct)
    {
        var alerted = urgentLow?.Channels
            .Where(c => c.ChannelType == ChannelType.InApp)
            .Select(c => c.Destination)
            .ToHashSet() ?? [];

        var members = await db.TenantMembers.AsNoTracking()
            .Where(m => m.TenantId == db.TenantId && m.SubjectId != callerId
                        && !m.Subject!.IsSystemSubject && m.Subject.IsActive)
            .OrderBy(m => m.SysCreatedAt)
            .Select(m => new { m.SubjectId, Name = m.Label ?? m.Subject!.Name })
            .ToListAsync(ct);

        return members
            .Select(m => new AlertSetupMember(m.SubjectId, m.Name, alerted.Contains(m.SubjectId.ToString())))
            .ToList();
    }

    /// <summary>
    /// The rule's channels other than a member added for urgent lows: every channel but an
    /// <c>in_app</c> one addressed to someone other than the caller.
    /// </summary>
    private static IEnumerable<AlertRuleChannelEntity> PrimaryChannels(AlertRuleEntity rule, Guid callerId) =>
        rule.Channels
            .Where(c => !IsMemberChannel(c, callerId))
            .OrderBy(c => c.SortOrder);

    private static bool IsMemberChannel(AlertRuleChannelEntity channel, Guid callerId) =>
        channel.ChannelType == ChannelType.InApp
        && Guid.TryParse(channel.Destination, out var subjectId)
        && subjectId != callerId;

    private void ReplacePrimaryChannels(
        AlertRuleEntity rule, Guid callerId, List<CreateAlertRuleChannelRequest> requests)
    {
        var retained = AlertRuleChannelWriter.CollectRetainedSecrets(rule.Channels);
        var members = rule.Channels.Where(c => IsMemberChannel(c, callerId)).ToList();
        foreach (var channel in rule.Channels.Except(members).ToList())
        {
            rule.Channels.Remove(channel);
            db.AlertRuleChannels.Remove(channel);
        }

        var sortOrder = 0;
        foreach (var request in requests)
            rule.Channels.Add(_channels.Build(request, rule.Id, rule.TenantId, sortOrder++, retained));
        foreach (var member in members)
            member.SortOrder = sortOrder++;
    }

    private static decimal ThresholdOf(AlertRuleEntity rule)
    {
        using var json = JsonDocument.Parse(rule.ConditionParams);
        return json.RootElement.TryGetProperty("value", out var value) && value.TryGetDecimal(out var mgdl)
            ? mgdl
            : Starters[rule.StarterKind!.Value].DefaultMgdl;
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

    private static AlertRuleChannelResponse ToResponse(AlertRuleChannelEntity c) => new()
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
