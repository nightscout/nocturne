using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Which credentials may hear about every tenant their subject reads glucose for, rather than the
/// one tenant they were issued for.
/// </summary>
/// <remarks>
/// The cross-tenant overview is reachable on tenant subdomains as well as the apex, so a request may
/// carry a credential that only ever authorized one tenant: a tenant-pinned OAuth token, a
/// <c>noc_</c> direct grant, an api-secret, a guest code, a platform-access grant. Answering such a
/// credential with the subject's every tenant would widen it past the tenant it was issued for, so
/// only the human's own login is admitted: the set
/// <see cref="MemberScopeResolver.UnscopedCredentialTypes"/> holds, because "presents no scope
/// ceiling" and "is not a per-tenant grant" are the same property. A type absent from that set is
/// refused, so a new credential type fails closed.
/// </remarks>
public static class OverviewCredential
{
    /// <summary>Whether <paramref name="auth"/> is a subject's own login.</summary>
    public static bool Admits(AuthContext? auth) =>
        auth is { IsAuthenticated: true, SubjectId: not null }
        && MemberScopeResolver.UnscopedCredentialTypes.Contains(auth.AuthType);
}
