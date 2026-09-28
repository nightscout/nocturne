using FluentAssertions;
using Nocturne.API.Helpers;

namespace Nocturne.API.Tests.Helpers;

public class NightscoutBaseUriTests
{
    [Theory]
    [InlineData("https://ns.example", "https://ns.example/")]
    [InlineData("https://ns.example/", "https://ns.example/")]
    [InlineData("https://ns.example/nightscout", "https://ns.example/nightscout/")]
    [InlineData("https://ns.example/nightscout/", "https://ns.example/nightscout/")]
    [InlineData("https://ns.example/a/b//", "https://ns.example/a/b/")]
    [InlineData("https://ns.example/?token=synthetic-token", "https://ns.example/")]
    [InlineData("https://ns.example/nightscout?token=synthetic-token", "https://ns.example/nightscout/")]
    [InlineData("https://ns.example/nightscout?token=synthetic-token#section", "https://ns.example/nightscout/")]
    [InlineData("https://user:pass@ns.example/nightscout", "https://ns.example/nightscout/")]
    [InlineData("http://ns.example:1337/nightscout", "http://ns.example:1337/nightscout/")]
    [InlineData("https://ns.example:443/nightscout", "https://ns.example/nightscout/")]
    [InlineData("https://ns.example/night%20scout", "https://ns.example/night%20scout/")]
    [InlineData("  https://ns.example/nightscout  ", "https://ns.example/nightscout/")]
    [InlineData("ns.example", "https://ns.example/")]
    [InlineData("ns.example/nightscout?token=synthetic-token", "https://ns.example/nightscout/")]
    [InlineData("ns.example:1337/nightscout", "https://ns.example:1337/nightscout/")]
    public void The_base_keeps_scheme_host_port_and_path_and_ends_in_one_slash(string configured, string expected)
    {
        NightscoutBaseUri.TryFor(configured, out var baseUri).Should().BeTrue();
        baseUri!.AbsoluteUri.Should().Be(expected);
        NightscoutBaseUri.For(configured).AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://ns.example/nightscout")]
    [InlineData("file:/etc/passwd")]
    [InlineData("/nightscout")]
    [InlineData("ns.example:notaport")]
    public void An_address_that_is_not_http_or_https_is_refused(string? configured)
    {
        NightscoutBaseUri.TryFor(configured, out var baseUri).Should().BeFalse();
        baseUri.Should().BeNull();
    }

    [Fact]
    public void For_names_the_address_when_it_refuses_one()
    {
        var build = () => NightscoutBaseUri.For("ftp://ns.example");

        build.Should().Throw<ArgumentException>().WithMessage(NightscoutBaseUri.InvalidUrlMessage + "*");
    }

    [Theory]
    [InlineData("https://ns.example", "/api/v1/entries.json?count=10", "https://ns.example/api/v1/entries.json?count=10")]
    [InlineData("https://ns.example/nightscout", "/api/v1/entries.json?count=10", "https://ns.example/nightscout/api/v1/entries.json?count=10")]
    [InlineData("https://ns.example/nightscout/", "api/v1/status", "https://ns.example/nightscout/api/v1/status")]
    [InlineData("https://ns.example/nightscout?token=synthetic-token", "/api/v1/status", "https://ns.example/nightscout/api/v1/status")]
    [InlineData("https://ns.example/nightscout", "//other.example/api/v1/status", "https://ns.example/nightscout/other.example/api/v1/status")]
    [InlineData("https://ns.example/nightscout", "", "https://ns.example/nightscout/")]
    [InlineData("ns.example/nightscout", "/api/v1/status", "https://ns.example/nightscout/api/v1/status")]
    public void A_path_resolves_under_the_base_with_or_without_a_leading_slash(
        string configured, string pathAndQuery, string expected)
    {
        NightscoutBaseUri.Resolve(configured, pathAndQuery).AbsoluteUri.Should().Be(expected);
        NightscoutBaseUri.TryResolve(configured, pathAndQuery, out var resolved).Should().BeTrue();
        resolved!.AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://ns.example/nightscout", "/http://other.example/x")]
    [InlineData("https://ns.example/nightscout", "https://other.example/x")]
    [InlineData("https://ns.example/nightscout", "http:other.example/x")]
    [InlineData("https://ns.example/nightscout", "/../x")]
    [InlineData("https://ns.example/nightscout", "api/../../x")]
    [InlineData("https://ns.example", "https://ns.example.other.example/x")]
    public void A_path_that_resolves_outside_the_base_is_refused(string configured, string pathAndQuery)
    {
        NightscoutBaseUri.TryResolve(configured, pathAndQuery, out var resolved).Should().BeFalse();
        resolved.Should().BeNull();

        var resolve = () => NightscoutBaseUri.Resolve(configured, pathAndQuery);
        resolve.Should().Throw<ArgumentException>().WithMessage(NightscoutBaseUri.OutsideBaseMessage + "*");
    }

    [Fact]
    public void Resolving_under_an_address_that_is_not_http_is_refused()
    {
        NightscoutBaseUri.TryResolve("ftp://ns.example", "/api/v1/status", out _).Should().BeFalse();

        var resolve = () => NightscoutBaseUri.Resolve("ftp://ns.example", "/api/v1/status");
        resolve.Should().Throw<ArgumentException>().WithMessage(NightscoutBaseUri.InvalidUrlMessage + "*");
    }

    [Theory]
    [InlineData("https://ns.example/", "https://ns.example")]
    [InlineData("https://ns.example/nightscout/?token=synthetic-token", "https://ns.example/nightscout")]
    [InlineData("https://user:pass@NS.example/nightscout#section", "https://ns.example/nightscout")]
    [InlineData("ns.example/nightscout", "https://ns.example/nightscout")]
    [InlineData("ftp://ns.example/nightscout?token=synthetic-token", "ftp://ns.example/nightscout")]
    [InlineData("not an address#section", "not an address")]
    [InlineData("not an address", "not an address")]
    [InlineData("ftp://user:pass@ns.example/nightscout?token=synthetic-token", "ftp://ns.example/nightscout")]
    [InlineData("ftp://user@ns.example", "ftp://ns.example")]
    [InlineData("ftp://ns.example/a@b", "ftp://ns.example/a@b")]
    [InlineData("user:pass@not an address/nightscout", "not an address/nightscout")]
    [InlineData("", "")]
    public void Display_never_carries_a_query_user_info_or_fragment(string configured, string expected)
    {
        NightscoutBaseUri.Display(configured).Should().Be(expected);
    }
}
