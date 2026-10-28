using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// An access token is stamped and validated against the injected clock, and a validator allows 30
/// seconds of skew on either side of its lifetime.
/// </summary>
[Trait("Category", "Unit")]
public class JwtServiceLifetimeTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 3, 14, 9, 26, 45, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly JwtService _jwt = At(IssuedAt);

    private readonly SubjectInfo _subject = new() { Id = Guid.CreateVersion7(), Name = "Member" };

    private static JwtService At(DateTimeOffset now) => new(
        Options.Create(new JwtOptions
        {
            SecretKey = "lifetime-test-secret-key-at-least-32-chars",
            Issuer = "nocturne",
            Audience = "nocturne-api",
            AccessTokenLifetimeMinutes = (int)Lifetime.TotalMinutes,
        }),
        NullLogger<JwtService>.Instance,
        new FakeTimeProvider(now));

    public static TheoryData<string> Overloads => ["session", "scoped"];

    private string Mint(string overload) => overload == "session"
        ? _jwt.GenerateAccessToken(_subject, [], [])
        : _jwt.GenerateAccessToken(_subject, [], [], ["glucose.read"]);

    [Theory]
    [MemberData(nameof(Overloads))]
    public void A_minted_token_is_issued_now_and_expires_after_its_lifetime(string overload)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(Mint(overload));

        token.Payload.IssuedAt.Should().Be(IssuedAt.UtcDateTime);
        token.Payload.ValidFrom.Should().Be(IssuedAt.UtcDateTime);
        token.Payload.ValidTo.Should().Be((IssuedAt + Lifetime).UtcDateTime);
    }

    [Fact]
    public void A_requested_lifetime_replaces_the_configured_one()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            _jwt.GenerateAccessToken(_subject, [], [], lifetime: TimeSpan.FromMinutes(2)));

        token.Payload.ValidTo.Should().Be((IssuedAt + TimeSpan.FromMinutes(2)).UtcDateTime);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, JwtValidationError.Expired)]
    public void A_token_validates_until_its_expiry_plus_the_skew(long ticksPastSkew, JwtValidationError? error)
    {
        var token = Mint("scoped");

        var result = At(IssuedAt + Lifetime + ClockSkew + TimeSpan.FromTicks(ticksPastSkew))
            .ValidateAccessToken(token);

        result.IsValid.Should().Be(error is null);
        result.ErrorCode.Should().Be(error);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, JwtValidationError.NotYetValid)]
    public void A_token_validates_from_its_issue_minus_the_skew(long ticksBeforeSkew, JwtValidationError? error)
    {
        var token = Mint("scoped");

        var result = At(IssuedAt - ClockSkew - TimeSpan.FromTicks(ticksBeforeSkew))
            .ValidateAccessToken(token);

        result.IsValid.Should().Be(error is null);
        result.ErrorCode.Should().Be(error);
    }

    [Fact]
    public void A_validated_token_reports_the_instants_it_was_minted_with()
    {
        var token = Mint("scoped");

        var claims = At(IssuedAt + TimeSpan.FromMinutes(1)).ValidateAccessToken(token).Claims!;

        claims.IssuedAt.Should().Be(IssuedAt);
        claims.ExpiresAt.Should().Be(IssuedAt + Lifetime);
    }
}
