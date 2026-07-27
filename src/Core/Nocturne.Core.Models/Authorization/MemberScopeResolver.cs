namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Resolves what a tenant membership grants on a given credential: the single place that turns a
/// member's effective permissions (role permissions ∪ direct permissions) into the granted scope
/// set. <c>MemberScopeMiddleware</c> applies it per request; <c>TenantOverviewService</c> applies it
/// per membership to decide which tenants the caller may see. Both must agree, or the tenant picker
/// and the endpoints it links to disagree about access.
/// </summary>
/// <seealso cref="OAuthScopes"/>
/// <seealso cref="TenantPermissions"/>
/// <seealso cref="ScopeTranslator"/>
public static class MemberScopeResolver
{
    /// <summary>
    /// Credential types that carry no scope grant of their own — the authenticated human's own
    /// interactive login. Tenant membership is the only authority for these, so a <c>"*"</c>
    /// membership widens to superuser. Every other credential presents scopes that bound what it
    /// may do (an OAuth access token's consented scopes, a direct grant's scopes, an API key's
    /// grant scopes, a guest link's scopes) and is intersected with membership instead. Membership
    /// resolution must never widen a scoped credential, or the consent decision is erased. Keyed on
    /// the credential type rather than on "the scope list is empty" because an interactive OIDC
    /// login carries the identity provider's own scopes (<c>openid</c>, <c>profile</c>), which are
    /// not Nocturne data scopes. Types absent from this set are treated as scoped (fail closed).
    /// </summary>
    /// <remarks>
    /// Per-type basis for membership:
    /// <list type="bullet">
    /// <item><see cref="AuthType.SessionCookie"/> — <c>SessionCookieHandler</c> never sets
    /// <c>Scopes</c>; the session JWT's permission claims are the subject's global
    /// <c>SubjectRoles</c>, which are unrelated to this tenant.</item>
    /// <item><see cref="AuthType.OidcToken"/> — <c>OidcTokenHandler</c> sets <c>Scopes</c> to the
    /// provider's configured scopes (<c>openid</c>, <c>profile</c>, <c>email</c>), an outbound
    /// protocol list identical for every user of that provider, not a per-user Nocturne grant.</item>
    /// <item><see cref="AuthType.LegacyJwt"/> — structurally unscoped:
    /// <c>OAuthAccessTokenHandler</c> runs first and claims every JWT bearing a <c>scope</c> or
    /// <c>client_id</c> claim, so a JWT that reaches <c>LegacyJwtHandler</c> has neither.
    /// <c>SessionService</c> mints session tokens through the same <c>IJwtService</c>, so this is
    /// the same credential as a session cookie presented on a different transport.</item>
    /// <item><see cref="AuthType.LegacyAccessToken"/> — <c>AccessTokenHandler</c> never sets
    /// <c>Scopes</c>. The token identifies a subject; every path that gives a subject a tenant
    /// membership (invite acceptance, membership-request approval, platform admin, tenant setup)
    /// assigns tenant roles deliberately, and none of them writes global <c>SubjectRoles</c>. The
    /// membership is the grant.</item>
    /// </list>
    /// <see cref="AuthType.ApiKey"/>, <see cref="AuthType.Guest"/>, <see cref="AuthType.InstanceKey"/>
    /// and <see cref="AuthType.PlatformAccess"/> are absent because <c>MemberScopeMiddleware</c>
    /// resolves them before reaching membership at all; <see cref="AuthType.OAuthAccessToken"/> and
    /// <see cref="AuthType.DirectGrant"/> are absent because their scopes are a consent boundary.
    /// </remarks>
    public static readonly IReadOnlySet<AuthType> UnscopedCredentialTypes = new HashSet<AuthType>
    {
        AuthType.SessionCookie,
        AuthType.OidcToken,
        AuthType.LegacyJwt,
        AuthType.LegacyAccessToken,
    };

    /// <summary>
    /// Resolves the scopes a membership grants on a credential.
    /// </summary>
    /// <param name="effectivePermissions">
    /// The membership's effective permissions: role permissions unioned with direct permissions.
    /// These come from the tenant RBAC vocabulary, so they are normalized through
    /// <see cref="OAuthScopes.NormalizeMemberPermissions"/> rather than
    /// <see cref="OAuthScopes.Normalize"/>.
    /// </param>
    /// <param name="authType">The credential type, matched against <see cref="UnscopedCredentialTypes"/>.</param>
    /// <param name="credentialScopes">
    /// The scopes the credential presents. Ignored for an unscoped credential, which has none.
    /// </param>
    /// <returns>The granted scope set. Empty when the membership grants nothing.</returns>
    public static IReadOnlySet<string> Resolve(
        IReadOnlySet<string> effectivePermissions,
        AuthType authType,
        IReadOnlySet<string> credentialScopes)
    {
        var isUnscoped = UnscopedCredentialTypes.Contains(authType);

        // Superuser on an unscoped credential: membership is the whole authority. The raw
        // permissions are published (rather than the normalized expansion) so "*" itself reaches
        // RequireScope checks and ScopeTranslator collapses the trie to a wildcard.
        if (isUnscoped && effectivePermissions.Contains(TenantPermissions.Superuser))
            return effectivePermissions;

        var memberScopes = OAuthScopes.NormalizeMemberPermissions(effectivePermissions).ToHashSet();

        // Member-personal device scopes are not part of the role intersection. device.notify /
        // device.actuate authorize the alert engine to drive the member's OWN registered client
        // devices (rows RLS-scoped to the member's subject), not patient data. Role rows are
        // persisted per tenant at seed time and never reconciled
        // (TenantRoleService.SeedRolesForTenantAsync skips existing slugs), so roles seeded before
        // these atoms existed would strip the scopes for every pre-existing tenant — no relink or
        // re-consent can fix that. Treat them as held by any member with at least one permission;
        // zero-permission members (the Denied seed role) stay fully stripped because alert
        // actuations reveal patient state. Added before the intersection below, so a scoped
        // credential still has to carry them.
        if (effectivePermissions.Count > 0)
            memberScopes.UnionWith(TenantPermissions.MemberPersonalScopes);

        // No grant to intersect against, so membership is the whole ceiling. An empty scope list on
        // an unscoped credential is the absence of a ceiling, not a ceiling of nothing: intersecting
        // against it resolved every non-owner member — Admin, Clinician, Caretaker, Viewer — to zero
        // scopes, because only an Owner escaped through the superuser branch above.
        if (isUnscoped)
            return memberScopes;

        // Scoped credential: the grant is a consent boundary that membership must not widen past.
        //
        // The credential's scopes are re-normalized through the REQUEST vocabulary, which drops
        // anything a client could not have asked for — in particular the tenant-administration
        // atoms, which are absent from ValidRequestScopes precisely because no client may request
        // them and no user may consent to them. Without this the intersection would honour an
        // administration atom that reached a credential's scope list by any route (a hand-written
        // grant row, a future endpoint), because an exact match satisfies the intersection. Making
        // it structural here means the property does not rest on every issuing path staying correct.
        var boundedCredentialScopes = OAuthScopes.Normalize(credentialScopes);

        var resolved = new HashSet<string>();
        foreach (var memberScope in memberScopes)
        {
            if (OAuthScopes.SatisfiesScope(boundedCredentialScopes, memberScope))
            {
                resolved.Add(memberScope);
                continue;
            }

            // A readwrite membership on a read-only credential downgrades to read instead of
            // dropping. SatisfiesScope answers false for a readwrite requirement met only by read,
            // and NormalizeMemberPermissions does not add the read counterpart, so a member holding
            // glucose.readwrite against a glucose.read token previously resolved to neither scope.
            // Both sides permit the read counterpart: the membership because readwrite includes
            // read, the credential because it granted read outright.
            if (OAuthScopes.TryGetImpliedReadScope(memberScope, out var readScope)
                && OAuthScopes.SatisfiesScope(boundedCredentialScopes, readScope))
            {
                resolved.Add(readScope);
            }
        }

        return resolved;
    }
}
