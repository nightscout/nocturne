using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Authorization;
using Nocturne.API.Middleware;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;
using Scope = Nocturne.Core.Models.Authorization.Scope;

namespace Nocturne.API.Extensions;

/// <summary>
/// The keys the auth pipeline parks its state under on <see cref="HttpContext.Items"/>.
/// </summary>
/// <remarks>
/// The bag is untyped and the keys were previously spelled out at every read site, so a typo read
/// as "absent" — which every consumer treats as unauthenticated or unscoped. Nothing outside
/// <see cref="HttpContextExtensions"/> should name them.
/// </remarks>
internal static class AuthContextKeys
{
    internal const string AuthContext = "AuthContext";
    internal const string LegacyAuthContext = "AuthenticationContext";
    internal const string GrantedScopes = "GrantedScopes";
    internal const string PermissionTrie = "PermissionTrie";
    internal const string TenantContext = "TenantContext";
    internal const string ShareAccess = "ShareAccess";
}

/// <summary>
/// Typed access to the auth state the middleware pipeline leaves on the request.
/// </summary>
/// <remarks>
/// Two different questions live here and they are NOT interchangeable:
/// <see cref="HasPermission"/> asks the legacy Nightscout <see cref="PermissionTrie"/>, which is
/// rebuilt from the resolved scopes and speaks the <c>api:entries:read</c> vocabulary;
/// <see cref="HasScope"/> asks the granted scope set directly in the <c>glucose.read</c>
/// vocabulary. A caller wanting a tenant permission atom wants <see cref="HasScope"/>.
/// </remarks>
public static class HttpContextExtensions
{
    /// <summary>
    /// Get the new-style authentication context from the request
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Authentication context or null if not set</returns>
    public static AuthContext? GetAuthContext(this HttpContext context)
    {
        return context.Items[AuthContextKeys.AuthContext] as AuthContext;
    }

    /// <summary>
    /// Get the legacy authentication context from the request (for backward compatibility)
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Legacy authentication context</returns>
    public static AuthenticationContext GetLegacyAuthContext(this HttpContext context)
    {
        return context.Items[AuthContextKeys.LegacyAuthContext] as AuthenticationContext
            ?? new AuthenticationContext();
    }

    /// <summary>
    /// Check if the current request has a specific permission
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="permission">Permission to check</param>
    /// <returns>True if permission is granted</returns>
    public static bool HasPermission(this HttpContext context, string permission)
    {
        var permissionTrie = context.Items[AuthContextKeys.PermissionTrie] as PermissionTrie;
        if (permissionTrie == null)
        {
            return false;
        }

        return permissionTrie.Check(permission);
    }

    /// <summary>
    /// Check if the current request is authenticated
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>True if authenticated</returns>
    public static bool IsAuthenticated(this HttpContext context)
    {
        return context.GetAuthContext()?.IsAuthenticated ?? false;
    }

    /// <summary>
    /// Get the subject ID for the current request
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Subject ID or null if not authenticated</returns>
    public static Guid? GetSubjectId(this HttpContext context)
    {
        return context.GetAuthContext()?.SubjectId;
    }

    /// <summary>
    /// Get the subject ID as a string for the current request (legacy compatibility)
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Subject ID as string or null if not authenticated</returns>
    public static string? GetSubjectIdString(this HttpContext context)
    {
        return context.GetAuthContext()?.SubjectId?.ToString();
    }

    /// <summary>
    /// The <c>oauth_grants</c> id the request's credential authenticates under, or null when the
    /// credential has no grant. <see cref="AuthContext.TokenId"/> means "the token row this
    /// credential came from", which is a grant id only for the grant-backed credential types; a
    /// platform-access or session cookie carries a JWT id instead, and a credential that carries
    /// no token id carries none.
    /// </summary>
    public static Guid? GetGrantId(this HttpContext context)
    {
        var authContext = context.GetAuthContext();
        if (authContext is null)
        {
            return null;
        }

        return authContext.AuthType is AuthType.OAuthAccessToken
            or AuthType.ApiKey
            or AuthType.DirectGrant
            or AuthType.Guest
            ? authContext.TokenId
            : null;
    }

    /// <summary>
    /// Check if the current request holds superuser permissions
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>True if the request holds superuser permissions</returns>
    public static bool IsSuperuser(this HttpContext context)
    {
        return context.HasPermission("*");
    }

    /// <summary>
    /// Get the resolved OAuth scopes for the current request.
    /// These are populated by the auth middleware from either OAuth token claims
    /// or translated from legacy Shiro-style permissions.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Set of granted scope strings</returns>
    public static IReadOnlySet<string> GetGrantedScopes(this HttpContext context)
    {
        if (context.Items[AuthContextKeys.GrantedScopes] is IReadOnlySet<string> scopes)
        {
            return scopes;
        }

        return new HashSet<string>();
    }

    /// <summary>
    /// Check if the current request has a specific OAuth scope.
    /// Handles readwrite implying read, and * implying everything.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="scope">The scope to check</param>
    /// <returns>True if the scope is satisfied</returns>
    public static bool HasScope(this HttpContext context, string scope)
    {
        var grantedScopes = context.GetGrantedScopes();
        return Scope.Satisfies(grantedScopes, scope);
    }

    /// <summary>
    /// The authority this request acknowledges an excursion with, for
    /// <see cref="IAlertAcknowledgementService.AcknowledgeExcursionAsync"/>.
    /// </summary>
    public static AlertAcknowledgementAuthority GetAlertAcknowledgementAuthority(this HttpContext context) =>
        new(context.GetSubjectId(), context.GetGrantedScopes());

    /// <summary>
    /// The refusal a history-clamped caller reads when an action would give someone more history
    /// than the caller can see.
    /// </summary>
    public const string HistoryCeilingDetail =
        "You can see the last 24 hours only, so you cannot give anyone more history than that.";

    /// <summary>
    /// Whether the caller may read only the last 24 hours, per
    /// <see cref="ICategoryReadContext.IsHistoryClamped"/>. This is the caller's history ceiling:
    /// whatever the caller mints (an invite, a direct grant, a guest link, an OAuth consent)
    /// inherits it, and a clamped caller may not lift another member's clamp or give a share
    /// full history, because either would hand out more history than the caller can read.
    /// </summary>
    public static bool IsCallerHistoryClamped(this HttpContext context) =>
        context.RequestServices?.GetService<ICategoryReadContext>()?.IsHistoryClamped == true;

    /// <summary>
    /// Clamps every read on the request's services to the last 24 hours, through
    /// <see cref="ICategoryReadContext"/>, which every context of the request and the PHI caches
    /// read. There is no way to lift it.
    /// </summary>
    public static void ClampMemberHistory(this HttpContext context) =>
        context.RequestServices.GetService<ICategoryReadContext>()?.ClampMemberHistory();

    /// <summary>
    /// The history limit something this caller mints carries: the requested limit, or the
    /// caller's own ceiling (<see cref="IsCallerHistoryClamped"/>).
    /// </summary>
    public static bool InheritHistoryClamp(this HttpContext context, bool requested) =>
        requested || context.IsCallerHistoryClamped();

    /// <summary>
    /// The refusal a caller whose own access ends on a date reads when an action would give someone
    /// access that lasts longer.
    /// </summary>
    public const string AccessEndCeilingDetail =
        "Your access ends on a date, so you cannot give anyone access that lasts longer than yours.";

    /// <summary>
    /// The refusal a caller whose own access ends on a date reads on any change to public sharing,
    /// in either direction: a public link carries no end date, so it could outlast the caller.
    /// </summary>
    public const string PublicSharingEndDateDetail =
        "Your access ends on a date, so you cannot change public sharing.";

    /// <summary>
    /// The 403 for a change to public sharing by a caller whose access ends
    /// (<see cref="CallerAccessEndsAt"/>), or null when the caller may go on. Checked before anything is
    /// written or evicted, so a refusal changes nothing.
    /// </summary>
    public static ObjectResult? PublicSharingEndDateRefusal(this ControllerBase controller) =>
        controller.HttpContext.CallerAccessEndsAt() is null
            ? null
            : controller.Problem(detail: PublicSharingEndDateDetail, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// When the caller's own membership of the request's tenant ends, read from the membership the
    /// request authenticated with; null when it does not end or the credential is held to no
    /// membership (the instance key, platform access). This is the caller's access ceiling, as
    /// <see cref="IsCallerHistoryClamped"/> is the history ceiling: a membership the caller creates
    /// inherits it, and the caller may not set an end later than it, or none.
    /// </summary>
    public static DateTime? CallerAccessEndsAt(this HttpContext context) =>
        context.GetAuthContext() is { SubjectId: { } subjectId, TenantId: { } tenantId }
            ? RequestMembership.Of(context, subjectId, tenantId)?.Membership?.Access.AccessEndsAt
            : null;

    /// <summary>
    /// The maker a device credential this caller creates is stamped with, or null when no subject is
    /// authenticated. Its end is the earlier of the caller's own access end
    /// (<see cref="CallerAccessEndsAt"/>) and the expiry of the grant the request presented, so a
    /// credential cannot make one that outlasts itself. A presented grant that a member made for
    /// another subject, a device credential, passes its maker on, so the member stays the one whose
    /// end date and removal reach what it makes.
    /// </summary>
    public static async Task<CredentialMaker?> CredentialMakerAsync(this HttpContext context, CancellationToken ct)
    {
        if (context.GetAuthContext() is not { SubjectId: { } subjectId } auth)
            return null;

        var accessEndsAt = context.CallerAccessEndsAt();
        if (auth is not { TokenId: { } grantId, TenantId: { } tenantId }
            || await context.RequestServices.GetRequiredService<IOAuthGrantRepository>()
                .GetStateAsync(tenantId, grantId, ct) is not { } presented)
            return new CredentialMaker(subjectId, accessEndsAt);

        var endsAt = Earlier(accessEndsAt, presented.ExpiresAt);
        return presented.CreatedBySubjectId is { } maker && maker != subjectId
            ? new CredentialMaker(maker, endsAt)
            : new CredentialMaker(subjectId, endsAt);
    }

    private static DateTime? Earlier(DateTime? first, DateTime? second) =>
        first is { } a && second is { } b ? (a < b ? a : b) : first ?? second;

    /// <summary>
    /// Whether the caller may remove the expiry of a device credential, so it outlasts the member who
    /// set it up: an interactive session (<see cref="RequireInteractiveSessionAttribute"/>) of a caller
    /// who manages the members and whose own access does not end (<see cref="CallerAccessEndsAt"/>).
    /// The keep endpoint and the connections list's <c>CanKeep</c> both read this.
    /// </summary>
    public static bool MayKeepDeviceCredentials(this HttpContext context) =>
        !RequireInteractiveSessionAttribute.Refuses(context)
        && context.HasScope(Scope.MembersManage)
        && context.CallerAccessEndsAt() is null;


    /// <summary>
    /// The refusal a caller whose own access ends reads when they try to keep a device credential.
    /// </summary>
    public const string KeepDeviceCredentialEndDateDetail =
        "Your access ends on a date, so you cannot keep a connection after the date it stops.";

    /// <summary>The end a membership this caller creates carries: the requested one, or the caller's own (<see cref="CallerAccessEndsAt"/>).</summary>
    public static DateTime? InheritAccessEnd(this HttpContext context, DateTime? requested) =>
        requested ?? context.CallerAccessEndsAt();

    /// <summary>
    /// Whether <paramref name="requested"/> would outlast the caller's own access: later than
    /// <see cref="CallerAccessEndsAt"/>, or no end at all, for a caller whose access ends.
    /// </summary>
    public static bool OutlastsCallerAccess(this HttpContext context, DateTime? requested) =>
        Outlasts(context.CallerAccessEndsAt(), requested);

    /// <summary>
    /// Whether <paramref name="end"/> outlasts <paramref name="callerEnd"/>: the caller's access
    /// ends and <paramref name="end"/> is later or none. The one date test behind every ceiling;
    /// a member or invite for which it holds is beyond the caller's reach.
    /// </summary>
    public static bool Outlasts(DateTime? callerEnd, DateTime? end) =>
        callerEnd is { } ceiling && (end is not { } e || e > ceiling);

    /// <summary>The refusal a caller whose access ends reads on a member whose access outlasts the caller's.</summary>
    public const string MemberOutlastsCallerDetail = "This member's access lasts longer than yours.";

    /// <summary>The refusal a caller whose access ends reads on an invite that would give access that outlasts the caller's.</summary>
    public const string InviteOutlastsCallerDetail = "This invite gives access that lasts longer than yours.";

    /// <summary>The refusal a caller whose access ends reads on a role held by a member whose access outlasts the caller's.</summary>
    public const string RoleHeldPastCallerDetail = "A member whose access lasts longer than yours has this role.";

    /// <summary>The refusal a caller whose access ends reads on a role a live invite gives, where the invite's access outlasts the caller's.</summary>
    public const string RoleInvitedPastCallerDetail = "An invite that gives access longer than yours uses this role.";

    /// <summary>
    /// The 403 for a change to a member whose access ends at <paramref name="memberEnd"/>, or null
    /// when the caller may go on (<see cref="OutlastsCallerAccess"/>). Checked before anything is
    /// written or evicted, so a refusal changes nothing.
    /// </summary>
    public static ObjectResult? MemberOutlastsCallerRefusal(this ControllerBase controller, DateTime? memberEnd) =>
        controller.HttpContext.OutlastsCallerAccess(memberEnd)
            ? controller.Problem(detail: MemberOutlastsCallerDetail, statusCode: StatusCodes.Status403Forbidden)
            : null;

    private const string ReaderItemKey = "TenantReader";

    /// <summary>
    /// Records who the request reads as, once authentication and member scope have narrowed it.
    /// Kept in <see cref="HttpContext.Items"/> because SignalR serves a long-polling connection from a
    /// copy of the upgrade request: the copy keeps the items but gets a DI scope of its own that never
    /// ran the middleware.
    /// </summary>
    public static void RecordReader(this HttpContext context) =>
        context.Items[ReaderItemKey] = TenantReader.Of(context.RequestServices);

    /// <summary>The reader <see cref="RecordReader"/> recorded, or null when the request recorded none.</summary>
    public static TenantReader? GetRecordedReader(this HttpContext context) =>
        context.Items.TryGetValue(ReaderItemKey, out var reader) ? reader as TenantReader : null;

    /// <summary>
    /// The tenant the request resolved to, or <see langword="null"/> when it resolved to none.
    /// </summary>
    public static TenantContext? GetTenantContext(this HttpContext context)
    {
        return context.Items[AuthContextKeys.TenantContext] as TenantContext;
    }

    /// <summary>Records the resolved tenant for the rest of the pipeline.</summary>
    public static void SetTenantContext(this HttpContext context, TenantContext tenantContext)
    {
        context.Items[AuthContextKeys.TenantContext] = tenantContext;
    }

    /// <summary>
    /// The legacy permission trie the request resolved to, or <see langword="null"/> when the
    /// credential produced none.
    /// </summary>
    /// <seealso cref="HasPermission"/>
    public static PermissionTrie? GetPermissionTrie(this HttpContext context)
    {
        return context.Items[AuthContextKeys.PermissionTrie] as PermissionTrie;
    }

    /// <summary>Records the resolved permission trie.</summary>
    public static void SetPermissionTrie(this HttpContext context, PermissionTrie trie)
    {
        context.Items[AuthContextKeys.PermissionTrie] = trie;
    }

    /// <summary>Records the resolved granted scopes.</summary>
    public static void SetGrantedScopes(this HttpContext context, IReadOnlySet<string> scopes)
    {
        context.Items[AuthContextKeys.GrantedScopes] = scopes;
    }

    /// <summary>
    /// Records the legacy authentication context v1-v3 read through
    /// <see cref="GetLegacyAuthContext"/>. Written from the same place as the modern one, so the
    /// pair cannot fall out of step.
    /// </summary>
    public static void SetLegacyAuthContext(
        this HttpContext context, AuthenticationContext legacyContext)
    {
        context.Items[AuthContextKeys.LegacyAuthContext] = legacyContext;
    }

    /// <summary>Records the resolved auth context.</summary>
    public static void SetAuthContext(this HttpContext context, AuthContext authContext)
    {
        context.Items[AuthContextKeys.AuthContext] = authContext;
    }

    /// <summary>
    /// Whether the request arrived on a public share link (<c>{token}.share.{domain}</c>), which is
    /// anonymous and category-narrowed rather than a signed-in session.
    /// </summary>
    public static bool IsShareAccess(this HttpContext context)
    {
        return context.Items[AuthContextKeys.ShareAccess] is true;
    }

    /// <summary>Marks the request as arriving on a public share link.</summary>
    public static void SetShareAccess(this HttpContext context)
    {
        context.Items[AuthContextKeys.ShareAccess] = true;
    }
}
