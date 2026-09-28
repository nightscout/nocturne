using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Timestamps;

/// <summary>
/// A <c>DateTime</c>/<c>DateTimeOffset</c> parse that passes no
/// <see cref="System.Globalization.DateTimeStyles"/>, or passes <c>DateTimeStyles.None</c>, reads a
/// zone-less string as the API host's local time and converts an explicit offset into it, so the
/// stored instant moves with the container's time zone. Parsing with <c>RoundtripKind</c> and then
/// relabelling the result UTC with <c>SpecifyKind</c> shifts an offset string the same way, since
/// the offset was already folded into host-local time. Timestamps go through
/// <see cref="UploaderTimestamp"/> instead.
/// </summary>
/// <remarks>
/// A source scan: the CI runner is on UTC, where host-local and UTC agree, so no behavioural test
/// can catch a new call site there.
/// </remarks>
[Trait("Category", "Unit")]
public class HostLocalTimestampParseGuardTests
{
    private static readonly string[] ScannedTrees = ["src/API", "src/Core", "src/Connectors"];

    private static readonly AllowedSite[] Allowed =
    [
        new("src/API/Nocturne.API/Controllers/V1/EntriesController.cs", "IfModifiedSince",
            "an HTTP-date always names GMT, so the host zone never applies"),
        new("src/API/Nocturne.API/Controllers/V1/ProfileController.cs", "IfModifiedSince",
            "an HTTP-date always names GMT, so the host zone never applies"),
    ];

    private static readonly IReadOnlyList<ParseSite> Sites = Scan();

    [Fact]
    public void NoTimestampIsParsedInHostLocalTime()
    {
        var offenders = Sites
            .Where(site => !Allowed.Any(allowed => allowed.Covers(site)))
            .Select(site => site.ToString())
            .ToList();

        offenders.Should().BeEmpty(
            "these parses read a timestamp in the host's time zone; " +
            "use UploaderTimestamp.TryParse or ParseUtcDateTime, or allowlist the site with a reason");
    }

    [Fact]
    public void EveryAllowlistEntryStillMatchesASite()
    {
        Allowed.Where(allowed => !Sites.Any(allowed.Covers))
            .Should().BeEmpty("a stale entry would silently cover a future call in that file");
    }

    [Theory]
    [InlineData("DateTime.Parse(s)", "no DateTimeStyles")]
    [InlineData("DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, out var r)", "no DateTimeStyles")]
    [InlineData("DateTime.ParseExact(s, \"O\", CultureInfo.InvariantCulture)", "no DateTimeStyles")]
    [InlineData("DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var r)", "DateTimeStyles.None")]
    [InlineData("DateTimeOffset.ParseExact(s, \"O\", null, System.Globalization.DateTimeStyles.None)", "DateTimeStyles.None")]
    [InlineData("DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var r) ? DateTime.SpecifyKind(r, DateTimeKind.Utc) : default",
        "RoundtripKind relabelled UTC by SpecifyKind")]
    [InlineData("DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var r) ? r : default", null)]
    [InlineData("DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r)", null)]
    [InlineData("DateTime.TryParseExact(s, \"O\", null, DateTimeStyles.AdjustToUniversal, out var r)", null)]
    public void DetectsHostLocalParseShapes(string expression, string? expectedReason)
    {
        var source = $"class C {{ object M(string s) => {expression}; }}";

        Detect(CSharpSyntaxTree.ParseText(source), "C.cs")
            .Select(site => site.Reason)
            .Should().BeEquivalentTo(expectedReason is null ? [] : new[] { expectedReason });
    }

    private static IEnumerable<ParseSite> Detect(SyntaxTree syntax, string path) =>
        syntax.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(call => (Call: call, Reason: HostLocalReason(call)))
            .Where(found => found.Reason is not null)
            .Select(found => new ParseSite(
                path,
                syntax.GetLineSpan(found.Call.Span).StartLinePosition.Line + 1,
                found.Call.ArgumentList.Arguments[0].ToString(),
                found.Reason!));

    private static List<ParseSite> Scan()
    {
        var root = RepositoryRoot();
        var sites = new List<ParseSite>();

        foreach (var tree in ScannedTrees)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, tree), "*.cs",
                         SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path));

                sites.AddRange(Detect(syntax, relative));
            }
        }

        return sites;
    }

    private static string? HostLocalReason(InvocationExpressionSyntax call)
    {
        if (call.Expression is not MemberAccessExpressionSyntax access || !IsDateTimeReceiver(access))
            return null;

        var method = access.Name.Identifier.Text;
        if (method is not ("Parse" or "TryParse" or "ParseExact" or "TryParseExact"))
            return null;

        var arguments = call.ArgumentList.Arguments;
        if (IsStyleless(method, arguments.Count))
            return "no DateTimeStyles";

        if (arguments.Any(argument => IsStyle(argument.Expression, "None")))
            return "DateTimeStyles.None";

        if (arguments.Any(argument => IsStyle(argument.Expression, "RoundtripKind"))
            && IsRelabelledUtc(call, method))
            return "RoundtripKind relabelled UTC by SpecifyKind";

        return null;
    }

    /// <summary>
    /// <c>Parse(s[, provider])</c>, <c>TryParse(s[, provider], out r)</c> and
    /// <c>ParseExact(s, format, provider)</c> are the overloads with no styles parameter; every
    /// <c>TryParseExact</c> overload takes one.
    /// </summary>
    private static bool IsStyleless(string method, int arguments) => method switch
    {
        "Parse" => arguments is 1 or 2,
        "TryParse" => arguments is 2 or 3,
        "ParseExact" => arguments is 3,
        _ => false,
    };

    private static bool IsDateTimeReceiver(MemberAccessExpressionSyntax access) =>
        access.Expression switch
        {
            MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.Text,
            IdentifierNameSyntax name => name.Identifier.Text,
            _ => null,
        } is "DateTime" or "DateTimeOffset";

    /// <summary>Matches <c>DateTimeStyles.X</c>, qualified or not, anywhere in a flags expression.</summary>
    private static bool IsStyle(ExpressionSyntax expression, string style) =>
        expression.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Any(member => member.Name.Identifier.Text == style
                           && member.Expression.ToString().EndsWith("DateTimeStyles", StringComparison.Ordinal));

    /// <summary>
    /// Whether the enclosing member hands the parsed value (the <c>out</c> variable, or the local the
    /// result is assigned to) to <c>DateTime.SpecifyKind(…, DateTimeKind.Utc)</c>.
    /// </summary>
    private static bool IsRelabelledUtc(InvocationExpressionSyntax call, string method)
    {
        var parsed = method.StartsWith("Try", StringComparison.Ordinal)
            ? OutVariable(call.ArgumentList.Arguments.Last())
            : call.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.Text;
        if (parsed is null)
            return false;

        var scope = call.Ancestors().FirstOrDefault(node =>
            node is MemberDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
        if (scope is null)
            return false;

        return scope.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(relabel => relabel.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "SpecifyKind" }
                            && relabel.ArgumentList.Arguments.Count == 2
                            && relabel.ArgumentList.Arguments[1].ToString().EndsWith(".Utc", StringComparison.Ordinal)
                            && relabel.ArgumentList.Arguments[0].Expression.DescendantNodesAndSelf()
                                .OfType<IdentifierNameSyntax>()
                                .Any(name => name.Identifier.Text == parsed));
    }

    private static string? OutVariable(ArgumentSyntax argument) => argument.Expression switch
    {
        DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax single } => single.Identifier.Text,
        IdentifierNameSyntax name => name.Identifier.Text,
        _ => null,
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No tests/Unit directory above {AppContext.BaseDirectory}.");
    }

    private sealed record ParseSite(string Path, int Line, string FirstArgument, string Reason)
    {
        public override string ToString() => $"{Path}:{Line} ({FirstArgument}): {Reason}";
    }

    private sealed record AllowedSite(string Path, string ArgumentContains, string Reason)
    {
        public bool Covers(ParseSite site) =>
            site.Path == Path && site.FirstArgument.Contains(ArgumentContains, StringComparison.Ordinal);
    }
}
