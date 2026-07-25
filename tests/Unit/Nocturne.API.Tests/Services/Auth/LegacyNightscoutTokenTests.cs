using Nocturne.API.Services.Auth;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Verifies Nocturne reproduces legacy Nightscout's access-token matching. The vector below is a
/// real Nightscout derivation:
///   hashedSecret = sha1("my-test-secret-123") = 0d4e7110fb6d9c37b624fbed8b6249826d00a4d2
///   mongoId      = 5f9b3faa1c9d440000a1b2c3
///   digest       = sha1(hashedSecret + mongoId) = 318030bcdc470b9d05518755491a80239a640400
///   accessToken  = "phone-" + digest[..16]      = phone-318030bcdc470b9d
/// </summary>
public class LegacyNightscoutTokenTests
{
    private const string HashedSecret = "0d4e7110fb6d9c37b624fbed8b6249826d00a4d2";
    private const string MongoId = "5f9b3faa1c9d440000a1b2c3";
    private const string Digest = "318030bcdc470b9d05518755491a80239a640400";
    private const string CanonicalToken = "phone-318030bcdc470b9d";

    [Theory]
    // Canonical token AAPS is configured with.
    [InlineData(CanonicalToken, true)]
    // Nightscout ignores the name-abbrev; only the suffix after the last dash matters.
    [InlineData("differentname-318030bcdc470b9d", true)]
    // Uppercase hex still resolves (clients may emit either case).
    [InlineData("PHONE-318030BCDC470B9D", true)]
    // Nightscout also accepts any digest prefix of length 16..40, including the full digest.
    [InlineData("phone-318030bcdc470b9d05518755491a80239a640400", true)]
    // A bare full digest (no dash) is a valid prefix of itself.
    [InlineData(Digest, true)]
    // Suffix shorter than 16 chars is rejected (Nightscout's minimum).
    [InlineData("phone-318030bcdc470b9", false)]
    // Correct length, wrong value.
    [InlineData("phone-aaaaaaaaaaaaaaaa", false)]
    // Non-hex suffix can never prefix a hex digest.
    [InlineData("phone-zzzzzzzzzzzzzzzz", false)]
    // JWT-shaped input is not a legacy token.
    [InlineData("header.payload.signature", false)]
    public void Matches_follows_nightscout_prefix_rule(string token, bool expected)
    {
        LegacyNightscoutToken.Matches(Digest, token).Should().Be(expected);
    }

    [Fact]
    public void Matches_returns_false_when_no_digest_stored()
    {
        LegacyNightscoutToken.Matches(null, CanonicalToken).Should().BeFalse();
        LegacyNightscoutToken.Matches("", CanonicalToken).Should().BeFalse();
    }

    [Fact]
    public void ExtractDigestPrefix_normalizes_to_lowercase_hex()
    {
        LegacyNightscoutToken.ExtractDigestPrefix("PHONE-318030BCDC470B9D")
            .Should().Be("318030bcdc470b9d");
    }

    [Fact]
    public void DeriveDigest_reconstructs_the_nightscout_digest()
    {
        var digest = LegacyNightscoutToken.DeriveDigest(HashedSecret, MongoId, CanonicalToken);

        digest.Should().Be(Digest);
    }

    [Fact]
    public void DeriveDigest_selfcheck_rejects_mismatched_secret()
    {
        // A digest reconstructed from the wrong secret would never match the client's real token,
        // so it must not be stored.
        var wrongSecret = "ffffffffffffffffffffffffffffffffffffffff";

        LegacyNightscoutToken.DeriveDigest(wrongSecret, MongoId, CanonicalToken)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null, MongoId)]
    [InlineData(HashedSecret, null)]
    [InlineData("", "")]
    public void DeriveDigest_returns_null_on_missing_inputs(string? hashedSecret, string? mongoId)
    {
        LegacyNightscoutToken.DeriveDigest(hashedSecret, mongoId, CanonicalToken)
            .Should().BeNull();
    }
}
