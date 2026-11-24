using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Finishes a sign-in once a method has proved who the subject is: admission, the second-factor
/// gate, the session, last-login and the <see cref="AuthAuditEventType.Login"/> row, in that order.
/// It answers with a <see cref="SignInOutcome"/> and writes no cookie; the caller hands the outcome
/// to <c>SignInOutcomeExtensions.ApplySignInAsync</c>.
/// </summary>
/// <remarks>
/// Admission runs before the gate so a non-member is never minted a step-up token, and is answered
/// as a non-member whatever factors they hold.
/// <para>
/// Every step after the session can fail and nothing here catches it: the caller gets no outcome,
/// so no cookie is written, and the refresh token the session stored is never delivered.
/// </para>
/// </remarks>
public sealed class SignInCompletion(
    ITotpService totp,
    ITenantMemberService members,
    ISessionService sessions,
    ISubjectService subjects,
    IAuthAuditService audit)
{
    public async Task<SignInOutcome> CompleteAsync(SignInAttempt attempt, CancellationToken ct = default)
    {
        // Built before any step so a method or enrolment with no row name throws before any write.
        var details = MethodDetails(attempt);

        if (!await IsAdmittedAsync(attempt.Admission, attempt.SubjectId, ct))
            return await RefuseAsync(attempt, SignInRefusal.NotAMember, details);

        var gate = attempt.SecondFactor switch
        {
            SecondFactorProof.CodeVerified => SecondFactorRequirement.NotEnrolled,
            SecondFactorProof.Primary primary =>
                await totp.GetSecondFactorRequirementAsync(attempt.SubjectId, primary.TrustedDeviceToken),
            _ => SecondFactorRequirement.CodeRequired,
        };

        if (gate == SecondFactorRequirement.CodeRequired)
        {
            details["secondFactorPending"] = true;
            await WriteAuditAsync(AuthAuditEventType.Login, attempt, success: false, details);
            var (tenantId, admittedByInvite) = StepUpBinding(attempt.Admission);
            var stepUpToken = await totp.CreateStepUpTokenAsync(attempt.SubjectId, tenantId, admittedByInvite);
            return new SignInOutcome.SecondFactorRequired(
                attempt.SubjectId, stepUpToken, attempt.Method, attempt.ProviderId);
        }

        var session = await sessions.IssueSessionAsync(attempt.SubjectId, attempt.Session, ct);

        await subjects.UpdateLastLoginAsync(attempt.SubjectId);

        var trustedDevice = gate == SecondFactorRequirement.SatisfiedByTrustedDevice;
        if (trustedDevice)
            details["secondFactor"] = "trusted_device";
        await WriteAuditAsync(AuthAuditEventType.Login, attempt, success: true, details);

        return new SignInOutcome.SignedIn(session, trustedDevice, attempt.Method, attempt.ProviderId);
    }

    private async Task<bool> IsAdmittedAsync(SignInAdmission admission, Guid subjectId, CancellationToken ct) =>
        admission switch
        {
            SignInAdmission.Member member => await members.IsMemberAsync(subjectId, member.TenantId, ct),
            SignInAdmission.Invite or SignInAdmission.Account => true,
            _ => false,
        };

    private static (Guid? TenantId, bool AdmittedByInvite) StepUpBinding(SignInAdmission admission) =>
        admission switch
        {
            SignInAdmission.Member member => (member.TenantId, false),
            SignInAdmission.Invite invite => (invite.TenantId, true),
            _ => (null, false),
        };

    private async Task<SignInOutcome> RefuseAsync(SignInAttempt attempt, SignInRefusal reason, JsonObject details)
    {
        if (attempt.RefusalAudit is RefusalAudit.MethodRow)
            return new SignInOutcome.Refused(reason);

        var added = attempt.RefusalAudit as RefusalAudit.ModuleRow;
        var reasonName = ReasonName(reason);
        details["reason"] = reasonName;
        foreach (var (name, value) in added?.Details ?? [])
            details.Add(name, JsonSerializer.SerializeToNode(value));

        await audit.LogAsync(AuthAuditEventType.FailedAuth, attempt.SubjectId, success: false,
            ipAddress: attempt.Session.IpAddress,
            userAgent: attempt.Session.UserAgent,
            errorMessage: added is { ReasonAsErrorMessage: true } ? reasonName : null,
            detailsJson: details.ToJsonString());
        return new SignInOutcome.Refused(reason);
    }

    private static string ReasonName(SignInRefusal reason) => reason switch
    {
        SignInRefusal.NotAMember => "not_a_member",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>
    /// The method, and the enrolment it completes when it is one, that every row of the sign-in
    /// starts with.
    /// </summary>
    private static JsonObject MethodDetails(SignInAttempt attempt)
    {
        var details = new JsonObject { ["method"] = MethodName(attempt.Method) };
        if (attempt.Enrolment is { } enrolment)
            details["enrolment"] = EnrolmentName(enrolment);
        return details;
    }

    private static string MethodName(SignInMethod method) => method switch
    {
        SignInMethod.Passkey => "passkey",
        SignInMethod.Oidc => "oidc",
        SignInMethod.Totp => "totp",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    private static string EnrolmentName(SignInEnrolment enrolment) => enrolment switch
    {
        SignInEnrolment.Setup => "setup",
        SignInEnrolment.Invite => "invite",
        SignInEnrolment.TenantInvite => "tenant_invite",
        _ => throw new ArgumentOutOfRangeException(nameof(enrolment), enrolment, null),
    };

    private Task WriteAuditAsync(string eventType, SignInAttempt attempt, bool success, JsonObject details) =>
        audit.LogAsync(eventType, attempt.SubjectId, success,
            ipAddress: attempt.Session.IpAddress,
            userAgent: attempt.Session.UserAgent,
            detailsJson: details.ToJsonString());
}

/// <summary>The method whose proof a sign-in completes.</summary>
public enum SignInMethod
{
    Passkey,
    Oidc,

    /// <summary>The authenticator code that finishes a sign-in a primary factor started.</summary>
    Totp,
}

/// <summary>The enrolment a sign-in completes: the first session of a subject its proof has just
/// given a credential.</summary>
public enum SignInEnrolment
{
    /// <summary>First-run setup: the owner's first passkey or provider identity.</summary>
    Setup,

    /// <summary>A member invite: the invitee's first passkey, on the inviting tenant.</summary>
    Invite,

    /// <summary>A tenant creation link: the new owner's first passkey.</summary>
    TenantInvite,
}

/// <summary>What admits the proved subject to the host the sign-in runs on.</summary>
public abstract record SignInAdmission
{
    private SignInAdmission() { }

    /// <summary>Admitted while the subject is a member of <paramref name="TenantId"/>.</summary>
    public sealed record Member(Guid TenantId) : SignInAdmission;

    /// <summary>
    /// The proof admitted a non-member on a valid invite. A step-up token carries that decision to
    /// the code step.
    /// </summary>
    public sealed record Invite(Guid? TenantId) : SignInAdmission;

    /// <summary>
    /// The proof was bound to no tenant: a host that resolves none, or a flow that creates the
    /// membership itself. There is no membership to check.
    /// </summary>
    public sealed record Account : SignInAdmission;
}

/// <summary>What the method proved towards the second factor.</summary>
public abstract record SecondFactorProof
{
    private SecondFactorProof() { }

    /// <summary>
    /// Only a primary factor. The gate decides whether a code is still owed, and whether the
    /// trusted-device cookie the request carried stands in for it.
    /// </summary>
    public sealed record Primary(string? TrustedDeviceToken) : SecondFactorProof;

    /// <summary>The method was the code itself, which <paramref name="Verification"/> accepted.</summary>
    public sealed record CodeVerified(TotpLoginResult Verification) : SecondFactorProof;
}

/// <summary>A proved subject on its way to a session.</summary>
/// <param name="SubjectId">The subject the method proved.</param>
/// <param name="Method">The method that proved it.</param>
/// <param name="Admission">What admits the subject here.</param>
/// <param name="SecondFactor">What the method proved towards the second factor.</param>
/// <param name="Session">The session's device description and the request's address and agent,
/// which the audit rows record too.</param>
/// <param name="ProviderId">The identity provider, for <see cref="SignInMethod.Oidc"/>.</param>
/// <param name="RefusalAudit">How a refusal is recorded; null records the module's row with
/// nothing added.</param>
/// <param name="Enrolment">The enrolment the sign-in completes, which every row names; null for a
/// sign-in with a credential the subject already held.</param>
public sealed record SignInAttempt(
    Guid SubjectId,
    SignInMethod Method,
    SignInAdmission Admission,
    SecondFactorProof SecondFactor,
    SessionContext Session,
    Guid? ProviderId = null,
    RefusalAudit? RefusalAudit = null,
    SignInEnrolment? Enrolment = null);

/// <summary>How the <see cref="AuthAuditEventType.FailedAuth"/> row of a refused sign-in is recorded.</summary>
public abstract record RefusalAudit
{
    private RefusalAudit() { }

    /// <summary>
    /// The module records the row: the subject, the method and the <see cref="SignInRefusal"/>,
    /// whatever is added.
    /// </summary>
    /// <param name="ReasonAsErrorMessage">Whether the row's error message is the reason too.</param>
    /// <param name="Details">Recorded after the method and the reason, in this order.</param>
    public sealed record ModuleRow(bool ReasonAsErrorMessage, IReadOnlyList<KeyValuePair<string, object?>> Details)
        : RefusalAudit;

    /// <summary>
    /// The method records the row itself, in the shape its proof's own refusals take, and the
    /// module records none.
    /// </summary>
    public sealed record MethodRow : RefusalAudit;
}

/// <summary>Why a proved subject was not signed in.</summary>
public enum SignInRefusal
{
    NotAMember,
}

/// <summary>How a sign-in ended.</summary>
public abstract record SignInOutcome
{
    private SignInOutcome() { }

    /// <param name="Session">The session issued.</param>
    /// <param name="TrustedDevice">Whether a trusted device stood in for the code.</param>
    /// <param name="Method">The method that proved the subject, which names the sign-in hint.</param>
    /// <param name="ProviderId">The identity provider, for <see cref="SignInMethod.Oidc"/>.</param>
    public sealed record SignedIn(SessionTokenPair Session, bool TrustedDevice, SignInMethod Method, Guid? ProviderId)
        : SignInOutcome;

    /// <summary>No session: the subject finishes at the code step with <paramref name="StepUpToken"/>.</summary>
    public sealed record SecondFactorRequired(
        Guid SubjectId, string StepUpToken, SignInMethod Method, Guid? ProviderId) : SignInOutcome;

    public sealed record Refused(SignInRefusal Reason) : SignInOutcome;
}
