using System.Text.Json;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The steps every sign-in takes once its method has proved the subject: admission, the
/// second-factor gate, the session, last-login and the audit row, and the cookies the outcome
/// calls for.
/// </summary>
[Trait("Category", "Unit")]
public class SignInCompletionTests
{
    private const string Ip = "203.0.113.9";
    private const string Agent = "Firefox";

    private readonly Mock<ITotpService> _totp = new();
    private readonly Mock<ITenantMemberService> _members = new();
    private readonly Mock<ISessionService> _sessions = new();
    private readonly Mock<ISubjectService> _subjects = new();
    private readonly Mock<IAuthAuditService> _audit = new();
    private readonly List<string> _steps = [];
    private readonly List<(string EventType, Guid? Subject, bool Success, string? Ip, string? Agent, JsonElement Details)> _rows = [];

    private readonly Guid _subjectId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly SessionTokenPair _session;

    public SignInCompletionTests()
    {
        _session = new SessionTokenPair("access", "refresh", 900, _subjectId);

        _members.Setup(m => m.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("admission"))
            .ReturnsAsync(true);
        _totp.Setup(t => t.GetSecondFactorRequirementAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
            .Callback(() => _steps.Add("gate"))
            .ReturnsAsync(SecondFactorRequirement.NotEnrolled);
        _totp.Setup(t => t.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()))
            .Callback(() => _steps.Add("step-up"))
            .ReturnsAsync("step-up-token");
        _sessions.Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("session"))
            .ReturnsAsync(_session);
        _subjects.Setup(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()))
            .Callback(() => _steps.Add("last-login"))
            .Returns(Task.CompletedTask);
        _audit.Setup(a => a.LogAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
            .Callback((string type, Guid? subject, bool success, string? ip, string? agent, string? _, string? details,
                Guid? _, AuthAuditActor? _, Guid? _) =>
            {
                _steps.Add($"audit:{type}");
                _rows.Add((type, subject, success, ip, agent, JsonDocument.Parse(details!).RootElement.Clone()));
            })
            .Returns(Task.CompletedTask);
    }

    private SignInCompletion Completion() =>
        new(_totp.Object, _members.Object, _sessions.Object, _subjects.Object, _audit.Object);

    private SignInAttempt Attempt(
        SignInMethod method = SignInMethod.Passkey,
        SignInAdmission? admission = null,
        SecondFactorProof? secondFactor = null,
        Guid? providerId = null,
        SignInEnrolment? enrolment = null) =>
        new(_subjectId, method,
            admission ?? new SignInAdmission.Member(_tenantId),
            secondFactor ?? new SecondFactorProof.Primary("device-cookie"),
            new SessionContext(DeviceDescription: "Passkey", IpAddress: Ip, UserAgent: Agent),
            providerId,
            Enrolment: enrolment);

    private void GateAnswers(SecondFactorRequirement requirement) =>
        _totp.Setup(t => t.GetSecondFactorRequirementAsync(_subjectId, "device-cookie"))
            .Callback(() => _steps.Add("gate"))
            .ReturnsAsync(requirement);

    private void NotAMember() =>
        _members.Setup(m => m.IsMemberAsync(_subjectId, _tenantId, It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("admission"))
            .ReturnsAsync(false);

    [Fact]
    public async Task A_member_without_an_authenticator_is_admitted_gated_given_a_session_then_last_login_then_the_login_row()
    {
        var outcome = await Completion().CompleteAsync(Attempt());

        outcome.Should().BeEquivalentTo(new SignInOutcome.SignedIn(_session, false, SignInMethod.Passkey, null));
        _steps.Should().Equal("admission", "gate", "session", "last-login", "audit:login");
        var expected = new SessionContext(DeviceDescription: "Passkey", IpAddress: Ip, UserAgent: Agent);
        _sessions.Verify(s => s.IssueSessionAsync(_subjectId, expected, It.IsAny<CancellationToken>()));

        var row = _rows.Single();
        row.Should().Match<(string EventType, Guid? Subject, bool Success, string? Ip, string? Agent, JsonElement Details)>(
            r => r.EventType == AuthAuditEventType.Login && r.Subject == _subjectId && r.Success
                && r.Ip == Ip && r.Agent == Agent);
        row.Details.GetRawText().Should().Be("""{"method":"passkey"}""");
    }

    [Fact]
    public async Task A_trusted_device_stands_in_for_the_code_and_the_login_row_says_so()
    {
        GateAnswers(SecondFactorRequirement.SatisfiedByTrustedDevice);

        var outcome = await Completion().CompleteAsync(Attempt(SignInMethod.Oidc));

        outcome.Should().BeOfType<SignInOutcome.SignedIn>().Which.TrustedDevice.Should().BeTrue();
        _rows.Single().Details.GetRawText().Should().Be("""{"method":"oidc","secondFactor":"trusted_device"}""");
    }

    [Fact]
    public async Task A_code_still_owed_writes_the_pending_row_mints_a_step_up_token_and_issues_no_session()
    {
        GateAnswers(SecondFactorRequirement.CodeRequired);
        var providerId = Guid.NewGuid();

        var outcome = await Completion().CompleteAsync(Attempt(SignInMethod.Oidc, providerId: providerId));

        outcome.Should().BeEquivalentTo(new SignInOutcome.SecondFactorRequired(
            _subjectId, "step-up-token", SignInMethod.Oidc, providerId));
        _steps.Should().Equal("admission", "gate", "audit:login", "step-up");
        _totp.Verify(t => t.CreateStepUpTokenAsync(_subjectId, _tenantId, false));

        var row = _rows.Single();
        row.Success.Should().BeFalse("a sign-in waiting for its code is not a completed one");
        row.Details.GetRawText().Should().Be("""{"method":"oidc","secondFactorPending":true}""");
        _subjects.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task An_invitee_is_admitted_without_membership_and_the_step_up_token_carries_the_invite()
    {
        GateAnswers(SecondFactorRequirement.CodeRequired);

        await Completion().CompleteAsync(Attempt(admission: new SignInAdmission.Invite(_tenantId)));

        _members.Verify(m => m.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _totp.Verify(t => t.CreateStepUpTokenAsync(_subjectId, _tenantId, true));
    }

    [Fact]
    public async Task On_a_host_with_no_tenant_the_step_up_token_is_bound_to_no_tenant()
    {
        GateAnswers(SecondFactorRequirement.CodeRequired);

        await Completion().CompleteAsync(Attempt(admission: new SignInAdmission.Account()));

        _members.Verify(m => m.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _totp.Verify(t => t.CreateStepUpTokenAsync(_subjectId, null, false));
    }

    [Fact]
    public async Task An_invitee_on_a_host_with_no_tenant_is_minted_a_step_up_token_bound_to_no_tenant_with_the_invite()
    {
        GateAnswers(SecondFactorRequirement.CodeRequired);

        await Completion().CompleteAsync(Attempt(admission: new SignInAdmission.Invite(TenantId: null)));

        _members.Verify(m => m.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _totp.Verify(t => t.CreateStepUpTokenAsync(_subjectId, null, true));
    }

    [Fact]
    public async Task A_non_member_with_an_authenticator_is_refused_before_the_gate_and_is_minted_no_step_up_token()
    {
        NotAMember();
        GateAnswers(SecondFactorRequirement.CodeRequired);

        var outcome = await Completion().CompleteAsync(Attempt(SignInMethod.Totp));

        outcome.Should().Be(new SignInOutcome.Refused(SignInRefusal.NotAMember));
        _steps.Should().Equal("admission", "audit:failed_auth");
        _totp.Verify(t => t.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Never);
        _sessions.Verify(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()), Times.Never);

        var row = _rows.Single();
        row.Subject.Should().Be(_subjectId);
        row.Success.Should().BeFalse();
        row.Details.GetRawText().Should().Be("""{"method":"totp","reason":"not_a_member"}""");
    }

    [Fact]
    public async Task A_refusal_with_nothing_added_records_no_error_message_and_only_the_method_and_reason()
    {
        NotAMember();

        await Completion().CompleteAsync(Attempt(SignInMethod.Totp));

        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, _subjectId, false, Ip, Agent, null,
            """{"method":"totp","reason":"not_a_member"}""", null, null, null));
    }

    [Fact]
    public async Task A_provider_refusal_records_the_reason_as_the_error_and_the_providers_row_byte_for_byte()
    {
        NotAMember();
        var providerId = Guid.NewGuid();
        var attempt = Attempt(SignInMethod.Oidc, providerId: providerId) with
        {
            RefusalAudit = new RefusalAudit.ModuleRow(ReasonAsErrorMessage: true, [new("providerId", providerId)]),
        };

        var outcome = await Completion().CompleteAsync(attempt);

        outcome.Should().Be(new SignInOutcome.Refused(SignInRefusal.NotAMember));
        var providersRow = Nocturne.API.Controllers.Authentication.OidcController.NotAMemberDetails(
            OidcCallbackResult.NotAMember(_subjectId, providerId, externalIdentityDigest: null));
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, _subjectId, false, Ip, Agent, "not_a_member", providersRow,
            null, null, null));
    }

    [Theory]
    [InlineData("method")]
    [InlineData("reason")]
    public async Task A_refusal_detail_cannot_replace_the_method_or_the_reason(string reserved)
    {
        NotAMember();
        var attempt = Attempt(SignInMethod.Oidc) with
        {
            RefusalAudit = new RefusalAudit.ModuleRow(ReasonAsErrorMessage: false, [new(reserved, "other")]),
        };

        var act = () => Completion().CompleteAsync(attempt);

        await act.Should().ThrowAsync<ArgumentException>();
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()),
            Times.Never);
    }

    [Fact]
    public async Task A_method_that_records_its_own_refusals_is_refused_with_no_row_from_the_module()
    {
        NotAMember();

        var outcome = await Completion().CompleteAsync(Attempt() with { RefusalAudit = new RefusalAudit.MethodRow() });

        outcome.Should().Be(new SignInOutcome.Refused(SignInRefusal.NotAMember));
        _steps.Should().Equal("admission");
        _rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_verified_code_satisfies_the_gate_without_asking_whether_one_is_owed()
    {
        var outcome = await Completion().CompleteAsync(
            Attempt(SignInMethod.Totp, secondFactor: new SecondFactorProof.CodeVerified(
                new TotpLoginResult(_subjectId, "rhys", "Rhys", _tenantId, AdmittedByInvite: false))));

        outcome.Should().BeOfType<SignInOutcome.SignedIn>().Which.TrustedDevice.Should().BeFalse();
        _totp.Verify(t => t.GetSecondFactorRequirementAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        _rows.Single().Details.GetRawText().Should().Be("""{"method":"totp"}""");
    }

    [Fact]
    public async Task A_failed_last_login_write_yields_no_outcome_and_leaves_a_refresh_token_that_is_never_delivered()
    {
        _subjects.Setup(s => s.UpdateLastLoginAsync(_subjectId)).ThrowsAsync(new DbUpdateException("down"));

        var act = () => Completion().CompleteAsync(Attempt());

        await act.Should().ThrowAsync<DbUpdateException>();
        _sessions.Verify(s => s.IssueSessionAsync(_subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _rows.Should().BeEmpty("a sign-in that did not finish is not logged as one");
    }

    [Fact]
    public async Task A_session_sets_the_session_cookies_and_the_sign_in_hint()
    {
        var context = new DefaultHttpContext();

        await context.ApplySignInAsync(
            new SignInOutcome.SignedIn(_session, false, SignInMethod.Passkey, null), new OidcOptions());

        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{new OidcOptions().Cookie.AccessTokenName}=access");
        setCookie.Should().Contain($"{new OidcOptions().Cookie.RefreshTokenName}=refresh");
        setCookie.Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=passkey");
    }

    [Fact]
    public async Task A_provider_sign_in_hint_names_the_provider()
    {
        var context = new DefaultHttpContext();
        var providerId = Guid.NewGuid();

        await context.ApplySignInAsync(
            new SignInOutcome.SecondFactorRequired(_subjectId, "t", SignInMethod.Oidc, providerId), new OidcOptions());

        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=oidc%3A{providerId}");
        setCookie.Should().NotContain(new OidcOptions().Cookie.AccessTokenName, "a code is still owed");
    }

    [Fact]
    public async Task The_code_step_sets_the_session_cookies_and_leaves_the_hint_the_primary_factor_wrote()
    {
        var context = new DefaultHttpContext();

        await context.ApplySignInAsync(
            new SignInOutcome.SignedIn(_session, false, SignInMethod.Totp, null), new OidcOptions());

        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{new OidcOptions().Cookie.AccessTokenName}=access");
        setCookie.Should().NotContain(SessionCookieExtensions.LastSignInCookieName);
    }

    [Fact]
    public async Task A_session_writes_the_same_cookies_as_the_session_writer_given_the_expiry_it_computes()
    {
        var options = new OidcOptions
        {
            Cookie = new CookieSettings
            {
                Secure = true, Path = "/app", SessionDomain = ".nocturne.example.com",
                SameSite = Nocturne.Core.Models.Configuration.SameSiteMode.Strict,
            },
        };
        var providerId = Guid.NewGuid();
        var session = new SessionTokenPair("access", "refresh", 3600, _subjectId);
        var applied = new DefaultHttpContext();
        var direct = new DefaultHttpContext();

        await applied.ApplySignInAsync(new SignInOutcome.SignedIn(session, false, SignInMethod.Oidc, providerId), options);
        direct.Response.SetSessionCookies(
            session.AccessToken, session.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds), options);
        direct.Response.SetLastSignInCookie(SessionCookieExtensions.SignInMethods.Oidc, providerId.ToString(), options);

        var appliedCookies = applied.Response.Headers.SetCookie.ToArray();
        var directCookies = direct.Response.Headers.SetCookie.ToArray();
        appliedCookies.Select(WithoutExpiry).Should().Equal(directCookies.Select(WithoutExpiry));
        appliedCookies.Zip(directCookies, (a, d) => (Expiry(d) - Expiry(a)).Duration())
            .Should().OnlyContain(gap => gap <= TimeSpan.FromSeconds(1), "the cookie's expiry is formatted to the second");
    }

    private static readonly System.Text.RegularExpressions.Regex ExpiresAttribute =
        new("expires=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string WithoutExpiry(string? cookie) => ExpiresAttribute.Replace(cookie!, "expires=");

    private static DateTimeOffset Expiry(string? cookie) =>
        ExpiresAttribute.Match(cookie!) is { Success: true } match
            ? DateTimeOffset.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : DateTimeOffset.MinValue;

    [Theory]
    [InlineData(SignInMethod.Passkey, """{"method":"passkey","enrolment":"setup"}""")]
    [InlineData(SignInMethod.Oidc, """{"method":"oidc","enrolment":"setup"}""")]
    public async Task A_setup_sign_in_writes_the_login_row_of_its_method_with_the_enrolment_it_completes(
        SignInMethod method, string row)
    {
        var outcome = await Completion().CompleteAsync(Attempt(method, enrolment: SignInEnrolment.Setup));

        outcome.Should().BeOfType<SignInOutcome.SignedIn>().Which.Method.Should().Be(method);
        _steps.Should().Equal("admission", "gate", "session", "last-login", "audit:login");
        _audit.Verify(a => a.LogAsync(AuthAuditEventType.Login, _subjectId, true, Ip, Agent, null, row, null, null, null));
    }

    [Fact]
    public async Task A_setup_sign_in_over_a_trusted_device_names_the_enrolment_before_the_second_factor()
    {
        GateAnswers(SecondFactorRequirement.SatisfiedByTrustedDevice);

        await Completion().CompleteAsync(Attempt(SignInMethod.Oidc, enrolment: SignInEnrolment.Setup));

        _rows.Single().Details.GetRawText().Should()
            .Be("""{"method":"oidc","enrolment":"setup","secondFactor":"trusted_device"}""");
    }

    [Fact]
    public async Task A_setup_sign_in_that_owes_a_code_writes_the_pending_row_with_the_enrolment()
    {
        GateAnswers(SecondFactorRequirement.CodeRequired);

        var outcome = await Completion().CompleteAsync(Attempt(SignInMethod.Passkey, enrolment: SignInEnrolment.Setup));

        outcome.Should().BeOfType<SignInOutcome.SecondFactorRequired>();
        _sessions.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _rows.Single().Details.GetRawText().Should()
            .Be("""{"method":"passkey","enrolment":"setup","secondFactorPending":true}""");
    }

    [Fact]
    public async Task A_refused_setup_sign_in_records_the_enrolment_between_the_method_and_the_reason()
    {
        NotAMember();

        var outcome = await Completion().CompleteAsync(Attempt(SignInMethod.Passkey, enrolment: SignInEnrolment.Setup));

        outcome.Should().Be(new SignInOutcome.Refused(SignInRefusal.NotAMember));
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, _subjectId, false, Ip, Agent, null,
            """{"method":"passkey","enrolment":"setup","reason":"not_a_member"}""", null, null, null));
    }

    [Fact]
    public async Task A_setup_by_passkey_sets_the_passkey_sign_in_hint()
    {
        var context = new DefaultHttpContext();

        await context.ApplySignInAsync(
            new SignInOutcome.SignedIn(_session, false, SignInMethod.Passkey, null), new OidcOptions());

        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{new OidcOptions().Cookie.AccessTokenName}=access");
        setCookie.Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=passkey");
    }

    [Fact]
    public async Task A_setup_by_provider_sets_the_sign_in_hint_that_names_the_provider()
    {
        var context = new DefaultHttpContext();
        var providerId = Guid.NewGuid();

        await context.ApplySignInAsync(
            new SignInOutcome.SignedIn(_session, false, SignInMethod.Oidc, providerId), new OidcOptions());

        context.Response.Headers.SetCookie.ToString().Should()
            .Contain($"{SessionCookieExtensions.LastSignInCookieName}=oidc%3A{providerId}");
    }

    [Fact]
    public async Task A_refusal_sets_no_cookie()
    {
        var context = new DefaultHttpContext();

        await context.ApplySignInAsync(new SignInOutcome.Refused(SignInRefusal.NotAMember), new OidcOptions());

        context.Response.Headers.SetCookie.ToString().Should().BeEmpty();
    }

    private static readonly IReadOnlyDictionary<SignInMethod, (string Row, string? Hint)> Methods =
        new Dictionary<SignInMethod, (string, string?)>
        {
            [SignInMethod.Passkey] = ("passkey", "passkey"),
            [SignInMethod.Oidc] = ("oidc", "oidc"),
            [SignInMethod.Totp] = ("totp", null),
        };

    private static readonly IReadOnlyDictionary<SignInEnrolment, string> Enrolments =
        new Dictionary<SignInEnrolment, string>
        {
            [SignInEnrolment.Setup] = "setup",
            [SignInEnrolment.Invite] = "invite",
            [SignInEnrolment.TenantInvite] = "tenant_invite",
        };

    public static TheoryData<SignInMethod> EveryMethod() => new(Enum.GetValues<SignInMethod>());

    public static TheoryData<SignInEnrolment> EveryEnrolment() => new(Enum.GetValues<SignInEnrolment>());

    [Theory]
    [MemberData(nameof(EveryMethod))]
    public async Task Every_method_has_a_row_name_and_a_sign_in_hint_decision(SignInMethod method)
    {
        Methods.Should().ContainKey(method, "a new method needs its row name and its hint decided here");
        var (rowName, hint) = Methods[method];

        await Completion().CompleteAsync(Attempt(method));
        var context = new DefaultHttpContext();
        await context.ApplySignInAsync(new SignInOutcome.SignedIn(_session, false, method, null), new OidcOptions());

        _rows.Single().Details.GetProperty("method").GetString().Should().Be(rowName);
        var setCookie = context.Response.Headers.SetCookie.ToString();
        if (hint is null)
            setCookie.Should().NotContain(SessionCookieExtensions.LastSignInCookieName);
        else
            setCookie.Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}={hint}");
    }

    [Theory]
    [MemberData(nameof(EveryEnrolment))]
    public async Task Every_enrolment_has_a_row_name_and_writes_the_sign_in_hint_of_its_method(SignInEnrolment enrolment)
    {
        Enrolments.Should().ContainKey(enrolment, "a new enrolment needs its row name decided here");
        var context = new DefaultHttpContext();

        var outcome = await Completion().CompleteAsync(Attempt(SignInMethod.Passkey, enrolment: enrolment));
        await context.ApplySignInAsync(outcome, new OidcOptions());

        _rows.Single().Details.GetRawText().Should()
            .Be($$"""{"method":"passkey","enrolment":"{{Enrolments[enrolment]}}"}""");
        context.Response.Headers.SetCookie.ToString().Should()
            .Contain($"{SessionCookieExtensions.LastSignInCookieName}={SessionCookieExtensions.SignInMethods.Passkey}");
    }

    [Fact]
    public async Task A_method_with_no_row_name_throws_before_admission_session_last_login_or_any_row()
    {
        var act = () => Completion().CompleteAsync(Attempt((SignInMethod)99));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _steps.Should().BeEmpty();
    }

    [Fact]
    public async Task An_enrolment_with_no_row_name_throws_before_admission_session_last_login_or_any_row()
    {
        var act = () => Completion().CompleteAsync(Attempt(enrolment: (SignInEnrolment)99));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _steps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_signed_in_method_with_no_hint_decision_throws_before_any_cookie_or_ending_the_handed_off_session()
    {
        var options = new OidcOptions();
        var refreshTokens = new Mock<IRefreshTokenService>();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(refreshTokens.Object)
                .AddSingleton(_audit.Object)
                .BuildServiceProvider(),
        };
        context.Request.Headers.Cookie = $"{options.Cookie.RefreshTokenName}=handed-off";

        var act = () => context.ApplySignInAsync(
            new SignInOutcome.SignedIn(_session, false, (SignInMethod)99, null), options);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        context.Response.Headers.SetCookie.ToString().Should().BeEmpty();
        refreshTokens.VerifyNoOtherCalls();
        _rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_method_with_no_hint_decision_fails_rather_than_writing_no_hint()
    {
        var context = new DefaultHttpContext();

        var act = () => context.ApplySignInAsync(
            new SignInOutcome.SecondFactorRequired(_subjectId, "t", (SignInMethod)99, null), new OidcOptions());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(SignInEnrolment.Invite, """{"method":"passkey","enrolment":"invite"}""")]
    [InlineData(SignInEnrolment.TenantInvite, """{"method":"passkey","enrolment":"tenant_invite"}""")]
    public async Task An_invite_enrolment_is_admitted_gated_and_signed_in_in_the_same_order_with_its_row(
        SignInEnrolment enrolment, string row)
    {
        var outcome = await Completion().CompleteAsync(Attempt(SignInMethod.Passkey, enrolment: enrolment));

        outcome.Should().BeEquivalentTo(new SignInOutcome.SignedIn(_session, false, SignInMethod.Passkey, null));
        _steps.Should().Equal("admission", "gate", "session", "last-login", "audit:login");
        _audit.Verify(a => a.LogAsync(AuthAuditEventType.Login, _subjectId, true, Ip, Agent, null, row, null, null, null));
    }

    [Fact]
    public async Task A_refused_invite_enrolment_names_the_enrolment_in_the_refusal_row()
    {
        NotAMember();

        var outcome = await Completion().CompleteAsync(Attempt(SignInMethod.Passkey, enrolment: SignInEnrolment.Invite));

        outcome.Should().Be(new SignInOutcome.Refused(SignInRefusal.NotAMember));
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, _subjectId, false, Ip, Agent, null,
            """{"method":"passkey","enrolment":"invite","reason":"not_a_member"}""", null, null, null));
    }
}
