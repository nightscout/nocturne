using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// The recovery session a spent recovery code buys, whoever issued the code: the member, an
/// administrator, the operator, or an emailed link. It admits one enrolment, a passkey or an
/// identity-provider link, and nothing else; no session is issued, so the member then signs in
/// with what they enrolled.
/// </summary>
/// <remarks>
/// The session is a <see cref="LoginCodePurpose.RecoverySession"/> code held in a host-only cookie,
/// so it is bound to the tenant it was started on as every login code is. Only a completed
/// enrolment spends it, inside the claim's transaction (<see cref="EnrolAsync{T}"/>), so two
/// enrolments cannot both land and a refused one leaves the session usable.
/// <para>
/// The cookie is SameSite=Lax rather than Strict because the provider's redirect back to the link
/// callback is a cross-site navigation. Everything it admits that changes state is a POST, which Lax
/// does not send cross-site, except that callback, which the OIDC state cookie also binds.
/// </para>
/// </remarks>
public sealed class RecoverySession(
    INocturneDbContextFactory contexts,
    ILoginCodeService loginCodes,
    ITenantMemberService tenantMembers,
    IOptions<OidcOptions> oidcOptions,
    ILogger<RecoverySession> logger)
{
    /// <summary>The cookie the session's code is held in.</summary>
    public const string CookieName = ".Nocturne.RecoverySession";

    private static readonly IReadOnlyCollection<LoginCodePurpose> Purposes = [LoginCodePurpose.RecoverySession];

    /// <summary>
    /// Starts a session for <paramref name="subjectId"/> on the request's tenant and hands it to the
    /// caller (<see cref="IssueAsync"/>, <see cref="Present"/>).
    /// </summary>
    public async Task StartAsync(HttpContext http, Guid subjectId, CancellationToken ct) =>
        Present(http, await IssueAsync(http, subjectId, ct));

    /// <summary>
    /// Writes a session for <paramref name="subjectId"/> on the request's tenant and returns its
    /// code, without handing it to the caller. A session the member already had is ended
    /// (<see cref="LoginCodeService.SupersededBy"/>). Split from <see cref="Present"/> so a caller
    /// that starts the session inside another claim's transaction hands it over only once that
    /// transaction has committed.
    /// </summary>
    public async Task<string> IssueAsync(HttpContext http, Guid subjectId, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var issued = await loginCodes.IssueAsync(
            db, subjectId, LoginCodePurpose.RecoverySession,
            http.GetClientAddress(), http.Request.Headers.UserAgent.ToString(),
            AuthAuditActor.From(http.GetAuthContext()), ct);
        return issued.Code;
    }

    /// <summary>Hands the session <paramref name="code"/> names to the caller in its cookie.</summary>
    public void Present(HttpContext http, string code)
    {
        var options = CookieOptions();
        options.MaxAge = LoginCodeService.LifetimeOf(LoginCodePurpose.RecoverySession);
        http.Response.Cookies.Append(CookieName, code, options);
    }

    /// <summary>
    /// The member whose live session the request carries, without spending it; otherwise null.
    /// Admits the same members <see cref="EnrolAsync{T}"/> does.
    /// </summary>
    public async Task<Guid?> ReadSubjectAsync(HttpContext http, CancellationToken ct)
    {
        if (CodeOf(http) is not { } code)
            return null;

        await using var db = await contexts.CreateAsync(ct);
        return (await loginCodes.FindLiveAsync(db, code, Purposes, Admits(db), ct))?.SubjectId;
    }

    /// <summary>
    /// Runs <paramref name="enrol"/> for the session's member and spends the session when
    /// <paramref name="enrolled"/> accepts what it returned. Both happen in the claim's transaction:
    /// a refusal or an exception rolls the claim back, so the session stays usable, and a
    /// concurrent second enrolment on the same session finds it spent. A member whose access to the
    /// tenant has ended since the session began finds no session
    /// (<see cref="LoginCodeAdmission.LiveMemberOf"/>). A write
    /// <paramref name="enrol"/> makes through another context commits on its own, so it must not
    /// write anything it would have to undo when it then refuses.
    /// </summary>
    public async Task<RecoveryEnrolment<T>> EnrolAsync<T>(
        HttpContext http,
        Func<Guid, CancellationToken, Task<T>> enrol,
        Func<T, bool> enrolled,
        CancellationToken ct)
    {
        if (CodeOf(http) is not { } code)
            return RecoveryEnrolment<T>.NoSession;

        await using var db = await contexts.CreateAsync(ct);
        var outcome = default(T);
        RedeemedLoginCode? spent;
        try
        {
            spent = await loginCodes.RedeemAsync(
                db, code, Purposes, admits: Admits(db),
                withSpend: async (session, token) =>
                {
                    outcome = await enrol(session.SubjectId, token);
                    if (!enrolled(outcome))
                        throw new EnrolmentRefusedException();
                },
                http.GetClientAddress(), http.Request.Headers.UserAgent.ToString(), ct);
        }
        catch (EnrolmentRefusedException)
        {
            return new RecoveryEnrolment<T>(RecoveryEnrolmentState.Refused, null, outcome);
        }

        if (spent is null)
            return RecoveryEnrolment<T>.NoSession;

        await EndAsync(http, spent.SubjectId, ct);
        return new RecoveryEnrolment<T>(RecoveryEnrolmentState.Enrolled, spent.SubjectId, outcome);
    }

    /// <summary>
    /// Ends any session the request carries and voids the member's outstanding recovery codes
    /// (<see cref="ILoginCodeService.VoidRecoveryCodesAsync"/>), because <paramref name="subjectId"/>
    /// has just gained a way to sign in. The enrolment exists whatever happens here, so a failure to
    /// void is logged rather than thrown.
    /// </summary>
    public async Task EndAsync(HttpContext http, Guid subjectId, CancellationToken ct)
    {
        http.Response.Cookies.Delete(CookieName, CookieOptions());

        try
        {
            await using var db = await contexts.CreateAsync(ct);
            await loginCodes.VoidRecoveryCodesAsync(db, subjectId, ct);
        }
        catch (Exception ex) when (StoreFailure.Is(ex) || ex is InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not void outstanding recovery codes for {SubjectId}", subjectId);
        }
    }

    private Func<RedeemedLoginCode, CancellationToken, Task<bool>> Admits(NocturneDbContext db) =>
        LoginCodeAdmission.LiveMemberOf(tenantMembers, db.TenantId);

    private static string? CodeOf(HttpContext http) =>
        http.Request.Cookies[CookieName] is { Length: > 0 } code ? code : null;

    /// <summary>
    /// A cookie is keyed by name, domain and path, so the write and the expiry that ends it must
    /// present the same ones. Host-only: the session is spent on the host it was started from.
    /// </summary>
    private CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        Secure = oidcOptions.Value.Cookie.Secure,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
    };

    private sealed class EnrolmentRefusedException : Exception;
}

/// <summary>How an attempt to enrol under a recovery session ended.</summary>
public enum RecoveryEnrolmentState
{
    /// <summary>The request carried no live session; nothing was attempted.</summary>
    NoSession,

    /// <summary>The enrolment was refused; the session is still usable.</summary>
    Refused,

    /// <summary>The enrolment landed and the session is spent.</summary>
    Enrolled,
}

/// <summary>An enrolment's outcome, with what the enrolment returned when it ran.</summary>
public sealed record RecoveryEnrolment<T>(RecoveryEnrolmentState State, Guid? SubjectId, T? Result)
{
    internal static readonly RecoveryEnrolment<T> NoSession = new(RecoveryEnrolmentState.NoSession, null, default);
}
