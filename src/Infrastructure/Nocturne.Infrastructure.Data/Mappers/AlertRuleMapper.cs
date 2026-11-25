using System.Linq.Expressions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>Stored alert rules and channels as the Core read models.</summary>
/// <remarks>
/// A rule's condition type, severity and scope class are read tolerantly
/// (<see cref="Converters.EnumMemberValueConverter{TEnum}.Unreadable"/>), so a stored value no member
/// has still lets the rule be listed, opened, saved and deleted. Every reader that turns one of the
/// three into a member goes through <see cref="Readable(AlertRuleSeverity)"/> and its overloads, or
/// through <see cref="ToSnapshot(AlertRuleEntity)"/>, and evaluation leaves such a rule out by
/// <see cref="IsReadable"/>.
/// </remarks>
public static class AlertRuleMapper
{
    /// <summary>The rule with its loaded channels in <c>SortOrder</c>, and the columns it shows a stand-in for.</summary>
    public static AlertRuleRecord ToRecord(AlertRuleEntity r) => new(
        ToSnapshot(r),
        r.Description,
        r.IsEnabled,
        r.ManagedBy,
        r.ReviewReason,
        r.Channels.OrderBy(c => c.SortOrder).Select(ToSnapshot).ToList(),
        UnreadableValuesOf(r));

    /// <summary>The rule as evaluated, with the <see cref="Readable(AlertRuleSeverity)"/> stand-ins.</summary>
    public static AlertRuleSnapshot ToSnapshot(AlertRuleEntity r) => new(
        r.Id, r.TenantId, r.Name, Readable(r.ConditionType),
        r.ConditionParams, Readable(r.Severity), r.ClientConfiguration, r.SortOrder,
        r.AutoResolveEnabled, r.AutoResolveParams, r.AllowThroughDnd, Readable(r.ScopeClass), r.OnUnknown,
        r.GlucoseFreshnessMinutes, r.EnabledAt);

    public static AlertRuleChannelSnapshot ToSnapshot(AlertRuleChannelEntity c) => new(
        c.Id, c.AlertRuleId, c.ChannelType,
        c.Destination, c.DestinationLabel, c.SortOrder, c.Metadata, c.Secret,
        c.AddedBySubjectId, c.StopsAt, c.RecipientSubjectId);

    /// <summary>
    /// The review reason the rule's owner is shown: the condition type, or else the severity, that
    /// the rule shows a stand-in for, else the stored reason. Never stored, so a writer that keeps
    /// <see cref="AlertRuleRecord.ReviewReason"/> keeps only what a data repair wrote.
    /// </summary>
    public static string? ShownReviewReason(AlertRuleRecord record) =>
        record.Unreadable.HasFlag(AlertRuleUnreadableValues.ConditionType) ? AlertRuleReviewReasons.ConditionTypeUnrecognized
        : record.Unreadable.HasFlag(AlertRuleUnreadableValues.Severity) ? AlertRuleReviewReasons.SeverityUnrecognized
        : record.ReviewReason;

    /// <summary>
    /// False when the rule holds a condition type, severity or scope class no member has. Such a
    /// rule is not evaluated: it is reported (<see cref="UnreadableAlertRule"/>) until it is saved.
    /// </summary>
    public static bool IsReadable(AlertRuleEntity rule) => UnreadableValuesOf(rule) == AlertRuleUnreadableValues.None;

    private static AlertRuleUnreadableValues UnreadableValuesOf(AlertRuleEntity r) =>
        (Enum.IsDefined(r.ConditionType) ? AlertRuleUnreadableValues.None : AlertRuleUnreadableValues.ConditionType)
        | (Enum.IsDefined(r.Severity) ? AlertRuleUnreadableValues.None : AlertRuleUnreadableValues.Severity)
        | (Enum.IsDefined(r.ScopeClass) ? AlertRuleUnreadableValues.None : AlertRuleUnreadableValues.ScopeClass);

    /// <summary>
    /// The stored severity, or critical for one no member has: the severity the rule's notice is
    /// sent at, as a data repair made such a rule critical
    /// (<see cref="AlertRuleReviewReasons.SeverityUnrecognized"/>).
    /// </summary>
    public static AlertRuleSeverity Readable(AlertRuleSeverity stored) =>
        Enum.IsDefined(stored) ? stored : AlertRuleSeverity.Critical;

    /// <summary>
    /// The stored condition type, or composite for one no member has, as a data repair stored such a
    /// rule (<see cref="AlertRuleReviewReasons.ConditionTypeUnrecognized"/>).
    /// </summary>
    public static AlertConditionType Readable(AlertConditionType stored) =>
        Enum.IsDefined(stored) ? stored : AlertConditionType.Composite;

    /// <summary>
    /// The stored scope class, or <see cref="RuleScopeClass.Undirected"/> for one no member has: the
    /// class of a rule not yet classified, which a save classifies again.
    /// </summary>
    public static RuleScopeClass Readable(RuleScopeClass stored) =>
        Enum.IsDefined(stored) ? stored : RuleScopeClass.Undirected;

    /// <summary>
    /// A query predicate: the stored severity at <paramref name="stored"/> reads
    /// (<see cref="Readable(AlertRuleSeverity)"/>) as one of <paramref name="severities"/>.
    /// </summary>
    /// <remarks>
    /// A stored value no member has is only known in the database as not being any member, so a set
    /// that holds critical is matched as "not one of the other members", and any other set by its
    /// members.
    /// </remarks>
    public static Expression<Func<T, bool>> SeverityReadsAs<T>(
        Expression<Func<T, AlertRuleSeverity>> stored, params AlertRuleSeverity[] severities)
    {
        var value = stored.Body;
        Expression Is(AlertRuleSeverity severity) => Expression.Equal(value, Expression.Constant(severity));

        Expression body = severities.Contains(AlertRuleSeverity.Critical)
            ? Enum.GetValues<AlertRuleSeverity>()
                .Where(s => !severities.Contains(s))
                .Select(s => (Expression)Expression.Not(Is(s)))
                .Aggregate((Expression)Expression.Constant(true), Expression.AndAlso)
            : severities
                .Select(Is)
                .Aggregate((Expression)Expression.Constant(false), Expression.OrElse);

        return Expression.Lambda<Func<T, bool>>(body, stored.Parameters);
    }
}
