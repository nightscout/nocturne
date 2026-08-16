using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Models.Translations;

namespace Nocturne.API.Tests.Services;

public class GitHubTranslationServiceTests
{
    private static GitHubTranslationService Service(GitHubContributionOptions options) =>
        new(new GitHubPrClient(Mock.Of<IHttpClientFactory>(), NullLogger<GitHubPrClient>.Instance),
            Mock.Of<IHttpClientFactory>(), Options.Create(options),
            NullLogger<GitHubTranslationService>.Instance);

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, "pat", false)]
    [InlineData(true, "pat", true)]
    public void AcceptsRelay_Needs_Both_The_Opt_In_And_A_Local_Pat(
        bool optedIn, string? pat, bool expected)
    {
        Service(new GitHubContributionOptions
        {
            AcceptRelayedContributions = optedIn,
            ContributionsPat = pat,
        }).AcceptsRelay.Should().Be(expected);
    }

    private static TranslationContributionRequest Request(
        string? gitHubUsername = null, string? email = null, string? note = null,
        string name = "Jane Doe") => new()
    {
        Locale = "fr",
        Entries = [new TranslationEntryDto { MsgId = "Hello", Translations = ["Bonjour"] }],
        Contributor = new ContributionContributorDto
        {
            Name = name,
            GitHubUsername = gitHubUsername,
            Email = email,
        },
        Note = note,
    };

    /// <summary>
    /// The note body between the fence lines, plus the fences themselves.
    /// </summary>
    private static (string Fence, string Body) NoteFence(string body)
    {
        const string heading = "## Contributor note";
        var section = body[(body.IndexOf(heading, StringComparison.Ordinal) + heading.Length)..]
            .ReplaceLineEndings("\n");
        var lines = section.Split('\n').SkipWhile(l => l.Length == 0).ToList();
        var fence = lines[0];
        var closing = lines.FindLastIndex(l => l == fence);
        return (fence, string.Join('\n', lines.Skip(1).Take(closing - 1)));
    }

    [Fact]
    public void CoAuthorTrailer_Prefers_GitHub_Username()
    {
        var trailer = GitHubPrClient.CoAuthorTrailer(
            Request(gitHubUsername: "janedoe", email: "jane@example.com").Contributor);

        trailer.Should().Be("Co-authored-by: janedoe <janedoe@users.noreply.github.com>");
    }

    [Fact]
    public void CoAuthorTrailer_Falls_Back_To_Email()
    {
        var trailer = GitHubPrClient.CoAuthorTrailer(
            Request(email: "jane@example.com").Contributor);

        trailer.Should().Be("Co-authored-by: Jane Doe <jane@example.com>");
    }

    [Fact]
    public void CoAuthorTrailer_Is_Null_Without_Identity()
    {
        GitHubPrClient.CoAuthorTrailer(Request().Contributor).Should().BeNull();
    }

    [Fact]
    public void CommitMessage_Includes_Attribution_And_Trailer()
    {
        var message = GitHubTranslationService.BuildCommitMessage(
            Request(gitHubUsername: "janedoe"), applied: 3);

        message.Should().StartWith("chore(i18n): fr translations via in-app contribution");
        message.Should().Contain("Applies 3 messages contributed by Jane Doe.");
        message.Should().Contain("Co-authored-by: janedoe");
    }

    [Fact]
    public void CommitMessage_Strips_Trailer_Injection_From_Contributor_Fields()
    {
        var request = Request() with
        {
            Contributor = new ContributionContributorDto
            {
                Name = "Jane\nCo-authored-by: victim <victim@example.com>",
                Email = "jane@example.com\nSigned-off-by: maintainer <m@x>",
            },
        };

        var message = GitHubTranslationService.BuildCommitMessage(request, applied: 1);

        // Sanitization keeps injected text inert by collapsing it onto the
        // legitimate lines: no attacker-controlled line can become a trailer.
        var lines = message.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.Count(l => l.StartsWith("Co-authored-by:")).Should().Be(1);
        lines.Should().NotContain(l => l.StartsWith("Signed-off-by"));
        lines.Single(l => l.StartsWith("Co-authored-by:")).Should().NotContain("<victim@example.com>");
    }

    [Fact]
    public void SanitizeMetadata_Removes_Control_Chars_And_Angle_Brackets()
    {
        GitHubPrClient.SanitizeMetadata("a\r\nb<c>d\te ")
            .Should().Be("abcde");
    }

    [Fact]
    public void PrBody_Keeps_Unmatched_MsgIds_On_One_Line()
    {
        var body = GitHubTranslationService.BuildPrBody(
            Request(),
            new PoEditResult
            {
                Text = "",
                Applied = 1,
                Unmatched = [new TranslationUnmatchedEntry { MsgId = "evil\n</details>\n# heading" }],
            });

        body.Should().NotContain("evil\n");
        body.Should().Contain("evil\\n</details>\\n# heading");
    }

    [Fact]
    public void PrBody_Lists_Contributor_Note_And_Unmatched()
    {
        var body = GitHubTranslationService.BuildPrBody(
            Request(gitHubUsername: "janedoe", note: "Reviewed against the app UI"),
            new PoEditResult
            {
                Text = "",
                Applied = 2,
                Unmatched = [new TranslationUnmatchedEntry { MsgId = "Gone message", Context = "page-title" }],
            });

        body.Should().Contain("**Contributor:** Jane Doe (@janedoe)");
        body.Should().Contain("**Messages updated:** 2");
        body.Should().Contain("`Gone message` (context: page-title)");
        body.Should().Contain("## Contributor note");
        NoteFence(body).Body.Should().Be("Reviewed against the app UI");
    }

    [Fact]
    public void PrBody_Renders_Note_Inside_An_Inert_Code_Fence()
    {
        const string note = "cc @nightscout/maintainers\nFixes #1234\n`rm -rf`\n\n## Injected heading\n<img src=x onerror=alert(1)>";
        var body = GitHubTranslationService.BuildPrBody(
            Request(note: note),
            new PoEditResult { Text = "", Applied = 1, Unmatched = [] });

        var (fence, fenced) = NoteFence(body);
        fence.Should().Be("```");
        fenced.Should().Be(note);

        // Exactly one opening and one closing fence: the note is wholly inside.
        var section = body[(body.IndexOf("## Contributor note", StringComparison.Ordinal))..];
        section.Split('\n').Count(l => l.TrimEnd('\r') == fence).Should().Be(2);
    }

    [Fact]
    public void PrBody_Neutralizes_Issue_Urls_In_The_Note()
    {
        const string url = "Fixes https://github.com/nightscout/nocturne/issues/123";
        var body = GitHubTranslationService.BuildPrBody(
            Request(note: url),
            new PoEditResult { Text = "", Applied = 1, Unmatched = [] });

        var (_, fenced) = NoteFence(body);
        fenced.Should().Be(url);
        body.Replace(fenced, "").Should().NotContain("github.com/nightscout/nocturne/issues/123");
    }

    [Fact]
    public void PrBody_Fence_Cannot_Be_Closed_By_Backticks_In_The_Note()
    {
        const string note = "```\n## escaped heading\n```\nstill inside `````";
        var body = GitHubTranslationService.BuildPrBody(
            Request(note: note),
            new PoEditResult { Text = "", Applied = 1, Unmatched = [] });

        // The fence must be longer than the longest backtick run in the note,
        // or the note closes it early and the tail renders as markdown.
        var (fence, fenced) = NoteFence(body);
        fence.Should().Be("``````");
        fenced.Should().Be(note);
        fenced.Split('\n').Should().NotContain(
            l => l.TrimStart().StartsWith(fence, StringComparison.Ordinal));
    }

    [Fact]
    public void PrBody_Escapes_Mentions_And_References_In_The_Contributor_Name()
    {
        var body = GitHubTranslationService.BuildPrBody(
            Request(name: @"Jane fixes #123 cc @someuser \x"),
            new PoEditResult { Text = "", Applied = 1, Unmatched = [] });

        body.Should().Contain(@"- **Contributor:** Jane fixes \#123 cc \@someuser \\x");
        body.Should().NotContain("cc @someuser");
    }

    [Fact]
    public void CommitMessage_Drops_Mentions_And_References_From_The_Contributor_Name()
    {
        var request = Request(email: "jane@example.com", name: "Jane fixes #123 cc @someuser");
        var message = GitHubTranslationService.BuildCommitMessage(request, applied: 1);

        message.Should().Contain("contributed by Jane fixes 123 cc someuser.");
        message.Should().Contain("Co-authored-by: Jane fixes 123 cc someuser <jane@example.com>");
        message.Should().NotContain("#123");
        message.Should().NotContain("@someuser");
    }

    [Fact]
    public void PrBody_Removes_Url_And_Shorthand_References_From_The_Contributor_Name()
    {
        var body = GitHubTranslationService.BuildPrBody(
            Request(name: "Jane fixes GH-123 see https://github.com/nightscout/nocturne/issues/456"),
            new PoEditResult { Text = "", Applied = 1, Unmatched = [] });

        body.Should().Contain("- **Contributor:** Jane fixes GH 123 see");
        body.Should().NotContain("GH-123");
        body.Should().NotContain("github.com");
    }

    [Fact]
    public void CommitMessage_Removes_Url_And_Shorthand_References_From_The_Contributor_Name()
    {
        var request = Request(
            email: "jane@example.com",
            name: "Jane fixes GH-123 see https://github.com/nightscout/nocturne/issues/456");
        var message = GitHubTranslationService.BuildCommitMessage(request, applied: 1);

        message.Should().Contain("contributed by Jane fixes GH 123 see.");
        message.Should().Contain("Co-authored-by: Jane fixes GH 123 see <jane@example.com>");
        message.Should().NotContain("GH-123");
        message.Should().NotContain("github.com");
    }

    [Fact]
    public void CommitMessage_Neutralizes_References_Spliced_By_Dropping_A_Hash()
    {
        var request = Request(name: "Jane htt#ps://github.com/nightscout/nocturne/issues/9 GH#-7");
        var message = GitHubTranslationService.BuildCommitMessage(request, applied: 1);

        // Dropping "#" reassembles both forms, so they have to be neutralised
        // after that pass rather than before it.
        message.Should().NotContain("github.com");
        message.Should().NotContain("GH-7");
        message.Should().Contain("contributed by Jane  GH 7.");
    }

    [Fact]
    public void PrBody_Strips_Control_Chars_From_The_Note()
    {
        var body = GitHubTranslationService.BuildPrBody(
            Request(note: "clean\u0000er\u001b[31m text"),
            new PoEditResult { Text = "", Applied = 1, Unmatched = [] });

        NoteFence(body).Body.Should().Be("cleaner[31m text");
        body.Should().NotContain("\u0000");
        body.Should().NotContain("\u001b");
    }

    [Fact]
    public void PrBody_Keeps_Note_Backslashes_Verbatim_Inside_The_Fence()
    {
        var body = GitHubTranslationService.BuildPrBody(
            Request(note: @"trailing backslash \@nobody"),
            new PoEditResult { Text = "", Applied = 1, Unmatched = [] });

        // Inside a code fence a backslash is literal and the "@" is inert, so
        // the note is neither escaped nor mangled.
        NoteFence(body).Body.Should().Be(@"trailing backslash \@nobody");
    }

    [Fact]
    public void PrBody_Removes_Backticks_From_Unmatched_MsgIds()
    {
        var body = GitHubTranslationService.BuildPrBody(
            Request(),
            new PoEditResult
            {
                Text = "",
                Applied = 1,
                // A backslash is literal inside a CommonMark code span, so an
                // escaped backtick would still terminate the span.
                Unmatched = [new TranslationUnmatchedEntry { MsgId = "evil` <img src=x> `rest" }],
            });

        body.Should().Contain("`evil <img src=x> rest`");
        body.Should().NotContain(@"\`");
    }

    [Fact]
    public async Task RelayAsync_Forwards_The_Relays_Own_Rejection_Reason()
    {
        // A relay 422 is not always an unmatched catalog: contributor
        // validation on the far side rejects with the same status, and a
        // hardcoded message would tell the contributor to refresh and retry
        // when refreshing cannot help.
        var service = RelayService(HttpStatusCode.UnprocessableEntity,
            """{"detail":"Invalid GitHub username","status":422}""");

        var act = () => service.RelayAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<ContributionRejectedException>())
            .WithMessage("Invalid GitHub username");
    }

    [Fact]
    public async Task RelayAsync_Falls_Back_When_The_Rejection_Carries_No_Detail()
    {
        var service = RelayService(HttpStatusCode.UnprocessableEntity, "not problem details");

        var act = () => service.RelayAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<ContributionRejectedException>())
            .WithMessage("The contribution was rejected by the relay.");
    }

    private static GitHubTranslationService RelayService(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(http);

        return new GitHubTranslationService(
            new GitHubPrClient(factory.Object, NullLogger<GitHubPrClient>.Instance),
            factory.Object,
            Options.Create(new GitHubContributionOptions()),
            NullLogger<GitHubTranslationService>.Instance);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
