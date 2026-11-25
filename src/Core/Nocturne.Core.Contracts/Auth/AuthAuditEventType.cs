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
    /// Event type when editing a role changes its permission set, and so what every holder of
    /// the role can read. The details name the role and the permissions it gained and lost.
    /// </summary>
    public const string RolePermissionsChanged = "role_permissions_changed";

    /// <summary>
    /// Event type when a tenant member's 24-hour history limit is turned on or off. The details name
    /// the limit the member reads under afterwards: <c>24_hours</c> or <c>full</c>.
    /// </summary>
    public const string HistoryLimitChanged = "history_limit_changed";

    /// <summary>
    /// Event type when the date a tenant membership ends is set, changed or cleared. The details name
    /// the end after the change as an ISO 8601 UTC instant, or null when it no longer ends.
    /// </summary>
    public const string AccessEndChanged = "access_end_changed";

    /// <summary>
    /// Event type when a subject becomes a member of a tenant. The details name how under <c>how</c>
    /// and, when known, the subject who let them in under <c>by</c>.
    /// </summary>
    public const string MemberJoined = "member_joined";

    /// <summary>
    /// Event type when a subject stops being a member of a tenant. The details name how under <c>how</c>.
    /// Nothing is written when the membership's end date passes.
    /// </summary>
    public const string MemberRemoved = "member_removed";

    /// <summary>
    /// Event type when removing a member revokes a member invite they made. The target is the member;
    /// the details name the invite under <c>invite</c>.
    /// </summary>
    public const string MemberInviteRevoked = "member_invite_revoked";

    /// <summary>
    /// Event type when a member's access end date set or moved earlier lowers the expiry of a guest
    /// link they made. The target is the member; the details name the link under <c>grant</c> and its
    /// new expiry under <c>expires_at</c> as an ISO 8601 UTC instant.
    /// </summary>
    public const string GuestLinkCapped = "guest_link_capped";

    /// <summary>
    /// Event type when removing a member revokes a guest link they made. The target is the member;
    /// the details name the link under <c>grant</c>.
    /// </summary>
    public const string GuestLinkRevoked = "guest_link_revoked";

    /// <summary>
    /// Event type when a member's access end date set or moved earlier, or their removal while it
    /// ends, lowers the expiry of a device credential they made. The target is the member; the details
    /// name the credential under <c>grant</c> and its new expiry under <c>expires_at</c> as an ISO 8601
    /// UTC instant.
    /// </summary>
    public const string DeviceCredentialCapped = "device_credential_capped";

    /// <summary>
    /// Event type when a member whose access does not end removes the expiry of a device credential.
    /// The target is the member who made it, when it is known; the details name the credential under
    /// <c>grant</c>.
    /// </summary>
    public const string DeviceCredentialKept = "device_credential_kept";

    /// <summary>
    /// Event type when a subject (user) is created.
    /// </summary>
    public const string SubjectCreated = "subject_created";

    /// <summary>
    /// Event type when a subject is updated.
    /// </summary>
    public const string SubjectUpdated = "subject_updated";

    /// <summary>
    /// Event type when an operator confirms a member's email address through the platform-admin API.
    /// The details carry no address.
    /// </summary>
    public const string EmailConfirmedByOperator = "email_confirmed_by_operator";

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

    /// <summary>
    /// Event type when a subject trusts a device at the authenticator step, so its later sign-ins
    /// skip the code.
    /// </summary>
    public const string DeviceTrustGranted = "trusted_device_added";

    /// <summary>
    /// Event type when a subject stops trusting one device, or all of them.
    /// </summary>
    public const string DeviceTrustRevoked = "trusted_device_revoked";

    /// <summary>Event type when a subject creates a team.</summary>
    public const string TeamCreated = "team_created";

    /// <summary>Event type when a manager renames a team. The details hold the old and the new name.</summary>
    public const string TeamRenamed = "team_renamed";

    /// <summary>
    /// Event type when a team is deleted: by a manager, or with the deletion of its one remaining member.
    /// </summary>
    public const string TeamDeleted = "team_deleted";

    /// <summary>Event type when a subject becomes a manager of a team. The target is that subject.</summary>
    public const string TeamManagerAdded = "team_manager_added";

    /// <summary>
    /// Event type when a manager leaves a team or is removed from it. The target is that manager.
    /// </summary>
    public const string TeamManagerRemoved = "team_manager_removed";

    /// <summary>Event type when a manager makes an invite link for a team.</summary>
    public const string TeamInviteCreated = "team_invite_created";

    /// <summary>Event type when a manager revokes a team's invite link.</summary>
    public const string TeamInviteRevoked = "team_invite_revoked";

    /// <summary>Event type when a manager makes a link that makes whoever opens it staff of a team.</summary>
    public const string TeamStaffInviteCreated = "team_staff_invite_created";

    /// <summary>
    /// Event type when a team's staff invite is revoked: by a manager, or because its maker left the team.
    /// </summary>
    public const string TeamStaffInviteRevoked = "team_staff_invite_revoked";

    /// <summary>Event type when a subject becomes staff of a team. The target is that subject.</summary>
    public const string TeamStaffAdded = "team_staff_added";

    /// <summary>
    /// Event type when a manager edits what narrows a staff member. The target is that staff member, and
    /// the details hold the ceiling, limit and end before and after.
    /// </summary>
    public const string TeamStaffChanged = "team_staff_changed";

    /// <summary>
    /// Event type when a staff member leaves a team or is removed from it. The target is that staff member.
    /// </summary>
    public const string TeamStaffRemoved = "team_staff_removed";

    /// <summary>Event type when a manager makes a link that gives the team access to a site.</summary>
    public const string TeamAccessLinkCreated = "team_access_link_created";

    /// <summary>
    /// Event type when a team's access link is revoked: by a manager, or because its maker left the team.
    /// </summary>
    public const string TeamAccessLinkRevoked = "team_access_link_revoked";

    /// <summary>
    /// Event type when an owner of a site gives a team access to it. The row names the tenant and the
    /// team, and no target.
    /// </summary>
    public const string TeamAccessGiven = "team_access_given";

    /// <summary>
    /// Event type when an owner of a site changes what a team's access to it gives: its roles, its 24-hour
    /// limit or its end. The row names the tenant and the team, and no target.
    /// </summary>
    public const string TeamAccessChanged = "team_access_changed";

    /// <summary>
    /// Event type when a team's access to a site stops: the site stopped it, or the team was deleted.
    /// The row names the tenant and the team, and no target.
    /// </summary>
    public const string TeamAccessStopped = "team_access_stopped";

    /// <summary>
    /// Event type when an owner of a site approves a staff member of a team with access to it, or the
    /// switch approves them. The row names the tenant and the team, and targets the staff member.
    /// </summary>
    public const string TeamStaffApproved = "team_staff_approved";

    /// <summary>
    /// Event type when an owner of a site declines a staff member of a team with access to it. The row
    /// names the tenant and the team, and targets the staff member.
    /// </summary>
    public const string TeamStaffDeclined = "team_staff_declined";

    /// <summary>
    /// Event type when an owner of a site turns "the team manages its own staff" on or off for a team
    /// with access to it. The row names the tenant and the team, and no target.
    /// </summary>
    public const string TeamStaffRuleChanged = "team_staff_rule_changed";
}
