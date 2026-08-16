using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Models.Content;
using Nocturne.Core.Models.Translations;

namespace Nocturne.API.Tests.Services;

public class GitHubContentServiceTests
{
    // The relay is [AllowAnonymous]; each operand of the gate is pinned on its
    // own, so flipping either sense or the && to an || fails one of these.
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, "pat", false)]
    [InlineData(true, "pat", true)]
    public void AcceptsRelay_Needs_Both_The_Opt_In_And_A_Local_Pat(
        bool optedIn, string? pat, bool expected)
    {
        var options = Options.Create(new GitHubContributionOptions
        {
            AcceptRelayedContributions = optedIn,
            ContributionsPat = pat,
        });
        var httpClientFactory = Mock.Of<IHttpClientFactory>();

        new GitHubContentService(
            new GitHubPrClient(httpClientFactory, NullLogger<GitHubPrClient>.Instance),
            httpClientFactory, options, NullLogger<GitHubContentService>.Instance)
            .AcceptsRelay.Should().Be(expected);
    }

    [Theory]
    [InlineData("src/Web/packages/portal/src/content/blog/my-post.svx", true)]
    [InlineData("src/Web/packages/portal/src/content/docs/authentication/github.svx", true)]
    [InlineData("src/Web/packages/portal/src/content/docs/windows-widget.svx", true)]
    [InlineData("src/Web/packages/portal/src/content/blog/post.2024.svx", true)]
    [InlineData("src/Web/packages/portal/src/content/blog/../../../../API/Program.cs", false)]
    [InlineData("src/Web/packages/portal/src/content/blog/post.md", false)]
    [InlineData("src/Web/packages/portal/src/content/email/steal.svx", false)]
    [InlineData("src/API/Nocturne.API/Program.cs", false)]
    [InlineData(".github/workflows/deploy.yml", false)]
    [InlineData("src/Web/packages/portal/src/content/blog/UPPER.svx", false)]
    [InlineData("src/Web/packages/portal/src/content/blog/.hidden.svx", false)]
    [InlineData("src/Web/packages/portal/src/content/blog//double.svx", false)]
    [InlineData("src/Web/packages/portal/src/content/blog/my-post.v2.svx", true)]
    [InlineData("src/Web/packages/portal/src/content/docs/a_b/c-d.e.svx", true)]
    // Consecutive separators are rejected to keep the stem, the slug and the
    // branch name the same recognisable string, not because they would be
    // unsafe: BranchSlug already reduces them to a legal ref.
    [InlineData("src/Web/packages/portal/src/content/blog/a..b.svx", false)]
    [InlineData("src/Web/packages/portal/src/content/blog/a--b.svx", false)]
    [InlineData("src/Web/packages/portal/src/content/blog/a._b.svx", false)]
    [InlineData("src/Web/packages/portal/src/content/blog/post-.svx", false)]
    [InlineData("src/Web/packages/portal/src/content/b..log/post.svx", false)]
    [InlineData("src/Web/packages/portal/src/content/blog/post.svx\n", false)]
    public void AllowedPathPattern_Constrains_To_Portal_Content(string path, bool allowed)
    {
        GitHubContentService.AllowedPathPattern().IsMatch(path).Should().Be(allowed);
    }

    /// <summary>
    /// Pins the leading anchor: every one of these carries an allowed suffix
    /// behind a hostile prefix, so an unanchored pattern would match them all.
    /// </summary>
    [Theory]
    [InlineData("evil/src/Web/packages/portal/src/content/blog/x.svx")]
    [InlineData("../src/Web/packages/portal/src/content/blog/x.svx")]
    [InlineData("xsrc/Web/packages/portal/src/content/blog/x.svx")]
    public void AllowedPathPattern_Is_Anchored_At_The_Start(string path)
    {
        GitHubContentService.AllowedPathPattern().IsMatch(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("src/Web/packages/portal/src/content/blog/my-post.svx", "my-post")]
    [InlineData("src/Web/packages/portal/src/content/blog/post.2024.svx", "post-2024")]
    // The allowlist rejects this stem, but only for slug predictability: were
    // it admitted, the branch name would still be a legal ref.
    [InlineData("src/Web/packages/portal/src/content/blog/a..b.svx", "a--b")]
    public void BranchSlug_Reduces_The_Stem_To_A_Legal_Ref_Segment(string path, string expected)
    {
        GitHubContentService.BranchSlug(path).Should().Be(expected);
    }

    private static ContentContributionRequest Request() => new()
    {
        Path = "src/Web/packages/portal/src/content/blog/my-post.svx",
        Content = "---\ntitle: My Post\n---\n\nBody",
        Title = "My Post",
        Contributor = new ContributionContributorDto { Name = "Jane Doe", GitHubUsername = "janedoe" },
        Note = "First draft",
    };

    [Fact]
    public void CommitMessage_Includes_Slug_Attribution_And_Trailer()
    {
        var message = GitHubContentService.BuildCommitMessage(Request(), created: true);

        message.Should().StartWith("content: add my-post");
        message.Should().Contain("Contributed by Jane Doe");
        message.Should().Contain("Co-authored-by: janedoe <janedoe@users.noreply.github.com>");
    }

    [Fact]
    public void PrBody_Lists_File_Contributor_And_Note()
    {
        var body = GitHubContentService.BuildPrBody(Request(), created: false);

        body.Should().StartWith("Updated content");
        body.Should().Contain("`src/Web/packages/portal/src/content/blog/my-post.svx`");
        body.Should().Contain("**Contributor:** Jane Doe (@janedoe)");
        body.Should().Contain("First draft");
    }

    [Fact]
    public void PrBody_Removes_Url_And_Shorthand_References_From_The_Contributor_Name()
    {
        var request = Request() with
        {
            Contributor = new ContributionContributorDto
            {
                Name = "Jane fixes GH-123 see https://github.com/nightscout/nocturne/issues/456",
            },
        };

        var body = GitHubContentService.BuildPrBody(request, created: true);

        // Neither form carries a "#" or "@", so the escapes miss both: they
        // would backlink at PR-open and auto-close on merge.
        body.Should().Contain("**Contributor:** Jane fixes GH 123 see");
        body.Should().NotContain("GH-123");
        body.Should().NotContain("github.com");
    }

    [Fact]
    public void CommitMessage_Removes_Url_And_Shorthand_References_From_The_Contributor_Name()
    {
        var request = Request() with
        {
            Contributor = new ContributionContributorDto
            {
                Name = "Jane htt#ps://github.com/nightscout/nocturne/issues/9 GH#-7",
                Email = "jane@example.com",
            },
        };

        var message = GitHubContentService.BuildCommitMessage(request, created: true);

        // Dropping "#" reassembles both forms, so they have to be neutralised
        // after that pass. The co-author trailer is a commit-message sink too.
        message.Should().Contain("Contributed by Jane  GH 7 via the in-app content studio.");
        message.Should().Contain("Co-authored-by: Jane  GH 7 <jane@example.com>");
        message.Should().NotContain("github.com");
        message.Should().NotContain("GH-7");
    }

    [Fact]
    public void CommitMessage_Sanitizes_Injection_Attempts()
    {
        var request = Request() with
        {
            Contributor = new ContributionContributorDto
            {
                Name = "Jane\nSigned-off-by: maintainer <m@x>",
            },
        };

        var message = GitHubContentService.BuildCommitMessage(request, created: false);

        var lines = message.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().NotContain(l => l.StartsWith("Signed-off-by"));
    }

    [Fact]
    public void PrBody_Gets_The_Same_Name_And_Note_Treatment_As_Translations()
    {
        const string note = "Fixes https://github.com/nightscout/nocturne/issues/123\n`x`";
        var request = Request() with
        {
            Contributor = new ContributionContributorDto { Name = "Jane fixes #123 cc @someuser" },
            Note = note,
        };

        var body = GitHubContentService.BuildPrBody(request, created: false);

        body.Should().Contain(@"- **Contributor:** Jane fixes \#123 cc \@someuser");
        body.ReplaceLineEndings("\n").Should().Contain($"```\n{note}\n```");
    }

    [Fact]
    public void CommitMessage_Drops_Mentions_And_References_From_The_Contributor_Name()
    {
        var request = Request() with
        {
            Contributor = new ContributionContributorDto
            {
                Name = "Jane fixes #123 cc @someuser",
                Email = "jane@example.com",
            },
        };

        var message = GitHubContentService.BuildCommitMessage(request, created: false);

        message.Should().Contain("Contributed by Jane fixes 123 cc someuser");
        message.Should().Contain("Co-authored-by: Jane fixes 123 cc someuser <jane@example.com>");
        message.Should().NotContain("#123");
        message.Should().NotContain("@someuser");
    }
}
