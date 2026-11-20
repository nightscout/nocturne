using FluentAssertions;
using Nocturne.Services.SelfHostSetup;
using Xunit;

namespace Nocturne.Services.SelfHostSetup.Tests;

public class DomainInputTests
{
    [Theory]
    [InlineData("myname", "myname")]
    [InlineData("MyName", "myname")]
    [InlineData("myname.duckdns.org", "myname")]
    [InlineData(" my-name.DuckDNS.org ", "my-name")]
    [InlineData("https://myname.duckdns.org/", "myname")]
    public void ADuckDnsNameIsReadFromWhatPeoplePaste(string input, string expected)
    {
        DomainInput.TryParseDuckDnsName(input, out var name).Should().BeTrue();
        name.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two.labels")]
    [InlineData("under_score")]
    [InlineData("other.example.com")]
    public void AnythingThatIsNotOneDuckDnsLabelIsRefused(string? input)
    {
        DomainInput.TryParseDuckDnsName(input, out _).Should().BeFalse();
    }

    [Fact]
    public void ADuckDnsTokenIsAGuid()
    {
        DomainInput.TryParseDuckDnsToken(" 01234567-89ab-cdef-0123-456789abcdef ", out var token).Should().BeTrue();
        token.Should().Be("01234567-89ab-cdef-0123-456789abcdef");
        DomainInput.TryParseDuckDnsToken("not-a-token", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("nocturne.example.com", "nocturne.example.com")]
    [InlineData("Example.COM", "example.com")]
    [InlineData("https://nocturne.example.com/", "nocturne.example.com")]
    [InlineData("example.com.", "example.com")]
    public void ABaseDomainIsAHostnameOfTwoOrMoreLabels(string input, string expected)
    {
        DomainInput.TryParseBaseDomain(input, out var domain).Should().BeTrue();
        domain.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("203.0.113.7")]
    [InlineData("example.com:8443")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.example.com")]
    [InlineData("example.com/path")]
    public void ABaseDomainIsNeverAnAddressAPortOrAPath(string? input)
    {
        DomainInput.TryParseBaseDomain(input, out _).Should().BeFalse();
    }
}
