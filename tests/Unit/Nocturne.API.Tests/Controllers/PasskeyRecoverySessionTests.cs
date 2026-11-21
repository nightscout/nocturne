using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// The recovery session and the anonymous recovery endpoints over the real pipeline: the fallback
/// authorisation policy, the setup gate and the cookie, which a controller called directly skips.
/// </summary>
/// <remarks>
/// An anonymous caller on a site's own host carries an empty permission trie, so the test host is
/// a private site; recovery mode is a member with no way to sign in. One factory, so one rate
/// limiter: <c>passkey-register</c> allows 20 a minute and <c>login-handoff</c> 10 across the class.
/// </remarks>
public class PasskeyRecoverySessionTests : IClassFixture<PasskeyRecoverySessionTests.Factory>
{
    /// <summary>
    /// The real pipeline with the identity provider and the email delivery stubbed, and the
    /// recovery-link job recorded rather than run.
    /// </summary>
    public sealed class Factory : AuthenticationTestFactory
    {
        public Mock<IOidcAuthService> Oidc { get; } = new();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            base.ConfigureTestServices(services);
            services.AddSingleton(Oidc.Object);
            var delivery = new Mock<IAccountLinkDelivery>();
            delivery.SetupGet(d => d.IsConfigured).Returns(true);
            delivery.Setup(d => d.DeliverAsync(It.IsAny<AccountLinkMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            services.AddSingleton(delivery.Object);
            services.AddSingleton<RecoveryLinkRequests>(sp => new RecordedRequests(sp));
        }
    }

    private sealed class RecordedRequests(IServiceProvider services)
        : RecoveryLinkRequests(services, NullLogger<RecoveryLinkRequests>.Instance)
    {
        public override Task RequestAsync(
            TenantContext tenant, string email, AuthAuditActor actor, string? ipAddress, string? userAgent) =>
            Task.CompletedTask;
    }

    private const string Username = "test";
    private const string RecoveryState = "recovery-state";
    private const string Address = LoginCodeIssuing.Address;

    private readonly Factory _factory;
    private readonly HttpClient _client;
    private readonly Guid _providerId = Guid.CreateVersion7();

    public PasskeyRecoverySessionTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        factory.Oidc.Reset();
        factory.Oidc.Setup(s => s.IsRecoveryState(RecoveryState)).Returns(true);
    }

    public enum Site
    {
        Private,
        InRecoveryMode,
    }

    public static TheoryData<Site> Sites => [Site.Private, Site.InRecoveryMode];

    #region The session

    [Fact]
    public async Task A_spent_recovery_code_yields_a_session_that_can_enrol_a_passkey()
    {
        var cookie = await SpendARecoveryCodeAsync();

        var response = await PostAsync("/api/auth/passkey/register/options", new { username = Username }, cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("challengeToken").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_recovery_cookie_is_http_only_lax_host_only_and_time_boxed()
    {
        var attributes = (await VerifyRecoveryCodeAsync()).ToLowerInvariant();

        attributes.Should().Contain("httponly").And.Contain("samesite=lax").And.NotContain("domain=");
        // The window a session stays usable in; widening it silently is the failure this pins.
        attributes.Should().Contain("max-age=600");
    }

    [Fact]
    public async Task An_expired_recovery_session_cannot_enrol_a_passkey()
    {
        var session = await IssueAsync(TestDatabaseSeeder.TestSubjectId, LoginCodePurpose.RecoverySession);
        await using (var db = await TenantDbAsync())
        {
            await db.LoginCodes.Where(c => c.Purpose == LoginCodePurpose.RecoverySession)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        var response = await PostAsync("/api/auth/passkey/register/options", new { username = Username }, session);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<LoginCodePurpose> NotSessions =>
        [LoginCodePurpose.Recovery, LoginCodePurpose.OperatorRecovery, LoginCodePurpose.EmailRecovery,
            LoginCodePurpose.EmailConfirm, LoginCodePurpose.Login];

    [Theory]
    [MemberData(nameof(NotSessions))]
    public async Task Another_purposes_code_as_the_cookie_cannot_enrol_a_passkey(LoginCodePurpose purpose)
    {
        var code = await IssueAsync(TestDatabaseSeeder.TestSubjectId, purpose);

        var response = await PostAsync("/api/auth/passkey/register/options", new { username = Username }, code);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_forged_recovery_session_cannot_enrol_a_passkey()
    {
        var response = await PostAsync("/api/auth/passkey/register/options", new { username = Username }, "not-a-code");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task No_recovery_session_cannot_enrol_a_passkey()
    {
        var response = await PostAsync("/api/auth/passkey/register/options", new { username = Username }, null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_recovery_session_reaches_nothing_but_enrolment()
    {
        var cookie = await SpendARecoveryCodeAsync();

        // Passkey management on the subject's own account, one route over from the enrolment the
        // recovery session does authorise: it needs a session, which a recovery code never grants.
        var list = new HttpRequestMessage(HttpMethod.Get, "/api/auth/passkey/credentials");
        list.Headers.Add("Cookie", $"{RecoverySession.CookieName}={cookie}");
        (await _client.SendAsync(list)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var regenerate = await PostAsync("/api/auth/passkey/recovery/regenerate", new { }, cookie);
        regenerate.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region The anonymous recovery endpoints are admitted on a private site and in recovery mode

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task The_provider_callback_under_a_session_reaches_the_action_and_hands_over_to_the_ordinary_sign_in(Site site)
    {
        await using var _ = await ArrangeAsync(site);
        var session = await IssueAsync(TestDatabaseSeeder.TestSubjectId, LoginCodePurpose.RecoverySession);
        _factory.Oidc
            .Setup(s => s.HandleRecoveryLinkCallbackAsync("code", RecoveryState, RecoveryState, TestDatabaseSeeder.TestSubjectId))
            .ReturnsAsync(OidcLinkResult.Succeeded(Guid.CreateVersion7(), "/", _providerId));

        var response = await CallbackAsync(session);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith($"/api/auth/oidc/login?provider={_providerId}");
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task The_provider_callback_without_a_session_attaches_nothing(Site site)
    {
        await using var _ = await ArrangeAsync(site);

        var response = await CallbackAsync(session: null);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("/auth/error");
        _factory.Oidc.Verify(s => s.HandleRecoveryLinkCallbackAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task The_provider_link_start_is_admitted(Site site)
    {
        await using var _ = await ArrangeAsync(site);
        var session = await IssueAsync(TestDatabaseSeeder.TestSubjectId, LoginCodePurpose.RecoverySession);
        _factory.Oidc
            .Setup(s => s.GenerateRecoveryLinkAuthorizationUrlAsync(
                _providerId, TestDatabaseSeeder.TestSubjectId, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new OidcAuthorizationRequest
            {
                AuthorizationUrl = "https://idp.example/auth",
                State = RecoveryState,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                ProviderId = _providerId,
            });

        var response = await PostAsync("/api/auth/oidc/recovery/link", new { providerId = _providerId }, session);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()
            .Should().Be("https://idp.example/auth");
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task Asking_for_a_recovery_link_is_admitted(Site site)
    {
        await using var _ = await ArrangeAsync(site);

        var response = await PostAsync("/api/auth/passkey/recovery-link", new { email = Address }, null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task Redeeming_an_emailed_recovery_link_is_admitted_and_starts_a_session(Site site)
    {
        await using var arranged = await ArrangeAsync(site);
        var code = await IssueAsync(arranged.EmailMemberId, LoginCodePurpose.EmailRecovery);

        var response = await PostAsync("/api/auth/passkey/recovery-mode/redeem", new { code }, null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie")
            .Should().Contain(h => h.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task Redeeming_a_confirmation_link_is_admitted(Site site)
    {
        await using var arranged = await ArrangeAsync(site, confirmed: false);
        var code = await IssueAsync(arranged.EmailMemberId, LoginCodePurpose.EmailConfirm);

        var response = await PostAsync("/api/auth/email-confirmation/redeem", new { code }, null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    #endregion

    /// <summary>
    /// A member holding <see cref="Address"/>, confirmed unless told otherwise. On a private site it
    /// holds a passkey; in recovery mode it is the member who cannot sign in.
    /// </summary>
    private async Task<Arranged> ArrangeAsync(Site site, bool confirmed = true)
    {
        var memberId = Guid.CreateVersion7();
        await using var db = await TenantDbAsync();
        var subject = new SubjectEntity { Id = memberId, Name = "Recovering", Username = "recovering", IsActive = true };
        subject.SetEmail(Address);
        if (confirmed)
            subject.ConfirmEmail(Address, DateTime.UtcNow);
        db.Subjects.Add(subject);
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TestDatabaseSeeder.TenantId, SubjectId = memberId,
        });
        if (site == Site.Private)
        {
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = memberId, CredentialId = memberId.ToByteArray(), PublicKey = [1],
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return new Arranged(this, memberId);
    }

    private sealed class Arranged(PasskeyRecoverySessionTests tests, Guid emailMemberId) : IAsyncDisposable
    {
        public Guid EmailMemberId => emailMemberId;

        public async ValueTask DisposeAsync()
        {
            await using var db = await tests.TenantDbAsync();
            await db.LoginCodes.Where(c => c.SubjectId == emailMemberId).ExecuteDeleteAsync();
            await db.PasskeyCredentials.Where(c => c.SubjectId == emailMemberId).ExecuteDeleteAsync();
            await db.TenantMembers.Where(m => m.SubjectId == emailMemberId).ExecuteDeleteAsync();
            await db.Subjects.Where(s => s.Id == emailMemberId).ExecuteDeleteAsync();
        }
    }

    private async Task<NocturneDbContext> TenantDbAsync()
    {
        var db = await _factory.Services.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        db.TenantId = TestDatabaseSeeder.TenantId;
        return db;
    }

    private async Task<string> IssueAsync(Guid subjectId, LoginCodePurpose purpose)
    {
        using var scope = _factory.Services.CreateScope();
        await using var db = await TenantDbAsync();
        return await scope.ServiceProvider.GetRequiredService<ILoginCodeService>().IssueAnyAsync(db, subjectId, purpose);
    }

    private Task<HttpResponseMessage> CallbackAsync(string? session)
    {
        var linkState = _factory.Services.GetRequiredService<IOptions<OidcOptions>>().Value.Cookie.LinkStateCookieName;
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/auth/oidc/link/callback?code=code&state={RecoveryState}");
        request.Headers.Add("Cookie", session is null
            ? $"{linkState}={RecoveryState}"
            : $"{linkState}={RecoveryState}; {RecoverySession.CookieName}={session}");
        return _client.SendAsync(request);
    }

    /// <summary>Spends a freshly issued recovery code and returns the recovery session's code.</summary>
    private async Task<string> SpendARecoveryCodeAsync()
    {
        var setCookie = await VerifyRecoveryCodeAsync();
        return setCookie[(setCookie.IndexOf('=') + 1)..setCookie.IndexOf(';')];
    }

    /// <summary>Spends a freshly issued recovery code and returns the recovery Set-Cookie header.</summary>
    private async Task<string> VerifyRecoveryCodeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var recoveryCodes = scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>();
        var codes = await recoveryCodes.GenerateCodesAsync(TestDatabaseSeeder.TestSubjectId);

        var response = await PostAsync(
            "/api/auth/passkey/recovery/verify", new { username = Username, code = codes[0] }, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return response.Headers.GetValues("Set-Cookie")
            .Single(header => header.StartsWith(RecoverySession.CookieName, StringComparison.Ordinal));
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body, string? session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (session != null)
            request.Headers.Add("Cookie", $"{RecoverySession.CookieName}={session}");
        return _client.SendAsync(request);
    }
}
