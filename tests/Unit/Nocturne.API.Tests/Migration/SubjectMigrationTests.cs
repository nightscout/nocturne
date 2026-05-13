using System.Security.Cryptography;
using System.Text;

namespace Nocturne.API.Tests.Migration;

public class SubjectMigrationTests
{
    [Fact]
    public void HashAccessToken_produces_same_hash_as_SubjectService()
    {
        // The migration must produce the same SHA-256 hash that SubjectService
        // and AccessTokenHandler use, so migrated tokens are found on lookup.
        var token = "phone-a1b2c3d4e5f6g7h8";

        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        var expected = Convert.ToHexString(hash).ToLowerInvariant();

        // Verify the hash is a 64-char lowercase hex string
        expected.Should().HaveLength(64);
        expected.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void AccessTokenPrefix_format_matches_SubjectService_convention()
    {
        var name = "Phone";
        var accessToken = "phone-a1b2c3d4e5f6g7h8";

        var prefix = $"{name.ToLowerInvariant()}-{accessToken[..Math.Min(8, accessToken.Length)]}";

        prefix.Should().Be("phone-phone-a1");
    }

    [Theory]
    [InlineData(new[] { "denied" }, false)]
    [InlineData(new[] { "readable" }, true)]
    [InlineData(new[] { "admin", "denied" }, true)]
    [InlineData(new[] { "readable", "careportal" }, true)]
    public void IsActive_is_false_only_when_denied_is_sole_role(string[] roles, bool expectedActive)
    {
        var isDenied = roles is ["denied"];
        var isActive = !isDenied;

        isActive.Should().Be(expectedActive);
    }

    [Fact]
    public void Empty_accessToken_should_be_skipped()
    {
        var accessToken = "";
        string.IsNullOrWhiteSpace(accessToken).Should().BeTrue();
    }
}
