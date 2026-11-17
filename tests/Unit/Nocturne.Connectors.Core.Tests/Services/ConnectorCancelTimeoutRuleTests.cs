using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// A catch under <c>src/Connectors</c> that names a cancellation type tells a cancel from a timeout
/// through <c>ConnectorFaults.IsTimeout</c>. A type-only test sends a client timeout, which arrives
/// as the same exception on a live token, down the cancel path.
/// </summary>
/// <remarks>
/// A source scan: the offence is the text of a catch clause, which reflection cannot see.
/// </remarks>
public partial class ConnectorCancelTimeoutRuleTests
{
    private sealed record Allowed(string File, string Member, string Reason);

    private static readonly Allowed[] Unruled =
    [
        new("GoogleHealthConnectorService.cs", "PerformSyncInternalAsync",
            "an earlier catch in the same try takes the real cancel; a timeout falls to the failure path"),
        new("NocturneRemoteConnectorService.cs", "FetchOrFailAsync",
            "rethrows; a timeout already reaches the retry loop, and its text the error"),
    ];

    [GeneratedRegex(@"\bcatch\b\s*(?:\([^)]*\))?(?:\s*when\s*\((?>[^()]+|\((?<depth>)|\)(?<-depth>))*(?(depth)(?!))\))?")]
    private static partial Regex CatchClause();

    [GeneratedRegex(@"ConnectorFaults\.IsTimeout\((?![^)]*,\s*(?:CancellationToken\.None|default)\s*\))")]
    private static partial Regex TestsATokenThroughTheRule();

    [GeneratedRegex(@"OperationCanceledException|TaskCanceledException")]
    private static partial Regex NamesACancellation();

    [GeneratedRegex(@"^\s*(?:(?:private|public|protected|internal|static|async|override|virtual|sealed)\s+)+[^=;(]*?(\w+)\s*(?:<[^>]*>)?\(", RegexOptions.Multiline)]
    private static partial Regex MethodDeclaration();

    [Fact]
    public void EveryCatchNamingACancellationUsesTheTimeoutRuleOrIsAllowed()
    {
        var offences = Offences().ToList();

        var unlisted = offences
            .Where(o => !Unruled.Any(a => a.File == o.File && a.Member == o.Member))
            .Select(o => $"{o.File} {o.Member}: {o.Clause}")
            .ToList();

        unlisted.Should().BeEmpty(
            "a catch that names OperationCanceledException or TaskCanceledException must test the " +
            "token through ConnectorFaults.IsTimeout, so a client timeout is not taken for a cancel");

        foreach (var allowed in Unruled)
        {
            offences.Should().Contain(o => o.File == allowed.File && o.Member == allowed.Member,
                $"the allowlist entry for {allowed.File} {allowed.Member} ({allowed.Reason}) matches nothing");
        }
    }

    [Fact]
    public void TheScanFlagsATypeOnlyCatchAndPassesAFilteredOne()
    {
        const string source = """
            class C
            {
                async Task TypeOnly()
                {
                    try { }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                    }
                    try { }
                    catch (OperationCanceledException) { throw; }
                    catch (HttpRequestException) { }
                }

                async Task OneLineAheadOfARuledCatch(CancellationToken ct)
                {
                    try { }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, ct))
                    {
                    }
                }

                async Task NoToken()
                {
                    try { }
                    catch (OperationCanceledException ex) when (ConnectorFaults.IsTimeout(ex, CancellationToken.None))
                    {
                    }
                }

                async Task Ruled(CancellationToken ct)
                {
                    try { }
                    catch (Exception ex) when (ex is not OperationCanceledException
                                               || ConnectorFaults.IsTimeout(ex, ct))
                    {
                    }
                }
            }
            """;

        var offences = OffencesIn("C.cs", source).ToList();

        offences.Select(o => o.Member).Should().Equal("TypeOnly", "TypeOnly", "OneLineAheadOfARuledCatch", "NoToken");
    }

    [Fact]
    public void TheScanSeesTheConnectorSources()
    {
        Sources().Should().HaveCountGreaterThan(100,
            "finding almost no files means the scan lost the source tree rather than that the tree is clean");
    }

    private static IEnumerable<(string File, string Member, string Clause)> Offences() =>
        Sources().SelectMany(file => OffencesIn(Path.GetFileName(file), File.ReadAllText(file)));

    private static IEnumerable<(string File, string Member, string Clause)> OffencesIn(string file, string text)
    {
        foreach (Match clause in CatchClause().Matches(text))
        {
            var statement = clause.Value;
            if (!NamesACancellation().IsMatch(statement) || TestsATokenThroughTheRule().IsMatch(statement))
                continue;

            var member = MethodDeclaration().Matches(text[..clause.Index]).LastOrDefault()?.Groups[1].Value ?? "";
            yield return (file, member, Regex.Replace(statement, @"\s+", " "));
        }
    }

    private static List<string> Sources()
    {
        var connectors = Path.Combine(RepositoryTree.Root(), "src", "Connectors");
        var separator = Path.DirectorySeparatorChar;

        return [.. Directory.EnumerateFiles(connectors, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}obj{separator}")
                           && !file.Contains($"{separator}bin{separator}")
                           && Path.GetFileName(file) != "ConnectorFaults.cs")];
    }
}
