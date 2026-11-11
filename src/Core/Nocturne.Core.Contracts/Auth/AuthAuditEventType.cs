namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Constants for auth audit event types
/// </summary>
public static class AuthAuditEventType
{
    /// <summary>
    /// Event type for a successful login.
    /// </summary>
    public const string Login = "login";

    /// <summary>
    /// Event type for a logout.
    /// </summary>
    public const string Logout = "logout";

    /// <summary>
    /// Event type when an access token is issued.
    /// </summary>
    public const string TokenIssued = "token_issued";

    /// <summary>
    /// Event type when an access token is revoked.
    /// </summary>
    public const string TokenRevoked = "token_revoked";

    /// <summary>
    /// Event type when an access token is refreshed.
    /// </summary>
    public const string TokenRefreshed = "token_refreshed";

    /// <summary>
    /// Event type for a failed authentication attempt.
    /// </summary>
    public const string FailedAuth = "failed_auth";

    /// <summary>
    /// Event type when a permission check is denied.
    /// </summary>
    public const string PermissionDenied = "permission_denied";

    /// <summary>
    /// Event type when a role is assigned to a subject.
    /// </summary>
    public const string RoleAssigned = "role_assigned";

    /// <summary>
    /// Event type when a role is removed from a subject.
    /// </summary>
    public const string RoleRemoved = "role_removed";

    /// <summary>
    /// Event type when a subject (user) is created.
    /// </summary>
    public const string SubjectCreated = "subject_created";

    /// <summary>
    /// Event type when a subject is updated.
    /// </summary>
    public const string SubjectUpdated = "subject_updated";

    /// <summary>
    /// Event type when a subject is deleted.
    /// </summary>
    public const string SubjectDeleted = "subject_deleted";

    /// <summary>
    /// Event type when an API secret is used.
    /// </summary>
    public const string ApiSecretUsed = "api_secret_used";

    /// <summary>
    /// Event type when the tenant's public share link is shown to an owner in the clear. The link
    /// needs no credential to use, so handing it back is worth a record of who asked and when.
    /// </summary>
    public const string ShareLinkRevealed = "share_link_revealed";

    /// <summary>
    /// Event type when a session has expired.
    /// </summary>
    public const string SessionExpired = "session_expired";

    /// <summary>
    /// Event type when an OIDC identity is linked to a subject.
    /// </summary>
    public const string OidcIdentityLinked = "oidc_identity_linked";

    /// <summary>
    /// Event type when an OIDC identity is unlinked from a subject.
    /// </summary>
    public const string OidcIdentityUnlinked = "oidc_identity_unlinked";

    /// <summary>
    /// Event type when a platform administrator is granted access to a tenant they are not a
    /// member of.
    /// </summary>
    public const string PlatformAdminTenantAccess = "platform_admin_tenant_access";

    /// <summary>
    /// Event type when a platform administrator issues a direct grant to a tenant's member,
    /// as opposed to that member issuing one for themselves.
    /// </summary>
    public const string PlatformAdminGrantIssued = "platform_admin_grant_issued";

    /// <summary>
    /// Event type when a platform administrator revokes a tenant member's direct grant.
    /// </summary>
    public const string PlatformAdminGrantRevoked = "platform_admin_grant_revoked";

    /// <summary>
    /// Event type when a platform administrator mints a single-use login code for a tenant member.
    /// </summary>
    public const string LoginCodeIssued = "login_code_issued";

    /// <summary>
    /// Event type when a login code is exchanged for a session.
    /// </summary>
    public const string LoginHandoff = "login_handoff";

    /// <summary>
    /// Event type when a login code exchange is refused. The row names no subject: the presented
    /// code did not resolve to one.
    /// </summary>
    public const string LoginHandoffFailed = "login_handoff_failed";
}
