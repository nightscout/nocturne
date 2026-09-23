using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Services;
using Nocturne.Core.Contracts.Translations;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// The editor drafts against whatever this endpoint points it at, while the
/// contribution is written through <see cref="GitHubContributionOptions"/>. If
/// the two can disagree, a fork or test-branch instance silently discards
/// every contributed entry as unmatched.
/// </summary>
public class TranslationsControllerCatalogSourceTests
{
    private static string CatalogBaseUrl(GitHubContributionOptions options)
    {
        var controller = new TranslationsController(
            Mock.Of<ITranslationContributionService>(),
            Mock.Of<ITranslationDraftService>(),
            NullLogger<TranslationsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = controller.GetCatalogSource(Options.Create(options));
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<TranslationCatalogSourceResponse>().Subject.CatalogBaseUrl;
    }

    [Fact]
    public void Defaults_Point_At_The_Upstream_Repository()
    {
        CatalogBaseUrl(new GitHubContributionOptions())
            .Should().Be("https://raw.githubusercontent.com/nightscout/nocturne/main/src/Web/locales");
    }

    [Fact]
    public void Every_Configured_Coordinate_Moves_The_Catalog_Source()
    {
        CatalogBaseUrl(new GitHubContributionOptions
        {
            Owner = "acme",
            Repo = "nocturne-fork",
            BaseBranch = "i18n-staging",
            CatalogDir = "web/locales",
        }).Should().Be("https://raw.githubusercontent.com/acme/nocturne-fork/i18n-staging/web/locales");
    }

    [Theory]
    [InlineData("feature/i18n", "src/Web/locales", "https://raw.githubusercontent.com/nightscout/nocturne/feature/i18n/src/Web/locales")]
    [InlineData("main", "/src/Web/locales/", "https://raw.githubusercontent.com/nightscout/nocturne/main/src/Web/locales")]
    [InlineData("main", "", "https://raw.githubusercontent.com/nightscout/nocturne/main")]
    public void Separators_Survive_And_Stray_Slashes_Do_Not(
        string baseBranch, string catalogDir, string expected)
    {
        CatalogBaseUrl(new GitHubContributionOptions
        {
            BaseBranch = baseBranch,
            CatalogDir = catalogDir,
        }).Should().Be(expected);
    }

    [Fact]
    public void Configured_Values_Are_Url_Escaped()
    {
        CatalogBaseUrl(new GitHubContributionOptions
        {
            Owner = "a c",
            Repo = "r?p",
            BaseBranch = "br anch",
            CatalogDir = "lo cales",
        }).Should().Be("https://raw.githubusercontent.com/a%20c/r%3Fp/br%20anch/lo%20cales");
    }
}
