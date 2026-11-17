using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>Stored alert rules and channels as the Core read models.</summary>
public static class AlertRuleMapper
{
    public static AlertRuleSnapshot ToSnapshot(AlertRuleEntity r) => new(
        r.Id, r.TenantId, r.Name, r.ConditionType,
        r.ConditionParams, r.Severity, r.ClientConfiguration, r.SortOrder,
        r.AutoResolveEnabled, r.AutoResolveParams, r.AllowThroughDnd, r.ScopeClass);

    public static AlertRuleChannelSnapshot ToSnapshot(AlertRuleChannelEntity c) => new(
        c.Id, c.AlertRuleId, c.ChannelType,
        c.Destination, c.DestinationLabel, c.SortOrder, c.Metadata, c.Secret);

    /// <summary>The rule with its loaded channels in <c>SortOrder</c>.</summary>
    public static AlertRuleRecord ToRecord(AlertRuleEntity r) => new(
        ToSnapshot(r),
        r.Description,
        r.IsEnabled,
        r.ManagedBy,
        r.ReviewReason,
        r.Channels.OrderBy(c => c.SortOrder).Select(ToSnapshot).ToList());
}
