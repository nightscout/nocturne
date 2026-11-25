using FluentAssertions;
using Nocturne.Connectors.Nightscout.Services;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
///     A Nightscout behind a reverse proxy can be served under a path that is itself the credential,
///     so a request that loses the path reaches a site that does not let it in.
/// </summary>
public class NightscoutAddressTests
{
    private const string Prefixed = "https://nscout.example.com/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";

    [Theory]
    [InlineData(Prefixed)]
    [InlineData(Prefixed + "/")]
    [InlineData(Prefixed + "//")]
    public void A_sub_path_is_kept_however_the_url_ends(string url)
    {
        var address = NightscoutAddress.Resolve(url);

        address.Base.Should().Be(Prefixed);
        address.For("api/v1/entries.json?count=1")
            .Should().Be(Prefixed + "/api/v1/entries.json?count=1");
        address.For("/api/v1/entries.json?count=1")
            .Should().Be(Prefixed + "/api/v1/entries.json?count=1", "a rooted path is still under the site");
    }

    [Fact]
    public void A_site_at_the_root_reads_as_before()
    {
        var address = NightscoutAddress.Resolve("https://ns.example.com/");

        address.Base.Should().Be("https://ns.example.com");
        address.For("/api/v1/profile.json").Should().Be("https://ns.example.com/api/v1/profile.json");
        address.Query.Should().BeNull();
        address.Token.Should().BeNull();
    }

    [Fact]
    public void A_host_without_a_scheme_is_read_as_https()
    {
        NightscoutAddress.Resolve("ns.example.com/site").Base.Should().Be("https://ns.example.com/site");
    }

    [Fact]
    public void The_query_goes_with_every_request()
    {
        var address = NightscoutAddress.Resolve(Prefixed + "/?token=reader-abc123&key=x%20y");

        address.Base.Should().Be(Prefixed);
        address.Token.Should().Be("reader-abc123");
        address.For("/api/v1/profile.json")
            .Should().Be(Prefixed + "/api/v1/profile.json?token=reader-abc123&key=x%20y");
        address.For("/api/v1/entries.json?count=1")
            .Should().Be(Prefixed + "/api/v1/entries.json?count=1&token=reader-abc123&key=x%20y");
    }

    [Fact]
    public void User_info_is_dropped_so_what_may_be_logged_carries_no_credential()
    {
        var address = NightscoutAddress.Resolve("https://user:pass@nscout.example.com/site?token=t");

        address.Base.Should().Be("https://nscout.example.com/site");
        address.Origin.Should().Be("https://nscout.example.com");
        address.BaseUri.AbsoluteUri.Should().Be("https://nscout.example.com/site/?token=t");
    }

    [Fact]
    public void An_address_read_back_from_its_base_uri_is_the_same_address()
    {
        var address = NightscoutAddress.Of(NightscoutAddress.Resolve(Prefixed + "?token=t").BaseUri);

        address.Base.Should().Be(Prefixed);
        address.For("api/v1/status").Should().Be(Prefixed + "/api/v1/status?token=t");
    }

    [Fact]
    public void A_fragment_is_not_sent()
    {
        NightscoutAddress.Resolve(Prefixed + "/#/report").For("/api/v1/status.json")
            .Should().Be(Prefixed + "/api/v1/status.json");
    }

    [Fact]
    public void The_socket_endpoint_is_under_the_site_path()
    {
        var address = NightscoutAddress.Resolve(Prefixed + "/");

        address.Origin.Should().Be("https://nscout.example.com");
        address.SocketIoPath.Should().Be("/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI/socket.io");
        NightscoutAddress.Resolve("https://ns.example.com").SocketIoPath.Should().Be("/socket.io");
    }

    [Theory]
    [InlineData("https://ns.example", "https://ns.example")]
    [InlineData("https://ns.example/nightscout/", "https://ns.example/nightscout")]
    [InlineData("https://ns.example/a/b//", "https://ns.example/a/b")]
    [InlineData("https://ns.example/nightscout?token=synthetic-token#section", "https://ns.example/nightscout")]
    [InlineData("https://user:pass@ns.example/nightscout", "https://ns.example/nightscout")]
    [InlineData("http://ns.example:1337/nightscout", "http://ns.example:1337/nightscout")]
    [InlineData("https://ns.example:443/nightscout", "https://ns.example/nightscout")]
    [InlineData("https://ns.example/night%20scout", "https://ns.example/night%20scout")]
    [InlineData("  https://ns.example/nightscout  ", "https://ns.example/nightscout")]
    [InlineData("ns.example", "https://ns.example")]
    [InlineData("ns.example:1337/nightscout", "https://ns.example:1337/nightscout")]
    public void The_base_keeps_scheme_host_port_and_path(string url, string expected)
    {
        NightscoutAddress.TryResolve(url, out var address).Should().BeTrue();

        address!.Base.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://ns.example/nightscout")]
    [InlineData("file:/etc/passwd")]
    [InlineData("/nightscout")]
    [InlineData("ns.example:notaport")]
    public void An_address_that_is_not_http_or_https_is_not_resolved(string? url)
    {
        NightscoutAddress.TryResolve(url, out var address).Should().BeFalse();

        address.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://ns.example.com")]
    public void A_url_that_is_not_http_is_refused(string url)
    {
        var act = () => NightscoutAddress.Resolve(url);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("https://ns.example", "/api/v1/entries.json?count=10", "https://ns.example/api/v1/entries.json?count=10")]
    [InlineData("https://ns.example/nightscout", "/api/v1/entries.json?count=10", "https://ns.example/nightscout/api/v1/entries.json?count=10")]
    [InlineData("https://ns.example/nightscout/", "api/v1/status", "https://ns.example/nightscout/api/v1/status")]
    [InlineData("https://ns.example/nightscout?token=abc", "/api/v1/status", "https://ns.example/nightscout/api/v1/status?token=abc")]
    [InlineData("https://ns.example/nightscout?token=abc", "/api/v1/entries.json?count=1", "https://ns.example/nightscout/api/v1/entries.json?count=1&token=abc")]
    [InlineData("https://ns.example/nightscout?token=abc", "", "https://ns.example/nightscout/?token=abc")]
    [InlineData("https://ns.example/nightscout", "//other.example/api/v1/status", "https://ns.example/nightscout/other.example/api/v1/status")]
    [InlineData("ns.example/nightscout", "/api/v1/status", "https://ns.example/nightscout/api/v1/status")]
    [InlineData("https://ns.example/nightscout?token=abc", "api/v1/status#x", "https://ns.example/nightscout/api/v1/status?token=abc")]
    [InlineData("https://ns.example/nightscout?token=abc", "api/v1/entries.json?count=1#x", "https://ns.example/nightscout/api/v1/entries.json?count=1&token=abc")]
    public void A_supplied_path_resolves_under_the_site_with_its_query(
        string url, string pathAndQuery, string expected)
    {
        NightscoutAddress.Resolve(url).TryFor(pathAndQuery, out var resolved).Should().BeTrue();

        resolved!.AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://ns.example/nightscout", "/http://other.example/x")]
    [InlineData("https://ns.example/nightscout", "https://other.example/x")]
    [InlineData("https://ns.example/nightscout", "http:other.example/x")]
    [InlineData("https://ns.example/nightscout", "/../x")]
    [InlineData("https://ns.example/nightscout", "api/../../x")]
    [InlineData("https://ns.example/nightscout?token=abc", "/../x")]
    [InlineData("https://ns.example", "https://ns.example.other.example/x")]
    public void A_supplied_path_that_resolves_outside_the_site_is_refused(string url, string pathAndQuery)
    {
        NightscoutAddress.Resolve(url).TryFor(pathAndQuery, out var resolved).Should().BeFalse();

        resolved.Should().BeNull();
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
        NightscoutAddress.Display(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://ns.example.com", "ns.example.com")]
    [InlineData("https://ns.example.com/", "ns.example.com")]
    [InlineData("https://NS.example.com:443/", "ns.example.com")]
    [InlineData("http://ns.example.com:80", "ns.example.com")]
    [InlineData("http://ns.example.com:1337", "ns.example.com:1337")]
    [InlineData("https://ns.example.com/s3cr3t", "ns.example.com/…")]
    [InlineData("https://ns.example.com/s3cr3t/", "ns.example.com/…")]
    [InlineData("https://ns.example.com:8443/s3cr3t/api", "ns.example.com:8443/…")]
    [InlineData("https://ns.example.com/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI", "ns.example.com/…")]
    [InlineData("https://ns.example.com/?token=synthetic-token", "ns.example.com")]
    [InlineData("https://ns.example.com/s3cr3t?token=synthetic-token#x", "ns.example.com/…")]
    [InlineData("https://user:pass@ns.example.com/s3cr3t", "ns.example.com/…")]
    [InlineData("https://user:pass@ns.example.com", "ns.example.com")]
    [InlineData("ns.example.com", "ns.example.com")]
    [InlineData("ns.example.com:1337/s3cr3t", "ns.example.com:1337/…")]
    [InlineData("not an address", "not an address")]
    [InlineData("user:pass@not an address/s3cr3t?token=synthetic-token", "not an address/…")]
    [InlineData("ftp://user:pass@ns.example.com/s3cr3t", "ns.example.com/…")]
    [InlineData(@"https://ns.example.com\s3cr3t", "ns.example.com/…")]
    [InlineData(@"ns.example.com\s3cr3t", "ns.example.com/…")]
    [InlineData(@"ftp://ns.example.com\s3cr3t", "ns.example.com/…")]
    public void Shown_keeps_host_and_port_and_marks_any_path_without_showing_it(string configured, string expected)
    {
        var shown = NightscoutAddress.Shown(configured);

        shown.Should().Be(expected);
        shown.Should().NotContainAny("s3cr3t", "AGYL", "token", "user", "pass", "?", "#", @"\");
        shown.Split('/').Skip(1).Should().HaveCountLessThanOrEqualTo(1).And.OnlyContain(path => path == "…");
    }

    [Theory]
    [InlineData("https://ns.example.com/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI/api", "ns.example.com/AGYL…/api")]
    [InlineData("https://ns.example.com/", "ns.example.com")]
    [InlineData("http://ns.example.com:1337/api/v1", "ns.example.com:1337/api/v1")]
    [InlineData("MongoDB: nightscout", "MongoDB: nightscout")]
    [InlineData("https://ns.example.com/0123456789abcde/", "ns.example.com/0123456789abcde")]
    [InlineData("https://ns.example.com/0123456789abcdef", "ns.example.com/0123…")]
    [InlineData("https://ns.example.com/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI/BGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI", "ns.example.com/AGYL…/BGYL…")]
    [InlineData("https://user:pass@NS.example.com:443/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI/?token=synthetic-token#x", "ns.example.com/AGYL…")]
    [InlineData("ns.example.com/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI", "ns.example.com/AGYL…")]
    [InlineData("a-very-long-host-name.example.com/nightscout", "a-very-long-host-name.example.com/nightscout")]
    public void Masked_keeps_host_port_and_path_and_cuts_each_long_path_segment(string configured, string expected)
    {
        NightscoutAddress.Masked(configured).Should().Be(expected);
    }
}
