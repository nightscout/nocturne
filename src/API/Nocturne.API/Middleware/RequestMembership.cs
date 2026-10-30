using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Middleware;

/// <summary>
/// An authenticated subject's membership of the resolved tenant, read once for the request and handed
/// from <see cref="AuthenticationMiddleware"/> to <see cref="MemberScopeMiddleware"/> through
/// <c>HttpContext.Items</c>.
/// </summary>
/// <remarks>
/// Each context open costs a <c>set_config</c> round trip and a pool reset, and every authenticated
/// request needs these facts before its controller runs. So they are read in one statement: joined to
/// the grant by a handler that already reads one (<see cref="WithMembership{TOuter}"/>), otherwise
/// joined to the subject's row with its platform-admin flag (<see cref="ReadAsync"/>). Nothing outlives
/// the request, so a removed member or a revoked credential is refused on the next one.
/// </remarks>
/// <param name="SubjectId">The authenticated subject.</param>
/// <param name="TenantId">The resolved tenant.</param>
/// <param name="Membership">The subject's membership of the tenant, or null when they hold none.</param>
internal sealed record RequestMembership(Guid SubjectId, Guid TenantId, MembershipFacts? Membership)
{
    private const string ItemsKey = "RequestMembership";

    /// <summary>Hands the membership to the rest of the request's pipeline.</summary>
    internal void StoreOn(HttpContext context) => context.Items[ItemsKey] = this;

    /// <summary>The membership stored on the request for this subject and tenant, if one was.</summary>
    internal static RequestMembership? Of(HttpContext context, Guid subjectId, Guid tenantId) =>
        context.Items[ItemsKey] is RequestMembership stored
        && stored.SubjectId == subjectId
        && stored.TenantId == tenantId
            ? stored
            : null;

    /// <summary>
    /// Reads the subject's membership of <paramref name="tenantId"/> and their platform-admin flag in
    /// one statement, on a context pinned to that tenant.
    /// </summary>
    internal static async Task<(RequestMembership Membership, bool IsPlatformAdmin)> ReadAsync(
        INocturneDbContextFactory contexts, Guid subjectId, Guid tenantId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForTenantAsync(tenantId, ct);
        var row = await WithMembership(
                db.Subjects.AsNoTracking().Where(s => s.Id == subjectId), s => s.Id, db, tenantId)
            .Select(r => new { r.Outer.IsPlatformAdmin, r.Member })
            .FirstOrDefaultAsync(ct);

        return (new RequestMembership(subjectId, tenantId, row?.Member.ToFacts()), row?.IsPlatformAdmin ?? false);
    }

    /// <summary>
    /// Pairs each row of <paramref name="outer"/> with the membership of
    /// <paramref name="tenantId"/> held by the subject <paramref name="subjectOf"/> names, as a left
    /// join, so the membership comes back in the same statement.
    /// </summary>
    internal static IQueryable<WithMember<TOuter>> WithMembership<TOuter>(
        IQueryable<TOuter> outer,
        Expression<Func<TOuter, Guid>> subjectOf,
        NocturneDbContext db,
        Guid tenantId) =>
        outer
            .GroupJoin(
                db.TenantMembers.AsNoTracking().Where(m => m.TenantId == tenantId),
                subjectOf,
                m => m.SubjectId,
                (o, members) => new { o, members })
            .SelectMany(
                joined => joined.members.DefaultIfEmpty(),
                (joined, m) => new WithMember<TOuter>
                {
                    Outer = joined.o,
                    Member = new MemberRow
                    {
                        Id = (Guid?)m!.Id,
                        DirectPermissions = m.DirectPermissions,
                        RolePermissions = m.MemberRoles.Select(mr => mr.TenantRole.Permissions).ToList(),
                        LimitTo24Hours = (bool?)m.LimitTo24Hours,
                        LastUsedAt = m.LastUsedAt,
                    },
                });

    /// <summary>A row of the outer query with the membership joined to it.</summary>
    internal sealed class WithMember<TOuter>
    {
        public required TOuter Outer { get; init; }

        public required MemberRow Member { get; init; }
    }

    /// <summary>The membership columns of the left join; <see cref="Id"/> is null when there is none.</summary>
    internal sealed class MemberRow
    {
        public Guid? Id { get; init; }

        public List<string>? DirectPermissions { get; init; }

        public List<List<string>> RolePermissions { get; init; } = [];

        public bool? LimitTo24Hours { get; init; }

        public DateTime? LastUsedAt { get; init; }

        public MembershipFacts? ToFacts() => Id is { } id
            ? new MembershipFacts(
                id,
                new TenantMemberAccess(
                    TenantMemberEntity.EffectivePermissionsOf(RolePermissions, DirectPermissions).ToHashSet(),
                    LimitTo24Hours == true),
                LastUsedAt)
            : null;
    }
}

/// <summary>A membership as <see cref="RequestMembership"/> reads it.</summary>
/// <param name="Id">The membership row.</param>
/// <param name="Access">What the membership grants on its tenant.</param>
/// <param name="LastUsedAt">When the membership was last stamped as used.</param>
internal sealed record MembershipFacts(Guid Id, TenantMemberAccess Access, DateTime? LastUsedAt)
{
    /// <summary>The membership a grant repository read beside the grant, or null when there was none.</summary>
    internal static MembershipFacts? From(DirectGrantMembership? membership) =>
        membership is null ? null : new MembershipFacts(membership.Id, membership.Access, membership.LastUsedAt);
}
