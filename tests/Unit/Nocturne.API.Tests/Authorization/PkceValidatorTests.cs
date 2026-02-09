using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public class PkceValidatorTests
{
    [Fact]
    public void ValidateCodeChallenge_ValidVerifier_ReturnsTrue()
    {
        var verifier = PkceValidator.GenerateCodeVerifier();
        var challenge = PkceValidator.ComputeCodeChallenge(verifier);

        Assert.True(PkceValidator.ValidateCodeChallenge(verifier, challenge));
    }

    [Fact]
    public void ValidateCodeChallenge_TamperedVerifier_ReturnsFalse()
    {
        var verifier = PkceValidator.GenerateCodeVerifier();
        var challenge = PkceValidator.ComputeCodeChallenge(verifier);

        var tampered = verifier + "x";

        Assert.False(PkceValidator.ValidateCodeChallenge(tampered, challenge));
    }

    [Fact]
    public void ValidateCodeChallenge_WrongChallenge_ReturnsFalse()
    {
        var verifier = PkceValidator.GenerateCodeVerifier();
        var differentVerifier = PkceValidator.GenerateCodeVerifier();
        var challenge = PkceValidator.ComputeCodeChallenge(differentVerifier);

        Assert.False(PkceValidator.ValidateCodeChallenge(verifier, challenge));
    }

    [Theory]
    [InlineData(null, "some-challenge")]
    [InlineData("", "some-challenge")]
    [InlineData("some-verifier", null)]
    [InlineData("some-verifier", "")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void ValidateCodeChallenge_NullOrEmptyInputs_ReturnsFalse(string? verifier, string? challenge)
    {
        Assert.False(PkceValidator.ValidateCodeChallenge(verifier!, challenge!));
    }

    [Fact]
    public void GenerateCodeVerifier_ProducesValidLength()
    {
        var verifier = PkceValidator.GenerateCodeVerifier();

        Assert.InRange(verifier.Length, 43, 128);
    }

    [Fact]
    public void GenerateCodeVerifier_IsUrlSafe()
    {
        var verifier = PkceValidator.GenerateCodeVerifier();

        // Must not contain characters that are not URL-safe
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);
    }

    [Fact]
    public void GenerateCodeVerifier_ProducesUniqueValues()
    {
        var verifier1 = PkceValidator.GenerateCodeVerifier();
        var verifier2 = PkceValidator.GenerateCodeVerifier();

        Assert.NotEqual(verifier1, verifier2);
    }

    [Fact]
    public void ComputeCodeChallenge_KnownVector()
    {
        // RFC 7636 Appendix B test vector
        // code_verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
        // expected S256 challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        var computed = PkceValidator.ComputeCodeChallenge(verifier);

        Assert.Equal(expectedChallenge, computed);
    }

    [Fact]
    public void ComputeCodeChallenge_KnownVector_ValidatesSuccessfully()
    {
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.True(PkceValidator.ValidateCodeChallenge(verifier, expectedChallenge));
    }

    [Fact]
    public void RoundTrip_GenerateComputeValidate()
    {
        // Full round-trip: generate verifier, compute challenge, then validate
        for (var i = 0; i < 10; i++)
        {
            var verifier = PkceValidator.GenerateCodeVerifier();
            var challenge = PkceValidator.ComputeCodeChallenge(verifier);

            Assert.True(PkceValidator.ValidateCodeChallenge(verifier, challenge),
                $"Round-trip failed for verifier: {verifier}");
        }
    }

    [Fact]
    public void ComputeCodeChallenge_DeterministicForSameInput()
    {
        var verifier = "test-verifier-deterministic-check";

        var challenge1 = PkceValidator.ComputeCodeChallenge(verifier);
        var challenge2 = PkceValidator.ComputeCodeChallenge(verifier);

        Assert.Equal(challenge1, challenge2);
    }
}
