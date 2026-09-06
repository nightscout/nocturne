using FluentAssertions;
using Nocturne.API.Services.Auth;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for TotpHelper — RFC 6238 TOTP computation
/// </summary>
public class TotpHelperTests
{
    // RFC 6238 Appendix B shared secret for SHA1 test vectors: ASCII "12345678901234567890"
    private static readonly byte[] RfcTestSecret = "12345678901234567890"u8.ToArray();

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    public void ComputeTotp_RfcTestVectors_ProducesExpectedCodes(long unixTime, string expected)
    {
        var result = TotpHelper.ComputeTotp(RfcTestSecret, unixTime);

        result.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GenerateSecret_Returns20Bytes()
    {
        var secret = TotpHelper.GenerateSecret();

        secret.Should().HaveCount(20);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GenerateSecret_ProducesDifferentValues()
    {
        var secret1 = TotpHelper.GenerateSecret();
        var secret2 = TotpHelper.GenerateSecret();

        secret1.Should().NotEqual(secret2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToBase32_ProducesValidBase32Characters()
    {
        var secret = TotpHelper.GenerateSecret();

        var base32 = TotpHelper.ToBase32(secret);

        base32.Should().MatchRegex("^[A-Z2-7]+$");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToBase32_KnownInput_ProducesExpectedOutput()
    {
        // "Hello!" in base32 is "JBSWY3DPEE"
        var input = "Hello!"u8.ToArray();

        var result = TotpHelper.ToBase32(input);

        result.Should().Be("JBSWY3DPEE");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildProvisioningUri_FormatsCorrectly()
    {
        var secret = "Hello!"u8.ToArray();
        var expectedBase32 = TotpHelper.ToBase32(secret);

        var uri = TotpHelper.BuildProvisioningUri("alice", secret);

        uri.Should().Be(
            $"otpauth://totp/Nocturne:alice?secret={expectedBase32}&issuer=Nocturne&digits=6&period=30"
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Verify_AcceptsCurrentTimeStepCode()
    {
        var secret = TotpHelper.GenerateSecret();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpHelper.ComputeTotp(secret, now);

        var result = TotpHelper.Verify(secret, code);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Verify_AcceptsPreviousTimeStepCode()
    {
        var secret = TotpHelper.GenerateSecret();
        // Compute code for 30 seconds ago (previous time step)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpHelper.ComputeTotp(secret, now - 30);

        var result = TotpHelper.Verify(secret, code);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Verify_AcceptsNextTimeStepCode()
    {
        var secret = TotpHelper.GenerateSecret();
        // Compute code for 30 seconds in the future (next time step)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpHelper.ComputeTotp(secret, now + 30);

        var result = TotpHelper.Verify(secret, code);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Verify_RejectsWrongCode()
    {
        var secret = TotpHelper.GenerateSecret();

        var result = TotpHelper.Verify(secret, "000000");

        // Extremely unlikely to be valid, but if it is, the test secret just happens to match.
        // We use a fixed known-bad approach instead:
        var knownSecret = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        var wrongCode = "999999";
        var currentCode = TotpHelper.ComputeTotp(knownSecret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Only assert if the wrong code is actually different from the current code
        if (wrongCode != currentCode)
        {
            TotpHelper.Verify(knownSecret, wrongCode).Should().BeFalse();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Verify_RejectsCodeFromDistantFuture()
    {
        var secret = TotpHelper.GenerateSecret();
        // Code from 5 minutes in the future — well outside the +/- 1 step window
        var futureCode = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300);

        var result = TotpHelper.Verify(secret, futureCode);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryVerify_ReportsTheMatchedTimeStep()
    {
        var secret = TotpHelper.GenerateSecret();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpHelper.ComputeTotp(secret, now);

        var result = TotpHelper.TryVerify(secret, code, lastUsedStep: null, out var matchedStep);

        result.Should().BeTrue();
        matchedStep.Should().Be(now / 30);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryVerify_RejectsACodeWhoseStepWasAlreadyConsumed()
    {
        var secret = TotpHelper.GenerateSecret();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpHelper.ComputeTotp(secret, now);

        TotpHelper.TryVerify(secret, code, lastUsedStep: null, out var consumedStep).Should().BeTrue();

        // Replayed inside the same +/- 1 step window, which without the floor would still match.
        var replay = TotpHelper.TryVerify(secret, code, consumedStep, out _);

        replay.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryVerify_AcceptsACodeFromAStepAfterTheConsumedOne()
    {
        var secret = TotpHelper.GenerateSecret();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpHelper.ComputeTotp(secret, now);

        // A step before the current one was consumed, so the current code is still usable.
        var result = TotpHelper.TryVerify(secret, code, lastUsedStep: (now / 30) - 1, out var matchedStep);

        result.Should().BeTrue();
        matchedStep.Should().Be(now / 30);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeTotp_AlwaysReturnsSixDigits()
    {
        var secret = TotpHelper.GenerateSecret();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var code = TotpHelper.ComputeTotp(secret, now);

        code.Should().HaveLength(6);
        code.Should().MatchRegex("^[0-9]{6}$");
    }
}
