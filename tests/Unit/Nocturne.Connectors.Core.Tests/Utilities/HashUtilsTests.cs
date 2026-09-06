using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Utilities;

/// <summary>
/// <see cref="HashUtils.Sha256Hex"/> is what stands behind the persisted credential hashes — the
/// <c>token_hash</c> columns on subjects and OAuth grants, refresh-token hashes, invite-token
/// hashes. A change to its output invalidates every stored credential at once, silently, so the
/// output is pinned to a published vector rather than to whatever the implementation produces.
/// </summary>
public class HashUtilsTests
{
    private const string Input = "abc";

    /// <summary>FIPS 180-4 SHA-256 vector for the three-byte message "abc".</summary>
    private const string AbcDigest =
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Sha256Hex_matches_the_published_vector()
    {
        HashUtils.Sha256Hex(Input).Should().Be(AbcDigest);
    }

    /// <summary>
    /// The call sites this replaced were split between the two spellings of lowercase hex. They
    /// agree, which is why folding them together leaves stored hashes readable.
    /// </summary>
    [Fact]
    public void Both_spellings_of_lowercase_hex_agree()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Input));

        Convert.ToHexString(digest).ToLowerInvariant().Should().Be(AbcDigest);
        Convert.ToHexStringLower(digest).Should().Be(AbcDigest);
    }

    [Fact]
    public void Sha256Hex_of_the_empty_string_matches_the_published_vector()
    {
        HashUtils.Sha256Hex(string.Empty)
            .Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }
}
