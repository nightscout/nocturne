using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "the Public subject a share link runs as".
/// </summary>
/// <remarks>
/// The subject is a system row with no person behind it, shared by every tenant and told apart from
/// other system subjects, such as the one <see cref="DeviceSubjectFilter"/> names, by its name alone.
/// A second spelling of that rule that drifts would let a share link's membership be edited as a
/// person's, or provision a tenant with no Public membership, so its owner can never open a share.
/// </remarks>
public static class PublicSubjectFilter
{
    /// <summary>The name the Public system subject carries.</summary>
    public const string PublicSubjectName = "Public";

    /// <summary>The memberships that belong to the Public subject.</summary>
    public static IQueryable<TenantMemberEntity> OfPublicSubject(this IQueryable<TenantMemberEntity> members) =>
        members.Where(m => m.Subject!.IsSystemSubject && m.Subject.Name == PublicSubjectName);

    /// <summary>Whether <paramref name="member"/> belongs to the Public subject.</summary>
    public static bool IsPublicSubject(this TenantMemberRecord member) =>
        member is { IsSystemSubject: true, SubjectName: PublicSubjectName };
}
