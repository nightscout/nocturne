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
    [InlineData("")]
    [InlineData("ftp://ns.example.com")]
    public void A_url_that_is_not_http_is_refused(string url)
    {
        var act = () => NightscoutAddress.Resolve(url);

        act.Should().Throw<InvalidOperationException>();
    }
}
