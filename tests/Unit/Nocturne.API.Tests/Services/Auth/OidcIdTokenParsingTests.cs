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
        var claims = OidcAuthService.ParseIdToken(Token(Claims()), Provider, Now);

        claims.Sub.Should().Be("ext-1");
        claims.Nonce.Should().Be("n");
    }

    [Fact]
    public void An_audience_array_naming_this_client_is_accepted()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(aud: new[] { "other", "nocturne" })), Provider, Now);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_trailing_slash_on_the_issuer_is_not_a_different_issuer()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(iss: "https://idp.example/realms/main/")), Provider, Now);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_token_from_another_issuer_is_refused()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(iss: "https://idp.example/realms/other")), Provider, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*issued by the provider*");
    }

    [Fact]
    public void A_token_for_another_client_is_refused()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(aud: new[] { "someone-else" })), Provider, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*for this client*");
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var act = () => OidcAuthService.ParseIdToken(
            Token(Claims(exp: Now.AddMinutes(-10).ToUnixTimeSeconds())), Provider, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*expired*");
    }

    [Fact]
    public void A_token_without_an_expiry_is_refused()
    {
        var token = Token(new { sub = "ext-1", iss = Provider.IssuerUrl, aud = "nocturne" });

        var act = () => OidcAuthService.ParseIdToken(token, Provider, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*expired*");
    }
}
