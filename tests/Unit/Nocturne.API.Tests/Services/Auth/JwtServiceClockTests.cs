using System.IdentityModel.Tokens.Jwt;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// <see cref="JwtService"/> issues and validates tokens against its injected clock, which starts years
/// before the test runs. The stamp cases fail if issuance reads the system clock, and the acceptance
/// cases fail if validation does.
/// </summary>
[Trait("Category", "Unit")]
public class JwtServiceClockTests
{
    private const string SecretKey = "jwt-clock-test-secret-key-at-least-32-chars";
    private const string Issuer = "nocturne";
    private const string Audience = "nocturne-api";

    private static readonly DateTimeOffset Minted = new(2020, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly FakeTimeProvider _clock = new(Minted);
    private readonly JwtService _jwt;
    private readonly string _token;

    public JwtServiceClockTests()
    {
        _jwt = Service(_clock);
        _token = _jwt.GenerateAccessToken(
            new SubjectInfo { Id = Guid.CreateVersion7() }, [], [], [], lifetime: Lifetime);
    }

    private static JwtService Service(TimeProvider clock) => new(
        Options.Create(new JwtOptions { SecretKey = SecretKey, Issuer = Issuer, Audience = Audience }),
        NullLogger<JwtService>.Instance,
        clock);

    /// <summary>A validator whose clock reads <paramref name="now"/>, which may precede <see cref="Minted"/>.</summary>
    private static JwtService ValidatorAt(DateTimeOffset now) => Service(new FakeTimeProvider(now));

    /// <summary>
    /// A token signed with the test key carrying exactly the time claims in <paramref name="times"/>.
    /// The payload is written by hand, so the handler neither fills in a time nor refuses an
    /// <c>nbf</c> after <c>exp</c>.
    /// </summary>
    private static string Signed(params (string Claim, DateTimeOffset At)[] times)
    {
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Sub] = Guid.CreateVersion7().ToString(),
            [JwtRegisteredClaimNames.Iss] = Issuer,
            [JwtRegisteredClaimNames.Aud] = Audience,
        };
        foreach (var (claim, at) in times)
            payload[claim] = at.ToUnixTimeSeconds();

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey)), SecurityAlgorithms.HmacSha256);
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.WriteToken(new JwtSecurityToken(new JwtHeader(credentials), payload));
    }

    [Fact]
    public void The_token_is_stamped_with_the_injected_clock()
    {
        var result = _jwt.ValidateAccessToken(_token);

        result.IsValid.Should().BeTrue(result.Error);
        result.Claims!.IssuedAt.Should().Be(Minted);
        result.Claims.ExpiresAt.Should().Be(Minted + Lifetime);
    }

    [Fact]
    public void The_session_token_is_stamped_with_the_injected_clock()
    {
        var token = _jwt.GenerateAccessToken(
            new SubjectInfo { Id = Guid.CreateVersion7() }, [], [], sessionId: "family", lifetime: Lifetime);

        var result = _jwt.ValidateAccessToken(token);

        result.IsValid.Should().BeTrue(result.Error);
        result.Claims!.IssuedAt.Should().Be(Minted);
        result.Claims.ExpiresAt.Should().Be(Minted + Lifetime);
    }

    [Fact]
    public void A_token_is_accepted_up_to_its_expiry_plus_the_clock_skew()
    {
        _clock.Advance(Lifetime + JwtService.ClockSkew);

        _jwt.ValidateAccessToken(_token).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_token_past_its_expiry_plus_the_clock_skew_is_expired()
    {
        _clock.Advance(Lifetime + JwtService.ClockSkew + TimeSpan.FromSeconds(1));

        var result = _jwt.ValidateAccessToken(_token);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(JwtValidationError.Expired);
    }

    [Fact]
    public void A_token_is_accepted_from_the_clock_skew_before_it_was_issued()
    {
        ValidatorAt(Minted - JwtService.ClockSkew).ValidateAccessToken(_token).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_token_earlier_than_the_clock_skew_before_it_was_issued_is_not_yet_valid()
    {
        var result = ValidatorAt(Minted - JwtService.ClockSkew - TimeSpan.FromSeconds(1))
            .ValidateAccessToken(_token);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(JwtValidationError.NotYetValid);
    }

    [Fact]
    public void A_hand_signed_token_with_both_times_is_accepted()
    {
        var token = Signed(
            (JwtRegisteredClaimNames.Nbf, Minted), (JwtRegisteredClaimNames.Exp, Minted + Lifetime));

        var result = _jwt.ValidateAccessToken(token);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public void A_token_with_no_expiry_is_refused()
    {
        var result = _jwt.ValidateAccessToken(Signed((JwtRegisteredClaimNames.Nbf, Minted)));

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(JwtValidationError.InvalidFormat);
        result.Error.Should().Contain("no expiration time");
    }

    [Fact]
    public void A_token_whose_not_before_follows_its_expiry_is_refused()
    {
        // Within the skew of both times, so neither the not-yet-valid nor the expired check refuses it.
        var token = Signed(
            (JwtRegisteredClaimNames.Nbf, Minted + TimeSpan.FromSeconds(10)), (JwtRegisteredClaimNames.Exp, Minted));
        _clock.Advance(TimeSpan.FromSeconds(5));

        var result = _jwt.ValidateAccessToken(token);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(JwtValidationError.InvalidFormat);
        result.Error.Should().Contain("NotBefore is after its Expires");
    }
}
