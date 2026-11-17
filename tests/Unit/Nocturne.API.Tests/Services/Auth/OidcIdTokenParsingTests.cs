using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The claims an ID token from the token endpoint must carry before its subject is trusted.
/// </summary>
[Trait("Category", "Unit")]
public class OidcIdTokenParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly OidcProvider Provider = new()
    {
        Id = Guid.CreateVersion7(),
        Name = "Keycloak",
        IssuerUrl = "https://idp.example/realms/main",
        ClientId = "nocturne",
    };

    private static string Token(object payload)
    {
        static string Segment(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Segment("{\"alg\":\"RS256\"}")}.{Segment(JsonSerializer.Serialize(payload))}.signature";
    }

    private static object Claims(
        string iss = "https://idp.example/realms/main",
        object? aud = null,
        long? exp = null) => new
    {
        sub = "ext-1",
        iss,
        aud = aud ?? "nocturne",
        exp = exp ?? Now.AddMinutes(5).ToUnixTimeSeconds(),
        nonce = "n",
    };

    [Fact]
    public void A_token_for_this_provider_and_client_parses()
    {
        var claims = OidcAuthService.ParseIdToken(Token(Claims()), Provider, Provider.IssuerUrl, Now);

        claims.Sub.Should().Be("ext-1");
        claims.Nonce.Should().Be("n");
    }

    [Fact]
    public void An_audience_array_naming_this_client_is_accepted()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(aud: new[] { "other", "nocturne" })), Provider, Provider.IssuerUrl, Now);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_trailing_slash_on_the_issuer_is_not_a_different_issuer()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(iss: "https://idp.example/realms/main/")), Provider, Provider.IssuerUrl, Now);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_token_from_another_issuer_is_refused()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(iss: "https://idp.example/realms/other")), Provider, Provider.IssuerUrl, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*issued by the provider*");
    }

    [Fact]
    public void A_token_without_an_issuer_is_refused()
    {
        var token = Token(new { sub = "ext-1", aud = "nocturne", exp = Now.AddMinutes(5).ToUnixTimeSeconds() });

        var act = () => OidcAuthService.ParseIdToken(token, Provider, Provider.IssuerUrl, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*issued by the provider*");
    }

    [Fact]
    public void A_token_that_is_not_three_segments_is_refused()
    {
        var act = () => OidcAuthService.ParseIdToken("not-a-jwt", Provider, Provider.IssuerUrl, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*format*");
    }

    [Fact]
    public void A_token_for_another_client_is_refused()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(aud: new[] { "someone-else" })), Provider, Provider.IssuerUrl, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*for this client*");
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(exp: Now.AddMinutes(-10).ToUnixTimeSeconds())), Provider, Provider.IssuerUrl, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*expired*");
    }

    [Fact]
    public void A_token_without_an_expiry_is_refused()
    {
        var token = Token(new { sub = "ext-1", iss = Provider.IssuerUrl, aud = "nocturne" });

        var act = () => OidcAuthService.ParseIdToken(token, Provider, Provider.IssuerUrl, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*expired*");
    }

    private static string RawToken(string payloadJson)
    {
        static string Segment(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Segment("{\"alg\":\"RS256\"}")}.{Segment(payloadJson)}.signature";
    }

    private static string StandardPayload(string extra) =>
        $"{{\"sub\":\"ext-1\",\"iss\":\"{Provider.IssuerUrl}\",\"aud\":\"nocturne\","
        + $"\"exp\":{Now.AddMinutes(5).ToUnixTimeSeconds()},{extra}}}";

    [Theory]
    [InlineData("\"email_verified\":true", true)]
    [InlineData("\"email_verified\":false", false)]
    [InlineData("\"email_verified\":\"true\"", true)]
    [InlineData("\"email_verified\":\"False\"", false)]
    [InlineData("\"email_verified\":\"yes\"", null)]
    [InlineData("\"email_verified\":null", null)]
    [InlineData("\"email_verified\":{\"value\":true}", null)]
    [InlineData("\"other\":1", null)]
    public void Email_verified_is_read_from_its_snake_case_claim_in_either_form(string claim, bool? expected)
    {
        var claims = OidcAuthService.ParseIdToken(
            RawToken(StandardPayload(claim)), Provider, Provider.IssuerUrl, Now);

        claims.EmailVerified.Should().Be(expected);
    }

    [Fact]
    public void The_snake_case_profile_claims_are_read()
    {
        var claims = OidcAuthService.ParseIdToken(
            RawToken(StandardPayload(
                "\"preferred_username\":\"rhys\",\"given_name\":\"Rhys\",\"family_name\":\"G\","
                + "\"email\":\"r@example.com\",\"email_verified\":true")),
            Provider, Provider.IssuerUrl, Now);

        claims.PreferredUsername.Should().Be("rhys");
        claims.GivenName.Should().Be("Rhys");
        claims.FamilyName.Should().Be("G");
        claims.Email.Should().Be("r@example.com");
        claims.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public void A_multi_tenant_entra_token_matches_through_the_discovery_issuer_template()
    {
        var entra = new OidcProvider
        {
            IssuerUrl = "https://login.microsoftonline.com/common/v2.0",
            ClientId = "nocturne",
        };
        const string tid = "9188040d-6c67-4c5b-b112-36a304b66dad";
        var token = Token(new
        {
            sub = "ext-1",
            iss = $"https://login.microsoftonline.com/{tid}/v2.0",
            tid,
            aud = "nocturne",
            exp = Now.AddMinutes(5).ToUnixTimeSeconds(),
        });

        var act = () => OidcAuthService.ParseIdToken(
            token, entra, "https://login.microsoftonline.com/{tenantid}/v2.0", Now);

        act.Should().NotThrow();
    }

    [Fact]
    public void An_entra_token_whose_issuer_names_another_tenant_than_its_tid_is_refused()
    {
        var entra = new OidcProvider
        {
            IssuerUrl = "https://login.microsoftonline.com/common/v2.0",
            ClientId = "nocturne",
        };
        var token = Token(new
        {
            sub = "ext-1",
            iss = "https://login.microsoftonline.com/aaaaaaaa-0000-0000-0000-000000000000/v2.0",
            tid = "bbbbbbbb-0000-0000-0000-000000000000",
            aud = "nocturne",
            exp = Now.AddMinutes(5).ToUnixTimeSeconds(),
        });

        var act = () => OidcAuthService.ParseIdToken(
            token, entra, "https://login.microsoftonline.com/{tenantid}/v2.0", Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*issued by the provider*");
    }

    [Fact]
    public void Google_issuing_its_bare_host_matches_the_https_issuer()
    {
        var google = new OidcProvider { IssuerUrl = "https://accounts.google.com", ClientId = "nocturne" };
        var token = Token(new
        {
            sub = "ext-1",
            iss = "accounts.google.com",
            aud = "nocturne",
            exp = Now.AddMinutes(5).ToUnixTimeSeconds(),
        });

        var act = () => OidcAuthService.ParseIdToken(token, google, "https://accounts.google.com", Now);

        act.Should().NotThrow();
    }
}
