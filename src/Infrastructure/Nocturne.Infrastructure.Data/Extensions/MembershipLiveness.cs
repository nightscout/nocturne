using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Whether a membership has ended: from its <see cref="TenantMemberEntity.AccessEndsAt"/> onward it
/// has, and with none it never does.
/// </summary>
/// <remarks>
/// Every read that decides what a subject may do on a tenant, which tenants a subject has, or who
/// receives something for a tenant reads only live memberships through <see cref="LiveAt"/>. The
/// reads that manage members, the setup gate and the owner reads see every row: an owner cannot
/// have an end date, and an ended member must not send a tenant back to first-run setup.
/// </remarks>
public static class MembershipLiveness
{
    private static readonly Expression<Func<DateTime?, DateTime, bool>> Live =
        (accessEndsAt, now) => accessEndsAt == null || accessEndsAt > now;

    private static readonly Func<DateTime?, DateTime, bool> LiveNow = Live.Compile();

    /// <summary>Whether a membership that ends at <paramref name="accessEndsAt"/> has ended at <paramref name="now"/>.</summary>
    public static bool HasEnded(DateTime? accessEndsAt, DateTime now) => !LiveNow(accessEndsAt, now);

    /// <summary>
    /// Whether <paramref name="end"/> outlasts <paramref name="callerEnd"/>: the caller's access
    /// ends and <paramref name="end"/> is later or none. The one date test behind every ceiling;
    /// a member or invite for which it holds is beyond the caller's reach.
    /// </summary>
    public static bool Outlasts(DateTime? callerEnd, DateTime? end) =>
        callerEnd is { } ceiling && (end is not { } e || e > ceiling);

    /// <summary>The memberships that have not ended at <paramref name="now"/>.</summary>
    public static IQueryable<TenantMemberEntity> LiveAt(this IQueryable<TenantMemberEntity> members, DateTime now) =>
        members.Where(IsLive(now));

    /// <summary>The predicate <see cref="LiveAt"/> filters by, with <paramref name="now"/> sent as a parameter.</summary>
    public static Expression<Func<TenantMemberEntity, bool>> IsLive(DateTime now)
    {
        var member = Expression.Parameter(typeof(TenantMemberEntity), "m");
        var at = new Instant(now);
        var body = ReplacingExpressionVisitor.Replace(
            [Live.Parameters[0], Live.Parameters[1]],
            [
                Expression.Property(member, nameof(TenantMemberEntity.AccessEndsAt)),
                Expression.Property(Expression.Constant(at), nameof(Instant.Value)),
            ],
            Live.Body);
        return Expression.Lambda<Func<TenantMemberEntity, bool>>(body, member);
    }

    private sealed class Instant(DateTime value)
    {
        public DateTime Value { get; } = value;
    }
}
