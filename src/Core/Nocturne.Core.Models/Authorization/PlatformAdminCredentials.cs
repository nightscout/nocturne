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
/// The test is what the credential stands for, not what it is allowed to do. A session, a provider
/// login and a legacy session JWT are the person; a grant, an api-secret or an OAuth token is a
/// delegation the person handed out, bounded by its scopes. Only the first kind speaks for the
/// subject's own standing.
/// </para>
/// <para>
/// <see cref="AuthType.PlatformAccess"/> is included because it is minted only by an endpoint that
/// already required platform admin, so refusing it there would lock an operator out of the tenant
/// they deliberately granted themselves access to.
/// <see cref="AuthType.InstanceKey"/> is absent because its handler asserts the flag directly
/// rather than reading it off a subject; infrastructure has no subject to read.
/// </para>
/// <para>
/// This is deliberately its own set rather than a reuse of
/// <see cref="MemberScopeResolver.UnscopedCredentialTypes"/>, which it currently coincides with.
/// That set answers "does this credential carry a scope grant of its own"; this one answers "is
/// this credential the person". They agree today for related reasons, and tying them together
/// would let a change to scope handling silently move a privilege boundary.
/// </para>
/// </remarks>
public static class PlatformAdminCredentials
{
    private static readonly IReadOnlySet<AuthType> CarryingTypes = new HashSet<AuthType>
    {
        AuthType.SessionCookie,
        AuthType.OidcToken,
        AuthType.LegacyJwt,
        AuthType.PlatformAccess,
    };

    /// <summary>
    /// Whether <paramref name="authType"/> may present its subject's platform-admin standing.
    /// </summary>
    public static bool Carries(AuthType authType) => CarryingTypes.Contains(authType);
}
