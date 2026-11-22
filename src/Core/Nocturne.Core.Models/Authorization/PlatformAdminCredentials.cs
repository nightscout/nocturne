namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Which credentials may carry a subject's instance-wide platform-admin standing.
/// </summary>
/// <remarks>
/// Platform admin is not tenant-scoped and is not a scope: it is a role claim that
/// <c>Controllers/V4/PlatformAdmin</c> gates on by itself, reaching every tenant on the instance.
/// A subject can therefore hold it while issuing credentials that are meant to do one narrow job,
/// and a device token minted by or for a platform admin would otherwise inherit the whole instance.
/// <para>
/// Only a Nocturne session carries the standing, because only a session is issued by Nocturne's own
/// sign-in checks, which hold it back until an enrolled second factor is passed. A grant, an
/// api-secret or an OAuth token is a delegation bounded by its scopes.
/// </para>
/// <para>
/// <see cref="AuthType.PlatformAccess"/> is included because it is minted only by an endpoint that
/// already required platform admin, so refusing it there would lock an operator out of the tenant
/// they deliberately granted themselves access to.
/// <see cref="AuthType.InstanceKey"/> is absent because its handler asserts the flag directly
/// rather than reading it off a subject; infrastructure has no subject to read.
/// </para>
/// <para>
/// A session redeemed from a login code (<see cref="AuthContext.IsHandedOff"/>) passed none of the
/// sign-in checks either: someone else issued the code, and redeeming it asks for no second factor.
/// </para>
/// <para>
/// This is deliberately its own set rather than a reuse of
/// <see cref="MemberScopeResolver.UnscopedCredentialTypes"/>. That set answers "does this credential
/// carry a scope grant of its own"; this one answers "did the person sign in for it". Tying them
/// together would let a change to scope handling silently move a privilege boundary.
/// </para>
/// </remarks>
public static class PlatformAdminCredentials
{
    private static readonly IReadOnlySet<AuthType> CarryingTypes = new HashSet<AuthType>
    {
        AuthType.SessionCookie,
        AuthType.LegacyJwt,
        AuthType.PlatformAccess,
    };

    /// <summary>
    /// The role claim that presents the standing. It is added from <see cref="AuthContext.IsPlatformAdmin"/>
    /// and from nothing else: a role a credential carries by this name, in any case, is never a claim.
    /// </summary>
    public const string RoleName = "platform_admin";

    /// <summary>
    /// Whether <paramref name="role"/> spells <see cref="RoleName"/>, ignoring case.
    /// </summary>
    public static bool IsRoleName(string? role) =>
        string.Equals(role, RoleName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="auth"/> may present its subject's platform-admin standing.
    /// </summary>
    public static bool Carries(AuthContext auth) =>
        CarryingTypes.Contains(auth.AuthType) && !auth.IsHandedOff;
}
