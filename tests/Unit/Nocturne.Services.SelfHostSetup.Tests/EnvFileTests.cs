using FluentAssertions;
using Nocturne.Services.SelfHostSetup;
using Xunit;

namespace Nocturne.Services.SelfHostSetup.Tests;

public class EnvFileTests
{
    [Fact]
    public void AnExistingKeyIsReplacedInPlace()
    {
        var result = EnvFile.Apply(
            "INSTANCE_KEY=secret\nBASE_DOMAIN=\nPOSTGRES_PASSWORD=pw\n",
            new Dictionary<string, string> { ["BASE_DOMAIN"] = "me.duckdns.org" });

        result.Should().Be("INSTANCE_KEY=secret\nBASE_DOMAIN=me.duckdns.org\nPOSTGRES_PASSWORD=pw\n");
    }

    [Fact]
    public void AMissingKeyIsAppended()
    {
        var result = EnvFile.Apply(
            "INSTANCE_KEY=secret\n",
            new Dictionary<string, string> { ["COMPOSE_PROFILES"] = "duckdns" });

        result.Should().Be("INSTANCE_KEY=secret\nCOMPOSE_PROFILES=duckdns\n");
    }

    [Fact]
    public void ACommentNamingTheKeyIsLeftAloneAndTheKeyAppended()
    {
        var result = EnvFile.Apply(
            "# BASE_DOMAIN=example.com\n",
            new Dictionary<string, string> { ["BASE_DOMAIN"] = "me.duckdns.org" });

        result.Should().Be("# BASE_DOMAIN=example.com\nBASE_DOMAIN=me.duckdns.org\n");
    }

    [Fact]
    public void AKeyThatPrefixesAnotherIsNotConfusedWithIt()
    {
        var result = EnvFile.Apply(
            "DUCKDNS_TOKEN_OLD=x\n",
            new Dictionary<string, string> { ["DUCKDNS_TOKEN"] = "t" });

        result.Should().Be("DUCKDNS_TOKEN_OLD=x\nDUCKDNS_TOKEN=t\n");
    }

    [Fact]
    public void AFileWithoutATrailingNewlineGetsOneBeforeAppending()
    {
        var result = EnvFile.Apply(
            "INSTANCE_KEY=secret",
            new Dictionary<string, string> { ["BASE_DOMAIN"] = "me.duckdns.org" });

        result.Should().Be("INSTANCE_KEY=secret\nBASE_DOMAIN=me.duckdns.org\n");
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void AValueSpanningLinesIsRefused(string value)
    {
        var act = () => EnvFile.Apply("", new Dictionary<string, string> { ["BASE_DOMAIN"] = value });

        act.Should().Throw<ArgumentException>();
    }
}
