using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Nocturne.Services.SelfHostSetup;
using Xunit;

namespace Nocturne.Services.SelfHostSetup.Tests;

public sealed class SetupPageTests
{
    private static readonly IPAddress PublicIp = IPAddress.Parse("203.0.113.7");

    private static ContentHttpResult Render(SetupOutcome? outcome) =>
        SetupPage.Render(PublicIp, outcome).Should().BeOfType<ContentHttpResult>().Subject;

    [Fact]
    public void TheFirstVisitShowsBothFormsAndThePublicAddress()
    {
        var page = Render(outcome: null);

        page.StatusCode.Should().Be(200);
        page.ContentType.Should().Be("text/html; charset=utf-8");
        page.ResponseContent.Should()
            .Contain("action=\"/duckdns\"")
            .And.Contain("action=\"/domain\"")
            .And.Contain("<code>203.0.113.7</code>");
    }

    [Fact]
    public void ACompletedSetupLinksToTheNewHostnameWithoutTheForms()
    {
        var page = Render(new SetupOutcome.Completed("myname.duckdns.org"));

        page.StatusCode.Should().Be(200);
        page.ResponseContent.Should()
            .Contain("href=\"https://myname.duckdns.org/\"")
            .And.NotContain("<form");
    }

    [Fact]
    public void AnAlreadyCompleteSetupSaysSo()
    {
        var page = Render(new SetupOutcome.AlreadyComplete());

        page.StatusCode.Should().Be(200);
        page.ResponseContent.Should().Contain("already complete").And.NotContain("<form");
    }

    [Fact]
    public void ALockoutIsTooManyRequests()
    {
        var page = Render(new SetupOutcome.LockedOut());

        page.StatusCode.Should().Be(429);
        page.ResponseContent.Should().Contain("Restart this machine").And.NotContain("<form");
    }

    [Fact]
    public void ARejectedClaimIsABadRequestAndKeepsTheForms()
    {
        var page = Render(new SetupOutcome.ClaimRejected());

        page.StatusCode.Should().Be(400);
        page.ResponseContent.Should()
            .Contain($"{ClaimGuard.MaxFailures} wrong attempts")
            .And.Contain("action=\"/duckdns\"");
    }

    [Fact]
    public void InvalidInputIsEncodedBeforeItIsEchoed()
    {
        var page = Render(new SetupOutcome.Invalid("<script>alert(1)</script>"));

        page.StatusCode.Should().Be(400);
        page.ResponseContent.Should()
            .Contain("&lt;script&gt;alert(1)&lt;/script&gt;")
            .And.NotContain("<script>");
    }

    [Theory]
    [InlineData(DuckDnsOutcome.Unreachable, "Could not reach duckdns.org")]
    [InlineData(DuckDnsOutcome.Refused, "DuckDNS refused the update")]
    public void ADuckDnsFailureIsABadGatewayNamingTheCause(DuckDnsOutcome reason, string expected)
    {
        var page = Render(new SetupOutcome.DuckDnsFailed(reason));

        page.StatusCode.Should().Be(502);
        page.ResponseContent.Should().Contain(expected);
    }

    [Fact]
    public void PendingDnsListsEachRecordAndWhetherItWasFound()
    {
        var page = Render(new SetupOutcome.DnsPending("nocturne.example.com", PublicIp, ApexReady: true, WildcardReady: false));

        page.StatusCode.Should().Be(200);
        page.ResponseContent.Should()
            .Contain("<tr><td><code>nocturne.example.com</code></td><td><code>203.0.113.7</code></td><td>found</td></tr>")
            .And.Contain("<tr><td><code>*.nocturne.example.com</code></td><td><code>203.0.113.7</code></td><td>missing</td></tr>");
    }
}
