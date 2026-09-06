using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Services;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The support config is the only operator address a browser can read on a host with no session,
/// so the two channels it carries have to survive each other's absence.
/// </summary>
[Trait("Category", "Unit")]
public class SupportConfigTests
{
    private static SupportConfigResponse Read(OperatorConfiguration config)
    {
        var githubService = new GitHubIssueService(
            Mock.Of<IHttpClientFactory>(),
            Options.Create(new GitHubIssueOptions()),
            NullLogger<GitHubIssueService>.Instance);

        var controller = new SupportController(
            githubService,
            Options.Create(new GitHubIssueOptions()),
            Options.Create(config),
            NullLogger<SupportController>.Instance);

        return controller.GetSupportConfig().Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<SupportConfigResponse>().Subject;
    }

    [Fact]
    public void CarriesTheAccountPortalAsAUrlAndLabel()
    {
        var response = Read(new OperatorConfiguration
        {
            Name = "Nocturne.run",
            Support = new OperatorSupportConfiguration
            {
                AccountPortal = new OperatorAccountPortalConfiguration
                {
                    Url = "https://nocturne.run/billing/account",
                    Label = "Manage your subscription",
                },
            },
        });

        response.AccountPortal.Should().NotBeNull();
        response.AccountPortal!.Url.Should().Be("https://nocturne.run/billing/account");
        response.AccountPortal.Label.Should().Be("Manage your subscription");
    }

    [Fact]
    public void GivesBothChannelsTheSameLabelFallback()
    {
        var response = Read(new OperatorConfiguration
        {
            Name = "Nocturne.run",
            Support = new OperatorSupportConfiguration
            {
                AccountBilling = new OperatorSupportChannelConfiguration
                {
                    Mode = OperatorSupportMode.Api,
                    Url = "https://nocturne.run/billing/support/issues",
                },
                AccountPortal = new OperatorAccountPortalConfiguration
                {
                    Url = "https://nocturne.run/billing/account",
                },
            },
        });

        response.AccountPortal!.Label.Should().Be("Contact Nocturne.run");
        response.AccountBilling!.Label.Should().Be(response.AccountPortal.Label);
    }

    [Fact]
    public void LeavesTheAccountPortalLabelUnsetWhenNoOperatorIsNamed()
    {
        var response = Read(new OperatorConfiguration
        {
            Support = new OperatorSupportConfiguration
            {
                AccountPortal = new OperatorAccountPortalConfiguration
                {
                    Url = "https://example.test/account",
                },
            },
        });

        response.AccountPortal!.Label.Should().BeNull();
    }

    [Fact]
    public void OmitsAnAccountPortalWithNoUrl()
    {
        var response = Read(new OperatorConfiguration
        {
            Name = "Nocturne.run",
            Support = new OperatorSupportConfiguration
            {
                AccountPortal = new OperatorAccountPortalConfiguration { Url = "   " },
            },
        });

        response.AccountPortal.Should().BeNull();
    }

    [Fact]
    public void OmitsAnAccountPortalTheOperatorDidNotConfigure()
    {
        var response = Read(new OperatorConfiguration
        {
            Name = "Nocturne.run",
            Support = new OperatorSupportConfiguration
            {
                AccountBilling = new OperatorSupportChannelConfiguration
                {
                    Mode = OperatorSupportMode.Redirect,
                    Url = "https://nocturne.run/billing/account",
                },
            },
        });

        response.AccountPortal.Should().BeNull();
        response.AccountBilling.Should().NotBeNull();
    }

    [Fact]
    public void BindsTheAccountPortalFromConfigurationKeys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operator:Support:AccountPortal:Url"] = "https://nocturne.run/billing/account",
                ["Operator:Support:AccountPortal:Label"] = "Manage your subscription",
            })
            .Build();

        var bound = config.GetSection(OperatorConfiguration.SectionName).Get<OperatorConfiguration>();

        bound!.Support.AccountPortal!.Url.Should().Be("https://nocturne.run/billing/account");
        bound.Support.AccountPortal.Label.Should().Be("Manage your subscription");
    }
}
