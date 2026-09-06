using Microsoft.AspNetCore.Http;
using Nocturne.API.Extensions;

namespace Nocturne.API.Tests.Extensions;

/// <summary>
/// Seven credential paths read the <c>Authorization</c> header, and three of them fall through to a
/// second source when it carries nothing for them. What
/// <see cref="AuthorizationHeaderExtensions.GetAuthorizationCredential"/> returns for a header that
/// is present but empty therefore decides whether those fallbacks are reached.
/// </summary>
[Trait("Category", "Unit")]
public class AuthorizationHeaderExtensionsTests
{
    [Theory]
    [InlineData("Bearer abc123", "abc123")]
    [InlineData("bearer abc123", "abc123")]
    [InlineData("BEARER abc123", "abc123")]
    public void Scheme_is_matched_case_insensitively(string header, string expected)
    {
        RequestWith(header).GetAuthorizationCredential().Should().Be(expected);
    }

    [Fact]
    public void Credential_is_trimmed()
    {
        RequestWith("Bearer   abc123  ").GetAuthorizationCredential().Should().Be("abc123");
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Refresh abc123")]
    [InlineData("Bearer")]
    [InlineData("abc123")]
    [InlineData("")]
    public void A_header_carrying_another_scheme_yields_null(string header)
    {
        RequestWith(header).GetAuthorizationCredential().Should().BeNull();
    }

    [Fact]
    public void An_absent_header_yields_null()
    {
        new DefaultHttpContext().Request.GetAuthorizationCredential().Should().BeNull();
    }

    [Fact]
    public void A_present_but_empty_credential_is_empty_rather_than_absent()
    {
        RequestWith("Bearer ").GetAuthorizationCredential()
            .Should().BeEmpty("a caller with a query-string fallback decides for itself whether an "
                + "empty Bearer header should fall through to it");
    }

    [Fact]
    public void A_non_default_scheme_is_read_under_its_own_word()
    {
        var request = RequestWith("Refresh abc123");

        request.GetAuthorizationCredential("Refresh").Should().Be("abc123");
        request.GetAuthorizationCredential().Should().BeNull();
    }

    private static HttpRequest RequestWith(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = header;
        return context.Request;
    }
}
