using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// The recovery-code path over the real pipeline: spending a code has to leave the caller able to
/// enrol a replacement passkey, and able to do nothing else.
/// </summary>
/// <remarks>
/// One factory, so one rate limiter, and the test server presents no client address — every test
/// here draws from the same <c>passkey-recovery</c> bucket (10 per 10 minutes). Adding recovery
/// verifications in bulk trips a 429 on whichever test happens to run last.
/// </remarks>
public class PasskeyRecoverySessionTests : IClassFixture<AuthenticationTestFactory>
{
    private const string RecoveryCookieName = ".Nocturne.RecoverySession";
    private const string Username = "test";

    private readonly AuthenticationTestFactory _factory;
    private readonly HttpClient _client;

    public PasskeyRecoverySessionTests(AuthenticationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_spent_recovery_code_yields_a_session_that_can_enrol_a_passkey()
    {
        var cookie = await SpendARecoveryCodeAsync();

        var response = await PostAsync("register/options", new { username = Username }, cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("challengeToken").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_recovery_cookie_is_http_only_and_time_boxed()
    {
        var setCookie = await VerifyRecoveryCodeAsync();

        var attributes = setCookie.ToLowerInvariant();

        attributes.Should().Contain("httponly");
        // The window a spent code stays redeemable in, and the whole time-boxing of the
        // credential — widening it silently is the failure this pins.
        attributes.Should().Contain("max-age=600");
    }

    [Fact]
    public async Task An_expired_recovery_session_cannot_enrol_a_passkey()
    {
        var response = await PostAsync(
            "register/options", new { username = Username }, ExpiredRecoveryToken());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_forged_recovery_session_cannot_enrol_a_passkey()
    {
        var response = await PostAsync(
            "register/options", new { username = Username }, cookie: "not-a-token");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_passkey_manage_token_that_is_not_a_recovery_session_cannot_enrol_a_passkey()
    {
        var response = await PostAsync(
            "register/options", new { username = Username }, SessionShapedPasskeyManageToken());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task No_recovery_session_cannot_enrol_a_passkey()
    {
        var response = await PostAsync("register/options", new { username = Username }, cookie: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_recovery_session_reaches_nothing_but_enrolment()
    {
        var cookie = await SpendARecoveryCodeAsync();

        // Passkey management on the subject's own account, one route over from the enrolment the
        // recovery session does authorize: it needs a session, which a recovery code never grants.
        var list = new HttpRequestMessage(HttpMethod.Get, "/api/auth/passkey/credentials");
        list.Headers.Add("Cookie", $"{RecoveryCookieName}={cookie}");
        (await _client.SendAsync(list)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var regenerate = await PostAsync("recovery/regenerate", new { }, cookie);
        regenerate.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A recovery-session token the API signed but whose window has passed. Hand-minted because
    /// <see cref="IJwtService.GenerateAccessToken"/> stamps <c>nbf</c> at issue time and refuses to
    /// mint a token that expires before it.
    /// </summary>
    private string ExpiredRecoveryToken()
    {
        var jwt = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey));

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: [new Claim("sub", TestDatabaseSeeder.TestSubjectId.ToString()),
                     new Claim("permission", "passkey:manage")],
            notBefore: DateTime.UtcNow.AddMinutes(-20),
            expires: DateTime.UtcNow.AddMinutes(-10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
    }

    /// <summary>
    /// A token the API itself signs and would honour as a session, carrying the permission a
    /// recovery session carries but nothing that says a recovery code bought it.
    /// </summary>
    private string SessionShapedPasskeyManageToken()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IJwtService>().GenerateAccessToken(
            new SubjectInfo { Id = TestDatabaseSeeder.TestSubjectId },
            permissions: ["passkey:manage"],
            roles: []);
    }

    /// <summary>Spends a freshly issued recovery code and returns the recovery-session token.</summary>
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
            "recovery/verify", new { username = Username, code = codes[0] }, cookie: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return response.Headers.GetValues("Set-Cookie")
            .Single(header => header.StartsWith(RecoveryCookieName, StringComparison.Ordinal));
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/passkey/{path}")
        {
            Content = JsonContent.Create(body),
        };
        if (cookie != null)
        {
            request.Headers.Add("Cookie", $"{RecoveryCookieName}={cookie}");
        }
        return _client.SendAsync(request);
    }
}
