using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "a linked OIDC identity the holder can sign in with".
/// </summary>
/// <remarks>
/// A disabled or de-configured provider cannot sign in, so an identity pointing at one is not a
/// credential. Every check that asks whether a subject or tenant has a sign-in method must agree
/// with this, or a locked-out account goes unnoticed and its tenant never reaches recovery mode.
/// </remarks>
public static class WorkingOidcIdentityFilter
{
    /// <summary>
    /// The linked OIDC identities whose provider exists and is enabled.
    /// </summary>
    public static IQueryable<SubjectOidcIdentityEntity> WorkingOidcIdentities(
        this NocturneDbContext db) =>
        db.SubjectOidcIdentities.Where(i =>
            db.OidcProviders.Any(p => p.Id == i.ProviderId && p.IsEnabled));
}
