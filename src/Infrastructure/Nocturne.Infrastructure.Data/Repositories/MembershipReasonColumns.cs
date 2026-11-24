using System.Linq.Expressions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// One of a membership's reasons as a read selects it beside the row, in the same statement
/// (<c>m.Reasons.AsQueryable().Select(MembershipReasonColumns.Of)</c>), so the read can tell what the
/// member's direct reason gives (<see cref="DirectAccess"/>) without another round trip.
/// </summary>
/// <remarks>
/// A read that recomputes a due row (<c>MembershipReasons.ReadAllInForceAsync</c>) selects the reasons
/// only of a row that is not due (<c>NextReasonLapseAt</c> null or later than now): it discards what it
/// read of a due row and reads again, so a reason too broken to read fails only the recompute, which
/// reads the row as absent, and not the read.
/// </remarks>
internal sealed class MembershipReasonColumns
{
    public string Kind { get; init; } = string.Empty;

    public List<string>? DirectPermissions { get; init; }

    public bool LimitTo24Hours { get; init; }

    public DateTime? AccessEndsAt { get; init; }

    public List<List<string>> RolePermissions { get; init; } = [];

    public static readonly Expression<Func<TenantMemberReasonEntity, MembershipReasonColumns>> Of = r => new MembershipReasonColumns
    {
        Kind = r.Kind,
        DirectPermissions = r.DirectPermissions,
        LimitTo24Hours = r.LimitTo24Hours,
        AccessEndsAt = r.AccessEndsAt,
        RolePermissions = r.ReasonRoles.Select(rr => rr.TenantRole.Permissions).ToList(),
    };

    /// <summary>
    /// What the member's direct reason gives at <paramref name="now"/>, given <paramref name="reasons"/>
    /// and <paramref name="row"/>, what the row gives; null when the member holds no direct reason in
    /// force.
    /// </summary>
    /// <remarks>
    /// Read as <see cref="TenantMemberRecord.HeldReasons"/> reads the reasons: when every stored reason
    /// is direct, or none is stored (a row an older build wrote, or a test seeded, that the startup
    /// reconcile has not yet given its reason), the direct reason is the row, since the row wins over a
    /// direct reason (<c>MembershipReasons.AlignWithRow</c>). Otherwise it is the stored direct reason,
    /// and none when there is no such reason or its own end has passed.
    /// </remarks>
    public static TenantMemberAccess? DirectAccess(
        IReadOnlyList<MembershipReasonColumns> reasons, TenantMemberAccess row, DateTime now)
    {
        if (reasons.All(r => r.Kind == MembershipReasonKind.Direct))
            return row;
        if (reasons.FirstOrDefault(r => r.Kind == MembershipReasonKind.Direct) is not { } direct
            || MembershipLiveness.HasEnded(direct.AccessEndsAt, now))
            return null;
        return new TenantMemberAccess(
            MemberPermissions.Effective(direct.RolePermissions, direct.DirectPermissions).ToHashSet(),
            direct.LimitTo24Hours,
            direct.AccessEndsAt);
    }
}
