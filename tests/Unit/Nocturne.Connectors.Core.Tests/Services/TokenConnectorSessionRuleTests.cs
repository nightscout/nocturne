using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// A token connector reaches its token through the session the base hands its sync. A sync that asks
/// the token provider for a token itself skips the base's one sign-in gate, so a refused sign-in
/// loses the reason shown to the user.
/// </summary>
/// <remarks>
/// A source scan over the text of each connector service deriving from <c>TokenConnectorService</c>.
/// It cannot see a token reached through a helper in another file, an aliased provider member, or a
/// session passed on before its <c>.Token</c> is read.
/// </remarks>
public partial class TokenConnectorSessionRuleTests
{
    [GeneratedRegex(@"\bclass\s+\w+[^{;]*?:[^{;]*?\bTokenConnectorService<")]
    private static partial Regex DerivesFromTokenConnectorService();

    [GeneratedRegex(@"\b(?:GetValidTokenAsync|ValidTokenAsync)\s*\(")]
    private static partial Regex FetchesATokenItself();

    [GeneratedRegex(@"GetCachedSessionAsync\s*\([^)]*\)\s*\)?\s*\??\.\s*Token\b")]
    private static partial Regex ReadsTokenOffTheCachedSession();

    [GeneratedRegex(@"\b(\w+)\s*=\s*(?:await\s+)?[\w.]*GetCachedSessionAsync\s*\(")]
    private static partial Regex AssignsTheCachedSession();

    [Fact]
    public void NoTokenConnectorFetchesItsOwnToken()
    {
        var offences = Sources()
            .SelectMany(file => OffencesIn(Path.GetFileName(file), File.ReadAllText(file)))
            .ToList();

        offences.Should().BeEmpty(
            "a token connector reaches its token through the TokenSession its SyncWithTokenAsync receives, " +
            "so a refused sign-in keeps the reason the base records");
    }

    [Fact]
    public void TheScanFlagsAProviderFetchAndACachedSessionTokenAndPassesTheSession()
    {
        const string source = """
            class Fetches : TokenConnectorService<Config, Provider>
            {
                async Task A() { var t = await TokenProvider.GetValidTokenAsync(config, ct); }
                async Task B() { var t = await ValidTokenAsync(config, ct); }
                async Task C() { var t = (await TokenProvider.GetCachedSessionAsync()).Token; }
                async Task D()
                {
                    var cached = await TokenProvider.GetCachedSessionAsync();
                    use(cached.Token);
                }
                async Task E() { var t = (await TokenProvider.GetCachedSessionAsync())?.Token; }
            }

            class Clean : TokenConnectorService<Config, Provider>
            {
                async Task A(TokenSession session)
                {
                    use(session.Token);
                    var cached = await TokenProvider.GetCachedSessionAsync();
                    use(cached.UserId);
                }
            }

            """;

        OffencesIn("C.cs", source).Select(o => o.Reason).Should().Equal(
            "fetches a token through the provider: GetValidTokenAsync(",
            "fetches a token through the provider: ValidTokenAsync(",
            "reads .Token off GetCachedSessionAsync",
            "reads .Token off GetCachedSessionAsync",
            "reads .Token off GetCachedSessionAsync");
    }

    [Fact]
    public void TheScanSeesTheTokenConnectors()
    {
        Sources().Where(file => DerivesFromTokenConnectorService().IsMatch(File.ReadAllText(file)))
            .Should().HaveCountGreaterThanOrEqualTo(10,
                "finding almost no token connectors means the scan lost the source tree rather than that the tree is clean");
    }

    private static IEnumerable<(string File, string Reason)> OffencesIn(string file, string text)
    {
        if (!DerivesFromTokenConnectorService().IsMatch(text))
            yield break;

        foreach (Match fetch in FetchesATokenItself().Matches(text))
            yield return (file, $"fetches a token through the provider: {fetch.Value.TrimEnd('(', ' ')}(");

        foreach (Match chained in ReadsTokenOffTheCachedSession().Matches(text))
            yield return (file, "reads .Token off GetCachedSessionAsync");

        foreach (Match assigned in AssignsTheCachedSession().Matches(text))
        {
            var local = Regex.Escape(assigned.Groups[1].Value);
            foreach (Match _ in Regex.Matches(text[assigned.Index..], $@"\b{local}\s*\??\.\s*Token\b"))
                yield return (file, "reads .Token off GetCachedSessionAsync");
        }
    }

    private static List<string> Sources()
    {
        var connectors = Path.Combine(RepositoryTree.Root(), "src", "Connectors");
        var separator = Path.DirectorySeparatorChar;

        return [.. Directory.EnumerateFiles(connectors, "*ConnectorService.cs", SearchOption.AllDirectories)
            .Where(file => file.Contains($"{separator}Services{separator}")
                           && !file.Contains($"{separator}obj{separator}")
                           && !file.Contains($"{separator}bin{separator}"))];
    }
}
