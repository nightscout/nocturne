using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "an account on this tenant that cannot sign in".
/// </summary>
/// <remarks>
/// A member holding neither a passkey nor a linked provider has no way back in, so the instance
/// opens the recovery flow rather than serving it API traffic. Every caller must agree on what
/// qualifies: a tenant answers 503 for as long as one of these exists, so a predicate that is wider
/// in one place than another either strands a tenant in recovery mode or lets a locked-out account
/// go unnoticed.
/// <para>
/// Deliberately not a definition of "has no credential at all". A device or service token is a
/// direct grant on <c>oauth_grants</c> issued to somebody else's subject, so it never reaches this
/// query. A demo tenant's visitor is credential-less by design and stands for nobody, so it is
/// excluded here rather than relying on callers to check <c>IsDemo</c> first: a filter that is only
/// correct because an unrelated early return happens to run before it is not correct.
/// </para>
/// <para>
/// Revoked memberships fall out through <see cref="TenantMemberEntity"/>'s global query filter. A
/// caller that reproduces this in SQL has to spell that condition out, since raw SQL does not carry
/// the filter.
/// </para>
/// </remarks>
public static class OrphanedSubjectFilter
{
    /// <summary>
    /// The subjects on <paramref name="tenantId"/> that hold a membership but no primary auth factor.
    /// </summary>
    /// <param name="db">
    /// The context to read on. Must not be a share-flagged context: membership is not share-visible
    /// data, so under Row Level Security a share is denied every row and would read a configured
    /// tenant as having no members at all.
    /// </param>
    /// <param name="tenantId">The tenant whose members are considered.</param>
    public static IQueryable<SubjectEntity> OrphanedSubjectsOf(
        this NocturneDbContext db, Guid tenantId) =>
        db.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.Subject!)
            .Where(s => s.IsActive
                && !s.IsSystemSubject
                && !s.IsDemoSubject
                && !db.PasskeyCredentials.Any(p => p.SubjectId == s.Id)
                && !db.SubjectOidcIdentities.Any(i => i.SubjectId == s.Id));
}
