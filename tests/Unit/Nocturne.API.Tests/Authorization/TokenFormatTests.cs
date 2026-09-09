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

    /// <summary>
    /// Both shapes that reach a subject's access token: the bare hex string Nocturne mints, and the
    /// <c>{name-abbrev}-{digest}</c> token a subject migrated from classic Nightscout keeps.
    /// </summary>
    [Theory]
    [InlineData("2f0c9cf12ed3fb2eb59df4bb4157dbb08ea44121e2e2b5cbfd85d1c31d34e5b6")]
    [InlineData("2F0C9CF12ED3FB2EB59DF4BB4157DBB08EA44121E2E2B5CBFD85D1C31D34E5B6")]
    [InlineData("phone-318030bcdc470b9d")]
    [InlineData("rhys-a1b2c3d4e5f6g7h8")]
    public void Both_access_token_shapes_are_worth_a_lookup(string token)
    {
        TokenFormat.IsAccessToken(token).Should().BeTrue();
    }

    /// <summary>
    /// A JWT and a dashless <c>noc_</c> direct grant keep falling through to the handlers that own
    /// them. A Base64-URL grant secret that happens to contain a dash reads as the Nightscout shape
    /// and always has, which costs nothing: <see cref="Nocturne.API.Middleware.Handlers.DirectGrantTokenHandler"/>
    /// runs first, and a grant this handler then fails to find is skipped rather than refused.
    /// </summary>
    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln")]
    [InlineData("noc_Zm9vYmFyYmF6cXV1eGNvcmdlZ3JhdWx0Z2FycGx5")]
    public void A_credential_another_handler_owns_is_not_an_access_token(string token)
    {
        TokenFormat.IsAccessToken(token).Should().BeFalse();
    }

    /// <summary>
    /// Hex alone is not enough — only the minted length is, so a random hex fragment does not buy a
    /// database lookup. A dash-delimited digest keeps the looser rule migrated tokens need.
    /// </summary>
    [Theory]
    [InlineData("2f0c9cf12ed3fb2e")]
    [InlineData("2f0c9cf12ed3fb2eb59df4bb4157dbb08ea44121e2e2b5cbfd85d1c31d34e5b")]
    [InlineData("2f0c9cf12ed3fb2eb59df4bb4157dbb08ea44121e2e2b5cbfd85d1c31d34e5b6a")]
    [InlineData("notanaccesstoken")]
    [InlineData("phone-short")]
    [InlineData("-318030bcdc470b9d")]
    [InlineData("phone-")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_worth_a_lookup(string? token)
    {
        TokenFormat.IsAccessToken(token).Should().BeFalse();
    }
}
