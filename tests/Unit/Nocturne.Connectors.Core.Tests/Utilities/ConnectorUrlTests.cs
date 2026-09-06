using FluentAssertions;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Utilities;

public class ConnectorUrlTests
{
    [Theory]
    [InlineData("remote.example.com", "https://remote.example.com")]
    [InlineData("remote.example.com/", "https://remote.example.com")]
    [InlineData("mysite.example:1337", "https://mysite.example:1337")]
    [InlineData("[fd12:3456:789a::1]:1337", "https://[fd12:3456:789a::1]:1337")]
    [InlineData("[fd12::1]", "https://[fd12::1]")]
    // A host that merely starts with the scheme's letters is still a host.
    [InlineData("httpbin.example.com", "https://httpbin.example.com")]
    [InlineData("http://remote.example.com/", "http://remote.example.com")]
    [InlineData("https://remote.example.com", "https://remote.example.com")]
    [InlineData("HTTP://X/", "HTTP://X")]
    public void ResolveBase_NormalisesConfiguredUrl(string configured, string expected)
    {
        ConnectorUrl.ResolveBase(configured, "Test").Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveBase_BlankUrl_Throws(string? configured)
    {
        FluentActions.Invoking(() => ConnectorUrl.ResolveBase(configured, "Test"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Test URL is not configured");
    }

    [Theory]
    [InlineData("ftp://x")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("file:/etc/passwd")]
    [InlineData("/relative/path")]
    // A colon that introduces neither a port nor a scheme this connector speaks.
    [InlineData("host:abc")]
    [InlineData("host:")]
    public void ResolveBase_NonHttpScheme_Throws(string configured)
    {
        FluentActions.Invoking(() => ConnectorUrl.ResolveBase(configured, "Test"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Test URL must be http or https");
    }
}
