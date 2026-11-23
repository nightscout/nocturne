using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Whether a membership is in force: from its <see cref="TenantMemberEntity.AccessEndsAt"/> onward it
/// has ended, and with none it never does; and from its <see cref="TenantMemberEntity.NextReasonLapseAt"/>
/// onward it is due, since the row still holds access a reason no longer gives.
/// </summary>
/// <remarks>
/// Every read that decides what a subject may do on a tenant, which tenants a subject has, or who
/// receives something for a tenant reads only live memberships through <see cref="LiveAt"/>, so a due
/// row is absent to it until it is recomputed. The reads that decide a request or a sign-in select by
/// end alone (<see cref="IsUnended"/>) and recompute a due row they find
/// (<see cref="Services.MembershipReasons.ReadInForceAsync{T}"/>). The reads that manage members, the
/// setup gate and the owner reads see every row: an owner cannot have an end date, and an ended member
/// must not send a tenant back to first-run setup.
/// </remarks>
public static class MembershipLiveness
{
    private static readonly Expression<Func<DateTime?, DateTime, bool>> Unended =
        (accessEndsAt, now) => accessEndsAt == null || accessEndsAt > now;

    private static readonly Expression<Func<DateTime?, DateTime?, DateTime, bool>> Live =
        (accessEndsAt, nextReasonLapseAt, now) =>
            (accessEndsAt == null || accessEndsAt > now) && (nextReasonLapseAt == null || nextReasonLapseAt > now);

    private static readonly Func<DateTime?, DateTime, bool> UnendedNow = Unended.Compile();

    /// <summary>Whether a membership that ends at <paramref name="accessEndsAt"/> has ended at <paramref name="now"/>.</summary>
    public static bool HasEnded(DateTime? accessEndsAt, DateTime now) => !UnendedNow(accessEndsAt, now);

    /// <summary>Whether a row whose next reason lapses at <paramref name="nextReasonLapseAt"/> must be recomputed at <paramref name="now"/>.</summary>
    public static bool IsDue(DateTime? nextReasonLapseAt, DateTime now) =>
        nextReasonLapseAt is { } lapse && HasEnded(lapse, now);

    /// <summary>
    /// Whether <paramref name="end"/> outlasts <paramref name="callerEnd"/>: the caller's access
    /// ends and <paramref name="end"/> is later or none. The one date test behind every ceiling;
    /// a member or invite for which it holds is beyond the caller's reach.
    /// </summary>
    public static bool Outlasts(DateTime? callerEnd, DateTime? end) =>
        callerEnd is { } ceiling && (end is not { } e || e > ceiling);

    /// <summary>The memberships that have not ended and are not due at <paramref name="now"/>.</summary>
    public static IQueryable<TenantMemberEntity> LiveAt(this IQueryable<TenantMemberEntity> members, DateTime now) =>
        members.Where(IsLive(now));

    /// <summary>The predicate <see cref="LiveAt"/> filters by, with <paramref name="now"/> sent as a parameter.</summary>
    public static Expression<Func<TenantMemberEntity, bool>> IsLive(DateTime now) =>
        Bind(Live, now, nameof(TenantMemberEntity.AccessEndsAt), nameof(TenantMemberEntity.NextReasonLapseAt));

    /// <summary>
    /// The memberships that have not ended at <paramref name="now"/>, due or not. Only a read that
    /// recomputes a due row it finds may select by it; <c>EndOnlyMembershipReadGuardTests</c> pins them.
    /// </summary>
    public static Expression<Func<TenantMemberEntity, bool>> IsUnended(DateTime now) =>
        Bind(Unended, now, nameof(TenantMemberEntity.AccessEndsAt));

    private static Expression<Func<TenantMemberEntity, bool>> Bind(LambdaExpression rule, DateTime now, params string[] columns)
    {
        var member = Expression.Parameter(typeof(TenantMemberEntity), "m");
        var at = new Instant(now);
        var body = ReplacingExpressionVisitor.Replace(
            rule.Parameters,
            [
                .. columns.Select(Expression (column) => Expression.Property(member, column)),
                Expression.Property(Expression.Constant(at), nameof(Instant.Value)),
            ],
            rule.Body);
        return Expression.Lambda<Func<TenantMemberEntity, bool>>(body, member);
    }

    private sealed class Instant(DateTime value)
    {
        public DateTime Value { get; } = value;
    }
}
