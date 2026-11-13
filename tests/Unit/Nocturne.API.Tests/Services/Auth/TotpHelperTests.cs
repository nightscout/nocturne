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

    // A multiple of the 30 second step, so StepStart is the first second of a step.
    private static readonly DateTimeOffset StepStart = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);

    private static string CodeAt(byte[] secret, DateTimeOffset time) =>
        TotpHelper.ComputeTotp(secret, time.ToUnixTimeSeconds());

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(-60, false)]
    [InlineData(-30, true)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(60, false)]
    public void Verify_AcceptsOnlyTheStepsAdjacentToTheClock(int codeOffsetSeconds, bool accepted)
    {
        var code = CodeAt(RfcTestSecret, StepStart.AddSeconds(codeOffsetSeconds));

        TotpHelper.Verify(RfcTestSecret, code, StepStart).Should().Be(accepted);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(29, true)]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public void Verify_StopsAcceptingACodeOnceTheClockMovesTwoStepsOn(int clockOffsetSeconds, bool accepted)
    {
        var code = CodeAt(RfcTestSecret, StepStart);

        TotpHelper.Verify(RfcTestSecret, code, StepStart.AddSeconds(clockOffsetSeconds)).Should().Be(accepted);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Verify_RejectsAWrongCode()
    {
        var current = CodeAt(RfcTestSecret, StepStart);
        var wrong = current == "000000" ? "000001" : "000000";

        TotpHelper.Verify(RfcTestSecret, wrong, StepStart).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryVerify_ReportsTheMatchedTimeStep()
    {
        var code = CodeAt(RfcTestSecret, StepStart);

        var result = TotpHelper.TryVerify(RfcTestSecret, code, lastUsedStep: null, StepStart, out var matchedStep);

        result.Should().BeTrue();
        matchedStep.Should().Be(StepStart.ToUnixTimeSeconds() / 30);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryVerify_RejectsACodeWhoseStepWasAlreadyConsumed()
    {
        var code = CodeAt(RfcTestSecret, StepStart);

        TotpHelper.TryVerify(RfcTestSecret, code, lastUsedStep: null, StepStart, out var consumedStep).Should().BeTrue();

        // Replayed inside the same +/- 1 step window, which without the floor would still match.
        var replay = TotpHelper.TryVerify(RfcTestSecret, code, consumedStep, StepStart, out _);

        replay.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryVerify_AcceptsACodeFromAStepAfterTheConsumedOne()
    {
        var code = CodeAt(RfcTestSecret, StepStart);
        var currentStep = StepStart.ToUnixTimeSeconds() / 30;

        // A step before the current one was consumed, so the current code is still usable.
        var result = TotpHelper.TryVerify(RfcTestSecret, code, lastUsedStep: currentStep - 1, StepStart, out var matchedStep);

        result.Should().BeTrue();
        matchedStep.Should().Be(currentStep);
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
