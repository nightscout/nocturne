using FluentAssertions;
using Nocturne.Connectors.Glooko.Xt;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtJwtTests
{
    internal static string Token(long? exp) =>
        $"{Encode("""{"alg":"HS256","typ":"JWT"}""")}.{Encode(exp is null ? """{"role":"PATIENT"}""" : $$$"""{"role":"PATIENT","exp":{{{exp}}}}""")}.signature";

    private static string Encode(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void TryGetExpiry_ReadsTheExpClaim()
    {
        var exp = new DateTimeOffset(2027, 6, 17, 0, 0, 0, TimeSpan.Zero);

        GlookoXtJwt.TryGetExpiry(Token(exp.ToUnixTimeSeconds())).Should().Be(exp.UtcDateTime);
    }

    [Fact]
    public void TryGetExpiry_IsNullWithoutAnExpClaim()
    {
        GlookoXtJwt.TryGetExpiry(Token(null)).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.!!!.c")]
    public void TryGetExpiry_IsNullForAnythingThatIsNotAJwt(string? token)
    {
        GlookoXtJwt.TryGetExpiry(token).Should().BeNull();
    }

    [Fact]
    public void LooksLikeJwt_RequiresThreeParts()
    {
        GlookoXtJwt.LooksLikeJwt(Token(1)).Should().BeTrue();
        GlookoXtJwt.LooksLikeJwt("abc").Should().BeFalse();
        GlookoXtJwt.LooksLikeJwt("a.b .c").Should().BeFalse();
    }
}
