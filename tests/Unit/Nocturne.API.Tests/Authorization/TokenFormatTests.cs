using Nocturne.API.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// The auth chain routes a credential to a handler on its shape alone, before anything validates
/// it, so widening or narrowing <see cref="TokenFormat.IsJwt"/> moves credentials between handlers.
/// </summary>
[Trait("Category", "Unit")]
public class TokenFormatTests
{
    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln")]
    [InlineData("a.b.c")]
    [InlineData("..")]
    public void Three_segments_read_as_a_jwt(string token)
    {
        TokenFormat.IsJwt(token).Should().BeTrue();
    }

    [Theory]
    [InlineData("noc_Zm9vYmFyYmF6")]
    [InlineData("rhys-a1b2c3d4e5f6g7h8")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_does_not(string? token)
    {
        TokenFormat.IsJwt(token).Should().BeFalse();
    }

    /// <summary>
    /// Nocturne's two opaque credential formats carry no dot, which is what lets the JWT handlers
    /// claim a credential without stealing one that belongs to DirectGrantTokenHandler or
    /// AccessTokenHandler.
    /// </summary>
    [Fact]
    public void A_fourth_segment_is_not_a_jwt_so_it_cannot_be_claimed_by_widening()
    {
        TokenFormat.IsJwt("header.payload.signature.extra").Should().BeFalse();
    }
}
