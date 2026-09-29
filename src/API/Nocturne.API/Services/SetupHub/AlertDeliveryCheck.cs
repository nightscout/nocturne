using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.SetupHub;

/// <summary>
/// Whether a tenant's alerts are known to reach someone: an enabled rule of its own whose test alert
/// the owner confirmed arrived, sent to the channels the rule has now. A caregiver's test must also
/// have gone to a channel that delivers while Nocturne is closed.
/// </summary>
public static class AlertDeliveryCheck
{
    /// <summary>
    /// Channel types that reach someone with no Nocturne page open. <c>in_app</c> shows only in an
    /// open, signed-in page, and <c>web_push</c> reaches no browser at all.
    /// </summary>
    public static bool DeliversWhileClosed(ChannelType type) =>
        type is not (ChannelType.InApp or ChannelType.WebPush);

    public static async Task<bool> VerifiedAsync(NocturneDbContext db, CancellationToken ct)
    {
        var tests = await db.AlertInstances.AsNoTracking()
            .Where(i => i.IsTest
                        && i.ReceiptConfirmedAt != null
                        && i.AlertExcursion!.AlertRule!.IsEnabled
                        && i.AlertExcursion.AlertRule.ManagedBy == null)
            .Select(i => new { i.Id, i.AlertExcursion!.AlertRuleId })
            .ToListAsync(ct);
        if (tests.Count == 0)
            return false;

        var caregiver = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == db.TenantId)
            .Select(t => t.PatientRelationship)
            .FirstOrDefaultAsync(ct) == PatientRelationship.Caregiver;

        var ruleIds = tests.Select(t => t.AlertRuleId).Distinct().ToList();
        var channels = (await db.AlertRuleChannels.AsNoTracking()
                .Where(c => ruleIds.Contains(c.AlertRuleId))
                .ToListAsync(ct))
            .ToLookup(c => c.AlertRuleId);

        foreach (var test in tests)
        {
            var sent = await SentToAsync(db, test.Id, ct);
            if (Matches(sent, channels[test.AlertRuleId]) && (!caregiver || sent.Any(s => DeliversWhileClosed(s.Type))))
                return true;
        }

        return false;
    }

    /// <summary>The rule channels a test was sent to and did not fail on.</summary>
    public static async Task<HashSet<(ChannelType Type, string Destination)>> SentToAsync(
        NocturneDbContext db, Guid instanceId, CancellationToken ct) =>
        (await db.AlertDeliveries.AsNoTracking()
            .Where(d => d.AlertInstanceId == instanceId && d.AlertRuleChannelId != null && d.Status != "failed")
            .Select(d => new { d.ChannelType, d.Destination })
            .ToListAsync(ct))
        .Select(d => (d.ChannelType, d.Destination))
        .ToHashSet();

    /// <summary>
    /// Whether a test went to exactly the rule's current channels. An <c>in_app</c> channel the test
    /// did not go to is left out: it is a member added to urgent lows, whom a test never reaches.
    /// </summary>
    public static bool Matches(
        HashSet<(ChannelType Type, string Destination)> sent, IEnumerable<AlertRuleChannelEntity> current)
    {
        var testedInApp = sent.Where(s => s.Type == ChannelType.InApp).Select(s => s.Destination).ToHashSet();
        var now = current
            .Where(c => c.ChannelType != ChannelType.InApp || testedInApp.Contains(c.Destination))
            .Select(c => (c.ChannelType, c.Destination))
            .ToHashSet();
        return sent.Count > 0 && sent.SetEquals(now);
    }
}
