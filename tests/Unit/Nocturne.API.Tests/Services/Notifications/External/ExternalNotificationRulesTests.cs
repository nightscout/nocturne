using FluentAssertions;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Notifications.External;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Notifications.External;

public class ExternalNotificationRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private static ExternalNotificationRequest Valid() => new()
    {
        Key = "plan.renewal:2026-12",
        Title = "Your plan renews soon",
        Body = "It renews on 1 December.\nNo action is needed.",
    };

    private static string? Error(ExternalNotificationRequest request, params string[] domains) =>
        ExternalNotificationRules.Validate(request, domains, Now, out _)?.Error;

    [Fact]
    public void AValidNoticeExpiresInThirtyDays()
    {
        ExternalNotificationRules.Validate(Valid(), [], Now, out var expiresAt).Should().BeNull();
        expiresAt.Should().Be(Now.AddDays(30));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/")]
    public void AKeyOutsideTheAlphabetIsRefused(string key)
    {
        var request = Valid();
        request.Key = key;
        Error(request).Should().Be("invalid_key");
    }

    [Fact]
    public void AKeyLongerThanAHundredIsRefused()
    {
        var request = Valid();
        request.Key = new string('k', 101);
        Error(request).Should().Be("invalid_key");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    [InlineData("bell\u0007")]
    [InlineData("bidi‮")]
    public void ATitleThatIsNotOneLineOfTextIsRefused(string title)
    {
        var request = Valid();
        request.Title = title;
        Error(request).Should().Be("invalid_title");
    }

    [Fact]
    public void ABodyOverFiveHundredCharactersIsRefused()
    {
        var request = Valid();
        request.Body = new string('b', 501);
        Error(request).Should().Be("invalid_body");
    }

    [Fact]
    public void MarkupInTheBodyIsKeptAsText()
    {
        var request = Valid();
        request.Body = "<script>alert(1)</script>";
        Error(request).Should().BeNull("the tray shows it as text, never as markup");
    }

    [Theory]
    [InlineData("https://nocturne.run/plan")]
    [InlineData("https://billing.nocturne.run/plan?x=1")]
    [InlineData("https://BILLING.Nocturne.Run/plan")]
    public void ALinkOnAnAllowedDomainOrASubdomainPasses(string url)
    {
        var request = Valid();
        request.Link = new ExternalNotificationLink { Label = "See your plan", Url = url };
        Error(request, "nocturne.run").Should().BeNull();
    }

    [Theory]
    [InlineData("https://evil.example/plan")]
    [InlineData("https://nocturne.run.evil.example/plan")]
    [InlineData("https://evilnocturne.run/plan")]
    public void ALinkOffTheAllowedDomainsIsRefused(string url)
    {
        var request = Valid();
        request.Link = new ExternalNotificationLink { Label = "See your plan", Url = url };
        Error(request, "nocturne.run").Should().Be("link_domain_not_allowed");
    }

    [Fact]
    public void ASenderWithNoDomainMayNotLink()
    {
        var request = Valid();
        request.Link = new ExternalNotificationLink { Label = "See your plan", Url = "https://nocturne.run/plan" };
        Error(request).Should().Be("link_domain_not_allowed");
    }

    [Theory]
    [InlineData("http://nocturne.run/plan")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative")]
    [InlineData("https://user:pass@nocturne.run/plan")]
    public void ALinkThatIsNotPlainHttpsIsRefused(string url)
    {
        var request = Valid();
        request.Link = new ExternalNotificationLink { Label = "See your plan", Url = url };
        Error(request, "nocturne.run").Should().Be("invalid_link");
    }

    [Fact]
    public void ALinkWithNoLabelIsRefused()
    {
        var request = Valid();
        request.Link = new ExternalNotificationLink { Label = " ", Url = "https://nocturne.run/plan" };
        Error(request, "nocturne.run").Should().Be("invalid_link");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(91)]
    public void AnExpiryInThePastOrPastNinetyDaysIsRefused(int days)
    {
        var request = Valid();
        request.ExpiresAt = new DateTimeOffset(Now.AddDays(days));
        Error(request).Should().Be("invalid_expiry");
    }

    [Fact]
    public void AnExpiryOfNinetyDaysIsKept()
    {
        var request = Valid();
        request.ExpiresAt = new DateTimeOffset(Now.AddDays(90));
        ExternalNotificationRules.Validate(request, [], Now, out var expiresAt).Should().BeNull();
        expiresAt.Should().Be(Now.AddDays(90));
    }

    [Fact]
    public void AnAppLinksOnlyToTheHostsOfItsHttpsRedirectUris()
    {
        ExternalNotificationRules.AppLinkDomains(
            [
                "https://app.sugarmate.io/callback",
                "http://insecure.example/callback",
                "https://localhost:8443/callback",
                "https://127.0.0.1/callback",
                "sugarmate://callback",
            ])
            .Should().Equal("app.sugarmate.io");
    }

    [Fact]
    public void TheOperatorLinksToItsListedDomains()
    {
        new OperatorConfiguration
        {
            NotificationLinkDomains = ["nocturne.run", " "],
            Support = new OperatorSupportConfiguration
            {
                AccountPortal = new OperatorAccountPortalConfiguration { Url = "https://portal.example/" },
            },
        }.ResolveNotificationLinkDomains().Should().Equal("nocturne.run");
    }

    [Fact]
    public void WithNoListTheOperatorLinksToItsSupportDomains()
    {
        new OperatorConfiguration
        {
            Support = new OperatorSupportConfiguration
            {
                AccountBilling = new OperatorSupportChannelConfiguration { Url = "https://billing.example/help" },
                AccountPortal = new OperatorAccountPortalConfiguration { Url = "https://portal.example/" },
            },
        }.ResolveNotificationLinkDomains().Should().Equal("billing.example", "portal.example");
    }

    [Fact]
    public void WithNothingConfiguredTheOperatorLinksNowhere()
    {
        new OperatorConfiguration().ResolveNotificationLinkDomains().Should().BeEmpty();
    }
}
