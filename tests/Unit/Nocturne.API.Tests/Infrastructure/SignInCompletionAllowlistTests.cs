using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.API.Services.Auth;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// What finishes a sign-in is written once, in <c>SignInCompletion</c>: last-login, the
/// <see cref="AuthAuditEventType.Login"/> row and the step-up token; and the session cookies are
/// written from its outcome by <c>ApplySignInAsync</c>. The members below are the only others allowed
/// to do one of those, each with its reason. The module trusts three things its caller says: that the
/// attempt's subject is the one its method proved (<see cref="SignInAttempt"/>), that no membership is
/// to be checked (<c>SignInAdmission.Invite</c>, <c>SignInAdmission.Account</c>) and that the code was
/// verified (<c>SecondFactorProof.CodeVerified</c>). So the members that build an attempt or hold the
/// module, and those that construct the others and the verified code result, are pinned too. A new
/// member is added deliberately or not at all.
/// </summary>
/// <remarks>
/// <see cref="SessionIssueAllowlistTests"/> pins who issues the session. The scan is syntactic, so
/// these are not seen: a target-typed <c>new(...)</c>, or a <c>with</c> copy, of a pinned type (an
/// attempt in a member that already holds the module, an admission re-targeted from another); a
/// nested type reached through <c>using static</c> or an alias, which leaves its qualifier unwritten;
/// a <c>Login</c> row whose event type is written as the literal string; a last-login set in an object
/// initializer; a session cookie appended under a name read into a local first, or written as a raw
/// <c>Set-Cookie</c> header (the cookie still needs a token a fenced minter made). An allowlist key
/// names a member, not an overload, so a new overload of a listed member shares its entry.
/// </remarks>
[Trait("Category", "Unit")]
public class SignInCompletionAllowlistTests
{
    private const string Api = "src/API/Nocturne.API/";
    private const string Module = Api + "Services/Auth/SignInCompletion.cs: SignInCompletion.CompleteAsync";
    private const string ModuleReason = "the sign-in completion";

    private const string LastLogin = "LastLoginAt";

    /// <summary>
    /// The members that move last-login: a call of <c>UpdateLastLoginAsync</c>, an edit that names it,
    /// and a write to the property.
    /// </summary>
    private static readonly AllowlistFence LastLoginWriters = new(
        "last-login moves only when a sign-in completes",
        new Dictionary<string, string>
        {
            [Module] = ModuleReason,
            [Api + "Services/Auth/SubjectService.cs: SubjectService.UpdateLastLoginAsync"] =
                "the edit the sign-in completion asks for",
            ["src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SubjectRepository.cs: SubjectRepository.ApplyEditAsync"] =
                "applies a SubjectEdit, which names last-login only in UpdateLastLoginAsync",
            [Api + "Controllers/V4/DevOnly/DevAdminController.cs: DevAdminController.ImportScopedSnapshot"] =
                "dev-only (SessionIssueAllowlistTests): restores a snapshot's subjects as exported",
        },
        source => SourceMentions.Calling(source, [nameof(ISubjectService.UpdateLastLoginAsync)])
            .Concat(!source.Text.Contains(LastLogin, StringComparison.Ordinal) ? [] :
                SourceMentions.Where(source, node => node switch
                {
                    ArgumentSyntax { NameColon.Name.Identifier.ValueText: LastLogin } => true,
                    AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: LastLogin } } => true,
                    _ => false,
                })));

    private static readonly AllowlistFence LoginRowWriters = new(
        "the Login row is written by the sign-in completion",
        new Dictionary<string, string>
        {
            [Module] = ModuleReason + ": the success row, and the pending row when a code is owed",
            [Api + "Controllers/Authentication/PasskeyController.cs: PasskeyController.RecoveryVerify"] =
                "the pending row (success false) of a spent recovery code, which issues no session but a recovery "
                + "session admitting one enrolment; the sign-in with what is enrolled writes the success row",
        },
        source => SourceMentions.Reading(source, nameof(AuthAuditEventType), nameof(AuthAuditEventType.Login)));

    private static readonly AllowlistFence StepUpMinters = new(
        "a step-up token is minted only when the gate owes a code",
        new Dictionary<string, string> { [Module] = ModuleReason },
        source => SourceMentions.Calling(source, [nameof(ITotpService.CreateStepUpTokenAsync)]));

    private const string RefreshesTheSession = "refresh: the renewed pair of a session the caller already holds";
    private const string IssuesItself = "sets the cookies of the session it issues (SessionIssueAllowlistTests)";

    private static readonly AllowlistFence SessionCookieWriters = new(
        "the session cookies of a sign-in are written by ApplySignInAsync",
        new Dictionary<string, string>
        {
            [Api + "Extensions/SignInOutcomeExtensions.cs: SignInOutcomeExtensions.ApplySignInAsync"] =
                "every sign-in the module completes",
            [Api + "Extensions/SessionCookieExtensions.cs: SessionCookieExtensions.SetSessionCookies"] =
                "the token-pair overload, handing to the one writer",
            [Api + "Controllers/Authentication/LoginHandoffController.cs: LoginHandoffController.ExchangeLoginCode"] =
                IssuesItself,
            [Api + "Controllers/V4/Demo/DemoSessionController.cs: DemoSessionController.CreateSession"] = IssuesItself,
            [Api + "Controllers/V4/DevOnly/DevAuthController.cs: DevAuthController.IssueSessionAsync"] = IssuesItself,
            [Api + "Controllers/Authentication/OidcController.cs: OidcController.Refresh"] = RefreshesTheSession,
            [Api + "Controllers/Authentication/OidcController.cs: OidcController.SetSessionCookies"] =
                RefreshesTheSession + "; the helper Refresh calls",
            [Api + "Middleware/Handlers/SessionCookieHandler.cs: SessionCookieHandler.TryRefreshSessionAsync"] =
                RefreshesTheSession + ", silently, when its access token has expired",
            [Api + "Middleware/Handlers/SessionCookieHandler.cs: SessionCookieHandler.SetSessionCookies"] =
                RefreshesTheSession + "; the helper TryRefreshSessionAsync calls",
        },
        source => SourceMentions.Calling(source, ["SetSessionCookies"]));

    private static readonly string[] SessionCookieNames =
        ["AccessTokenName", "RefreshTokenName", "IsAuthenticatedCookieName"];

    private static readonly AllowlistFence SessionCookieAppenders = new(
        "the session cookies are appended by SetSessionCookies alone, so their attributes cannot drift",
        new Dictionary<string, string>
        {
            [Api + "Extensions/SessionCookieExtensions.cs: SessionCookieExtensions.SetSessionCookies"] =
                "the one writer",
        },
        source => !SessionCookieNames.Any(name => source.Text.Contains(name, StringComparison.Ordinal)) ? [] :
            SourceMentions.Where(source, node => node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Append" },
            ArgumentList.Arguments: [var name, ..],
        } && name.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(id => SessionCookieNames.Contains(id.Identifier.ValueText))));

    private static readonly AllowlistFence TrustedClaimBuilders = new(
        "the module checks no membership for Invite or Account and asks no gate for CodeVerified",
        new Dictionary<string, string>
        {
            [Api + "Controllers/Authentication/TotpController.cs: TotpController.Login"] =
                "maps a step-up token VerifyStepUpAsync accepted with the code: the token carries the admission its "
                + "primary factor decided (an invite, a tenant or none) and is redeemed only on its minting host",
            [Api + "Services/Auth/OidcAuthService.cs: OidcAuthService.CompleteLoginAsync"] =
                "the callback's own admission, before the subject is made: a valid join-link invite to the resolved "
                + "tenant, or on a host with no tenant an existing subject or a link that authorises an account",
            [Api + "Controllers/Authentication/PasskeyController.cs: PasskeyController.TenantInviteComplete"] =
                "a creation link whose tenant was refused: the passkey is stored and the account is a member nowhere",
            [Api + "Services/Auth/TotpService.cs: TotpService.VerifyStepUpAsync"] =
                "the result of a code accepted for the subject its step-up token names",
        },
        source => SourceMentions.Constructing(
            source, ["SignInAdmission.Invite", "SignInAdmission.Account", "CodeVerified", nameof(TotpLoginResult)]));

    private const string Passkey = Api + "Controllers/Authentication/PasskeyController.cs: PasskeyController.";
    private const string Totp = Api + "Controllers/Authentication/TotpController.cs: TotpController.";
    private const string Setup = Api + "Controllers/V4/SetupController.cs: SetupController.";
    private const string Oidc = Api + "Services/Auth/OidcAuthService.cs: OidcAuthService.";
    private const string HoldsTheModule = "holds the module for the attempts the members listed beside it build";

    /// <summary>
    /// The members that build a <see cref="SignInAttempt"/>, call a helper that builds one for them,
    /// or hold the module. The module signs in whichever subject the attempt names.
    /// </summary>
    private static readonly AllowlistFence SignInAttempts = new(
        "the module signs in the subject an attempt names; an attempt is built only after its method proved "
        + "that subject",
        new Dictionary<string, string>
        {
            [Passkey + "LoginComplete"] =
                "the subject of a passkey assertion CompleteAssertionAsync verified against its challenge",
            [Passkey + "InviteComplete"] =
                "the subject a passkey was just registered for in the member-invite ceremony, once the invite was "
                + "accepted for it",
            [Passkey + "TenantInviteComplete"] =
                "the subject a passkey was just registered for in the creation-link ceremony, once the link was "
                + "claimed",
            [Totp + "Login"] = "the subject the step-up token names, once VerifyStepUpAsync accepted its code",
            [Oidc + "CompleteLoginAsync"] =
                "the subject linked to the identity in the claims it is handed (HandleCallbackAsync)",
            [Oidc + "HandleCallbackAsync"] =
                "calls CompleteLoginAsync with the claims ValidateCallbackAndParseIdTokenAsync accepted: the state "
                + "cookie, the nonce and the provider's code exchange",
            [Setup + "SignInOwnerAsync"] = "the setup owner its callers proved (OwnerComplete, OidcCallback)",
            [Setup + "OwnerComplete"] =
                "calls SignInOwnerAsync for the subject a passkey was just registered for in the setup-owner "
                + "ceremony, on the sole tenant while it has no owner",
            [Setup + "OidcCallback"] =
                "calls SignInOwnerAsync for the subject HandleSetupCallbackAsync linked, on the sole tenant while it "
                + "has no owner",
            [Passkey + "_signIn"] = HoldsTheModule,
            [Passkey + "PasskeyController"] = HoldsTheModule,
            [Totp + "_signIn"] = HoldsTheModule,
            [Totp + "TotpController"] = HoldsTheModule,
            [Setup + "_signIn"] = HoldsTheModule,
            [Setup + "SetupController"] = HoldsTheModule,
            [Oidc + "_signIn"] = HoldsTheModule,
            [Oidc + "OidcAuthService"] = HoldsTheModule,
            [Api + "Extensions/ServiceRegistrationExtensions.cs: ServiceRegistrationExtensions.AddAuthenticationAndIdentity"] =
                "registers the module",
        },
        source => SourceMentions.Constructing(source, [nameof(SignInAttempt)])
            .Concat(SourceMentions.Naming(source, [nameof(SignInCompletion)]))
            .Concat(SourceMentions.Calling(source, ["SignInOwnerAsync", "CompleteLoginAsync"])));

    private static readonly IReadOnlyDictionary<string, AllowlistFence> Fences =
        new Dictionary<string, AllowlistFence>
        {
            [nameof(LastLoginWriters)] = LastLoginWriters,
            [nameof(LoginRowWriters)] = LoginRowWriters,
            [nameof(StepUpMinters)] = StepUpMinters,
            [nameof(SessionCookieWriters)] = SessionCookieWriters,
            [nameof(SessionCookieAppenders)] = SessionCookieAppenders,
            [nameof(TrustedClaimBuilders)] = TrustedClaimBuilders,
            [nameof(SignInAttempts)] = SignInAttempts,
        };

    public static TheoryData<string> FenceNames => new(Fences.Keys);

    [Theory]
    [MemberData(nameof(FenceNames))]
    public void OnlyTheAllowlistDoesIt(string fence)
    {
        Fences[fence].Unlisted(RepoSources.Src).Should().BeEmpty(
            $"{Fences[fence].Rule}; allowlist a new member in {fence} deliberately, with its reason");
    }

    [Theory]
    [MemberData(nameof(FenceNames))]
    public void EveryAllowlistEntryStillDoesIt(string fence)
    {
        Fences[fence].Stale(RepoSources.Src).Should().BeEmpty(
            "a stale entry would silently cover a future call in that member");
    }

    [Theory]
    [InlineData(
        nameof(LoginRowWriters), "Controllers/Authentication/PasskeyController.cs", "PasskeyController.RecoveryVerify",
        "class PasskeyController { Task RecoveryVerify() => audit.LogAsync(AuthAuditEventType.FailedAuth, s, false); }",
        "class PasskeyController { Task RecoveryVerify() => audit.LogAsync(AuthAuditEventType.Login, s, false); }")]
    [InlineData(
        nameof(SignInAttempts), "Controllers/Authentication/TotpController.cs", "TotpController.Login",
        "class TotpController { Task Login() => Task.CompletedTask; }",
        "class TotpController { Task Login() => _signIn.CompleteAsync(new SignInAttempt(r.SubjectId, m, a, p, c)); }")]
    [InlineData(
        nameof(LastLoginWriters), "Services/Auth/SubjectService.cs", "SubjectService.UpdateLastLoginAsync",
        "class SubjectService { Task UpdateLastLoginAsync(Guid id) => _subjects.UpdateAsync(id, new SubjectEdit(Name: n)); }",
        "class SubjectService { Task UpdateLastLoginAsync(Guid id) => _subjects.UpdateAsync(id, new SubjectEdit(LastLoginAt: now)); }")]
    public void AnEntryWhoseMemberNoLongerDoesItIsStale(string fence, string file, string member, string without, string with)
    {
        var entry = $"{Api}{file}: {member}";

        Fences[fence].Stale([new SourceFile(Api + file, without)]).Should().Contain(entry);
        Fences[fence].Stale([new SourceFile(Api + file, with)]).Should().NotContain(entry);
    }

    [Theory]
    [InlineData(nameof(LastLoginWriters), "class C { Task M(ISubjectService s) => s.UpdateLastLoginAsync(id); }", "C.M")]
    [InlineData(nameof(LastLoginWriters), "class C { Task M(ISubjectService s) => s.GetSubjectByIdAsync(id); }", null)]
    [InlineData(nameof(LoginRowWriters), "class C { Task M(IAuthAuditService a) => a.LogAsync(AuthAuditEventType.Login, s, true); }", "C.M")]
    [InlineData(nameof(LoginRowWriters), "class C { string T = Nocturne.Core.Contracts.Auth.AuthAuditEventType.Login; }", "C.T")]
    [InlineData(nameof(LoginRowWriters), "class C { Task M(IAuthAuditService a) => a.LogAsync(AuthAuditEventType.Logout, s, true); }", null)]
    [InlineData(nameof(LoginRowWriters), "class C { IActionResult M() => Login(); }", null)]
    [InlineData(nameof(StepUpMinters), "class C { Task M(ITotpService t) => t.CreateStepUpTokenAsync(s, null, false); }", "C.M")]
    [InlineData(nameof(SessionCookieWriters), "class C { void M() => Response.SetSessionCookies(session, options); }", "C.M")]
    [InlineData(nameof(SessionCookieWriters), "class C { void M() => Response.SetLastSignInCookie(\"passkey\", null, options); }", null)]
    [InlineData(nameof(SessionCookieAppenders), "class C { void M() => Response.Cookies.Append(o.Cookie.AccessTokenName, t, c); }", "C.M")]
    [InlineData(nameof(SessionCookieAppenders), "class C { void M() => Response.Cookies.Append(SessionCookieExtensions.IsAuthenticatedCookieName, \"true\", c); }", "C.M")]
    [InlineData(nameof(SessionCookieAppenders), "class C { void M() => Response.Cookies.Append(RecoverySession.CookieName, code, c); }", null)]
    [InlineData(nameof(SessionCookieAppenders), "class C { void M() => Response.Cookies.Delete(o.Cookie.RefreshTokenName, c); }", null)]
    [InlineData(nameof(TrustedClaimBuilders), "class C { object M() => new SignInAdmission.Account(); }", "C.M")]
    [InlineData(nameof(TrustedClaimBuilders), "class C { object M() => new Nocturne.API.Services.Auth.SignInAdmission.Invite(t); }", "C.M")]
    [InlineData(nameof(TrustedClaimBuilders), "class C { object M() => new SecondFactorProof.CodeVerified(r); }", "C.M")]
    [InlineData(nameof(TrustedClaimBuilders), "class C { object M() => new TotpLoginResult(s, t, false); }", "C.M")]
    [InlineData(nameof(TrustedClaimBuilders), "class C { object M() => new SignInAdmission.Member(t); }", null)]
    [InlineData(nameof(TrustedClaimBuilders), "class C { object M() => new EnrolmentMatch.Invite(u); }", null)]
    [InlineData(nameof(TrustedClaimBuilders), "class C { object M() => new SecondFactorProof.Primary(d); }", null)]
    [InlineData(nameof(LastLoginWriters), "class C { Task M(ISubjectRepository r) => r.UpdateAsync(id, new SubjectEdit(LastLoginAt: now)); }", "C.M")]
    [InlineData(nameof(LastLoginWriters), "class C { void M(SubjectEntity s) => s.LastLoginAt = now; }", "C.M")]
    [InlineData(nameof(LastLoginWriters), "class C { DateTime? M(SubjectEntity s) => s.LastLoginAt; }", null)]
    [InlineData(nameof(SignInAttempts), "class C { Task M() => _signIn.CompleteAsync(new SignInAttempt(id, k, a, p, c)); }", "C.M")]
    [InlineData(nameof(SignInAttempts), "class C { private readonly SignInCompletion _signIn; }", "C._signIn")]
    [InlineData(nameof(SignInAttempts), "class C { C(SignInCompletion signIn) { } }", "C.C")]
    [InlineData(nameof(SignInAttempts), "class C { Task M() => SignInOwnerAsync(id, t, k, d); }", "C.M")]
    [InlineData(nameof(SignInAttempts), "class C { Task M() => CompleteLoginAsync(s, p, c, t, i, u, d); }", "C.M")]
    [InlineData(nameof(SignInAttempts), "class C { Task M(SignInOutcome o) => Apply(o); }", null)]
    public void FindsTheMemberDoingIt(string fence, string source, string? expected) =>
        Fences[fence].FoundIn([new SourceFile("src/X.cs", source)])
            .Should().Equal(expected is null ? [] : [$"src/X.cs: {expected}"]);

    [Theory]
    [InlineData(nameof(LastLoginWriters), "class Elsewhere { Task M(ISubjectService s) => s.UpdateLastLoginAsync(id); }")]
    [InlineData(nameof(SignInAttempts), "class Elsewhere { Task M(SignInCompletion m) => m.CompleteAsync(new SignInAttempt(id, k, a, p, c)); }")]
    [InlineData(nameof(SignInAttempts), "class Elsewhere { Task M(IServiceProvider p) => p.GetRequiredService<SignInCompletion>().CompleteAsync(new(id, k, a, p, c)); }")]
    public void AnUnlistedMemberIsReported(string fence, string source)
    {
        var elsewhere = new SourceFile(Api + "Controllers/V4/Elsewhere.cs", source);

        Fences[fence].Unlisted([elsewhere]).Should().Equal(Api + "Controllers/V4/Elsewhere.cs: Elsewhere.M");
    }
}
