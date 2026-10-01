using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nocturne.API.Helpers;
using Xunit;

namespace Nocturne.API.Tests.Helpers;

[Trait("Category", "Unit")]
public class LegacyFindQueryStringTests
{
    [Theory]
    [InlineData("?find[type]=sgv&count=5", "find[type]=sgv&count=5")]
    [InlineData("?find%5Btype%5D=sgv", "find%5Btype%5D=sgv")]
    [InlineData("?find%5btype%5d=sgv", "find%5btype%5d=sgv")]
    public void Resolve_BracketedFind_ReturnsTheQueryString(string queryString, string expected)
    {
        LegacyFindQueryString.Resolve(Request(queryString), boundFind: null).Should().Be(expected);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("?count=5", null, null)]
    [InlineData("?count=5", "", null)]
    [InlineData("?find={\"type\":\"sgv\"}", "{\"type\":\"sgv\"}", "{\"type\":\"sgv\"}")]
    public void Resolve_NoBracketedFind_ReturnsTheBoundFind(string queryString, string? bound, string? expected)
    {
        LegacyFindQueryString.Resolve(Request(queryString), bound).Should().Be(expected);
    }

    [Fact]
    public void Resolve_NoRequest_ReturnsTheBoundFind()
    {
        LegacyFindQueryString.Resolve(null, "{}").Should().Be("{}");
    }

    private static HttpRequest Request(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString.Length == 0 ? null : queryString);
        return context.Request;
    }
}
