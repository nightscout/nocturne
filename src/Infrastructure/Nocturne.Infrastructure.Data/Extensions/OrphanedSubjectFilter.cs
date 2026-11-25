using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "an account on this tenant that cannot sign in".
/// </summary>
/// <remarks>
/// A member holding neither a passkey nor a working provider identity has no way back in on their
/// own, unless a recovery link can reach them: that takes a delivery route on the instance
/// (<see cref="IAccountLinkRoute"/>) and an address on the account that has been confirmed
/// (<see cref="HoldsConfirmedEmail"/>). Without a route an address does not count, because the link
/// could never arrive. Every caller must agree on what qualifies: this set decides whether the site
/// is in recovery mode, which members an administrator is shown and may issue a recovery code for,
/// and whether such a code may still be redeemed, so a predicate that is wider in one place than
/// another either strands an account or lets a locked-out one go unnoticed. That is why the route is
/// a parameter taken from the container rather than a flag a caller could forget or get wrong.
/// <para>
/// Deliberately not a definition of "has no credential at all". A device or service token is a
/// direct grant on <c>oauth_grants</c> issued to somebody else's subject, so it never reaches this
/// query. A demo tenant's visitor is credential-less by design and stands for nobody, so it is
/// excluded here rather than relying on callers to check <c>IsDemo</c> first: a filter that is only
/// correct because an unrelated early return happens to run before it is not correct.
/// </para>
/// </remarks>
public static class OrphanedSubjectFilter
{
    /// <summary>
    /// Whether the account holds an address it has confirmed. The recovery-link lookup reads the
    /// same expression, so an account this predicate leaves to email is one that would be sent a link.
    /// </summary>
    public static readonly Expression<Func<SubjectEntity, bool>> HoldsConfirmedEmail =
        s => s.Email != null && s.EmailConfirmedAt != null;

    /// <summary>The negation of <see cref="HoldsConfirmedEmail"/>.</summary>
    internal static readonly Expression<Func<SubjectEntity, bool>> LacksConfirmedEmail =
        Expression.Lambda<Func<SubjectEntity, bool>>(
            Expression.Not(HoldsConfirmedEmail.Body), HoldsConfirmedEmail.Parameters);

    /// <summary>
    /// The subjects on <paramref name="tenantId"/> that hold a membership but cannot sign in.
    /// </summary>
    /// <param name="db">
    /// The context to read on. Must not be a share-flagged context: membership is not share-visible
    /// data, so under Row Level Security a share is denied every row and would read a configured
    /// tenant as having no members at all.
    /// </param>
    /// <param name="tenantId">The tenant whose members are considered.</param>
    /// <param name="linkRoute">Whether a recovery link can be delivered on this instance.</param>
    public static IQueryable<SubjectEntity> OrphanedSubjectsOf(
        this NocturneDbContext db, Guid tenantId, IAccountLinkRoute linkRoute) =>
        db.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.Subject!)
            .WhoCannotSignIn(db, linkRoute);

    /// <summary>
    /// Those of <paramref name="subjects"/> that cannot sign in, by the rule this class states.
    /// </summary>
    /// <param name="subjects">The subjects considered.</param>
    /// <param name="db">The context <paramref name="subjects"/> is read on.</param>
    /// <param name="linkRoute">Whether a recovery link can be delivered on this instance.</param>
    /// <param name="withoutProvider">
    /// A provider whose identities do not count, to ask who would be stranded if it went away.
    /// </param>
    public static IQueryable<SubjectEntity> WhoCannotSignIn(
        this IQueryable<SubjectEntity> subjects,
        NocturneDbContext db,
        IAccountLinkRoute linkRoute,
        Guid? withoutProvider = null)
    {
        var identities = withoutProvider is { } excluded
            ? db.WorkingOidcIdentities().Where(i => i.ProviderId != excluded)
            : db.WorkingOidcIdentities();
        return subjects
            .Where(HoldsStanding)
            .WithoutSignInFactor(db, linkRoute, identities);
    }

    /// <summary>
    /// Those of <paramref name="subjects"/> that are active accounts of a person and hold a sign-in
    /// factor: with the default identities, the complement of <see cref="WhoCannotSignIn"/> within
    /// the active accounts of a person.
    /// </summary>
    /// <param name="subjects">The subjects considered.</param>
    /// <param name="db">The context <paramref name="subjects"/> is read on.</param>
    /// <param name="linkRoute">Whether a recovery link can be delivered on this instance.</param>
    /// <param name="identities">
    /// The provider identities that count as a factor, as <see cref="WithoutSignInFactor"/> takes them.
    /// Working identities when null.
    /// </param>
    public static IQueryable<SubjectEntity> WhoCanSignIn(
        this IQueryable<SubjectEntity> subjects,
        NocturneDbContext db,
        IAccountLinkRoute linkRoute,
        IQueryable<SubjectOidcIdentityEntity>? identities = null)
    {
        var stuck = subjects.WithoutSignInFactor(db, linkRoute, identities);
        return subjects
            .Where(HoldsStanding)
            .Where(s => !stuck.Any(o => o.Id == s.Id));
    }

    private static readonly Expression<Func<SubjectEntity, bool>> HoldsStanding =
        s => s.IsActive && !s.IsSystemSubject && !s.IsDemoSubject;

    /// <summary>
    /// Whether a loaded subject is an active account of a person: the standing rule that
    /// <see cref="WhoCannotSignIn"/> and <see cref="WhoCanSignIn"/> apply in SQL.
    /// </summary>
    public static readonly Func<SubjectEntity, bool> IsStanding = HoldsStanding.Compile();

    /// <summary>
    /// Those of <paramref name="subjects"/> that hold no passkey, none of <paramref name="identities"/>
    /// and, while a recovery link can be delivered, no confirmed address: the factor part of
    /// <see cref="WhoCannotSignIn"/>, without its subject filters.
    /// </summary>
    /// <remarks>
    /// Each caller adds its own conditions beside this one. <see cref="WhoCannotSignIn"/> counts
    /// working identities only, because a provider that is off cannot sign anyone in, and asks who
    /// is locked out. <c>ISubjectRepository.FindSetupOwnerCandidateAsync</c> passes every identity,
    /// because a provider enabled again signs that identity in again, and it also refuses an
    /// authenticator, an unexpired session and a live login code, which this predicate does not
    /// read. <c>ISubjectRepository.CountStrandedByProviderAsync</c> reaches this through
    /// <see cref="WhoCannotSignIn"/> with the provider that goes away left out of the identities.
    /// <c>ISubjectRepository.ReplaceStrandedPlatformAdminsAsync</c> reaches it through
    /// <see cref="WhoCanSignIn"/> with every identity to decide whether an admin keeps the flag, and
    /// with working identities only to decide whether the successor can take it.
    /// </remarks>
    /// <param name="subjects">The subjects considered.</param>
    /// <param name="db">The context <paramref name="subjects"/> is read on.</param>
    /// <param name="linkRoute">Whether a recovery link can be delivered on this instance.</param>
    /// <param name="identities">The provider identities that count as a factor. Working identities when null.</param>
    public static IQueryable<SubjectEntity> WithoutSignInFactor(
        this IQueryable<SubjectEntity> subjects,
        NocturneDbContext db,
        IAccountLinkRoute linkRoute,
        IQueryable<SubjectOidcIdentityEntity>? identities = null)
    {
        identities ??= db.WorkingOidcIdentities();
        var withoutFactor = subjects.Where(s =>
            !db.PasskeyCredentials.Any(p => p.SubjectId == s.Id)
            && !identities.Any(i => i.SubjectId == s.Id));

        return linkRoute.IsConfigured ? withoutFactor.Where(LacksConfirmedEmail) : withoutFactor;
    }
}
