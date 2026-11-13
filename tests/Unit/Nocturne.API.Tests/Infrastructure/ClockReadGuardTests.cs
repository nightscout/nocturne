using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A subsystem that reads its clock through an injected <see cref="TimeProvider"/> never reads a
/// static clock and never reaches for <c>TimeProvider.System</c>.
/// The API project is too large for a project-wide ban, so each migrated subsystem's folder is listed
/// in <see cref="Folders"/>.
/// </summary>
[Trait("Category", "Unit")]
public class ClockReadGuardTests
{
    private static readonly string[] Folders =
    [
        "src/API/Nocturne.API/Services/Seeding",
        "src/API/Nocturne.API/Services/Demo",
        "src/API/Nocturne.API/Services/Sleep",
        "src/API/Nocturne.API/Services/Treatments",
        "src/API/Nocturne.API/Services/Glucose",
        "src/API/Nocturne.API/Services/Profiles",
        "src/API/Nocturne.API/Services/Timezones",
        "src/API/Nocturne.API/Services/Devices",
        "src/API/Nocturne.API/Services/Monitoring",
        "src/API/Nocturne.API/Services/BackgroundServices",
        "src/API/Nocturne.API/Services/Migration",
        "src/API/Nocturne.API/Services/Alerts",
        "src/API/Nocturne.API/Services/Health",
        "src/API/Nocturne.API/Services/Notifications",
        "src/API/Nocturne.API/Services/SetupHub",
        "src/API/Nocturne.API/Services/Compatibility",
        "src/API/Nocturne.API/Services/Platform",
        "src/API/Nocturne.API/Services/Connectors",
        "src/API/Nocturne.API/Services/Identity",
        "src/API/Nocturne.API/Services/Analytics",
        "src/API/Nocturne.API/Services/Legacy",
        "src/API/Nocturne.API/Services/Chat",
        "src/API/Nocturne.API/Services/V4",
        "src/API/Nocturne.API/Services/ClientDevices",
        "src/API/Nocturne.API/Services/CoachMarks",
        "src/API/Nocturne.API/Services/Docs",
        "src/API/Nocturne.API/Services/DevOnly",
        "src/API/Nocturne.API/Services/Jobs",
    ];

    /// <summary>Migrated files whose folder is not yet migrated as a whole.</summary>
    private static readonly string[] Files =
    [
        "src/API/Nocturne.API/Services/Auth/JwtService.cs",
    ];

    /// <summary>
    /// Files that read <see cref="DateTime.UtcNow"/> inside an EF query on purpose: Npgsql translates it
    /// to the database's <c>now()</c>, so replicas whose clocks disagree still agree on what is recent.
    /// Each is held to that, and to its count: a read outside a query lambda, or one more inside, fails.
    /// </summary>
    private static readonly Dictionary<string, int> DatabaseClockFiles = new()
    {
        ["src/API/Nocturne.API/Services/Platform/InstanceKey/InstanceKeyCheck.cs"] = 3,
    };

    private static readonly HashSet<string> ClockTypes = [nameof(DateTime), nameof(DateTimeOffset)];

    private static readonly HashSet<string> StaticClockMembers =
        [nameof(DateTime.UtcNow), nameof(DateTime.Now), nameof(DateTime.Today)];

    [Fact]
    public void A_migrated_folder_reads_no_static_clock()
    {
        var reads = new List<string>();

        foreach (var folder in Folders)
        {
            Directory.Exists(Path.Combine(RepoSources.Root, folder)).Should().BeTrue($"{folder} is listed in {nameof(Folders)}");

            foreach (var file in RepoSources.Under(folder))
            {
                var relative = file.Path;
                var source = file.Text;
                var inQueryOnly = DatabaseClockFiles.TryGetValue(relative, out var permitted);
                var violations = Violations(source, inQueryOnly);
                reads.AddRange(violations.Select(v => $"{relative}:{v}"));
                if (inQueryOnly && Violations(source).Count - violations.Count != permitted)
                    reads.Add($"{relative}: holds {Violations(source).Count - violations.Count} query-lambda clock reads, not {permitted}");
            }
        }

        reads.Should().BeEmpty("these read a static clock or the system clock; inject TimeProvider instead");
    }

    [Fact]
    public void A_migrated_file_reads_no_static_clock()
    {
        var reads = new List<string>();

        foreach (var file in Files)
        {
            var source = RepoSources.Src.SingleOrDefault(s => s.Path == file);
            source.Should().NotBeNull($"{file} is listed in {nameof(Files)}");
            reads.AddRange(Violations(source!.Text).Select(v => $"{file}:{v}"));
        }

        reads.Should().BeEmpty("these read a static clock or the system clock; inject TimeProvider instead");
    }

    [Theory]
    [InlineData("class C { void M() { var t = DateTime.UtcNow; } }", 1)]
    [InlineData("class C { void M() { var t = System.DateTime.Now; } }", 1)]
    [InlineData("class C { void M() { var t = global::System.DateTime.Now; } }", 1)]
    [InlineData("class C { void M() { var t = DateTimeOffset.UtcNow; } }", 1)]
    [InlineData("class C { void M() { var t = DateTimeOffset.Now; } }", 1)]
    [InlineData("class C { void M() { var t = DateTime.Today; } }", 1)]
    [InlineData("using static System.DateTime;\nclass C { void M() { var t = UtcNow; } }", 1)]
    [InlineData("using static System.DateTimeOffset;\nclass C { void M() { var t = Now; } }", 1)]
    [InlineData("using Clock = System.DateTime;\nclass C { void M() { var t = Clock.Now; } }", 1)]
    [InlineData("using Clock = System.DateTimeOffset;\nclass C { void M() { var t = Clock.UtcNow; } }", 1)]
    [InlineData("class C { void M() { var t = TimeProvider.System.GetUtcNow(); } }", 1)]
    [InlineData("class C { void M() { var t = System.TimeProvider.System; } }", 1)]
    [InlineData("class C { void M(TimeProvider time) { var t = time.GetUtcNow(); } }", 0)]
    [InlineData("class C { void M() { var t = DateTime.MinValue; } }", 0)]
    [InlineData("class C { void M() { var t = other.UtcNow; } }", 0)]
    [InlineData("using Clock = System.TimeSpan;\nclass C { void M() { var t = Clock.Zero; } }", 0)]
    [InlineData("class C { void M() { var t = 1; // DateTime.UtcNow\n } }", 0)]
    public void The_guard_recognises_a_static_clock_read(string source, int expected)
    {
        Violations(source).Should().HaveCount(expected);
    }

    [Theory]
    [InlineData("class C { void M() { q.Where(c => c.At > DateTime.UtcNow); } }", true, 0)]
    [InlineData("class C { void M() { q.Update(s => s.Set(c => c.At, _ => DateTime.UtcNow)); } }", true, 0)]
    [InlineData("class C { void M() { var t = DateTime.UtcNow; } }", true, 1)]
    [InlineData("class C { void M() { q.Where(c => c.At > DateTime.UtcNow); } }", false, 1)]
    [InlineData("class C { void M() { q.Select(c => TimeProvider.System.GetUtcNow()); } }", true, 1)]
    public void A_database_clock_file_may_read_the_clock_only_inside_a_query_lambda(
        string source, bool databaseClockFile, int expected)
    {
        Violations(source, databaseClockFile).Should().HaveCount(expected);
    }

    private static List<string> Violations(string source, bool allowInsideLambda = false)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var violations = new List<string>();

        void Report(SyntaxNode node) =>
            violations.Add($"{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {node}");

        var clockTypes = new HashSet<string>(ClockTypes);
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var target = LastName(directive.NamespaceOrType);
            if (!ClockTypes.Contains(target))
                continue;

            if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                Report(directive);
            else if (directive.Alias is { } alias)
                clockTypes.Add(alias.Name.Identifier.ValueText);
        }

        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var type = LastName(access.Expression);
            var member = access.Name.Identifier.ValueText;
            var isStaticClockRead = clockTypes.Contains(type) && StaticClockMembers.Contains(member);
            var isSystemProvider = type == nameof(TimeProvider) && member == nameof(TimeProvider.System);
            var translatedToSql = isStaticClockRead && allowInsideLambda
                && access.Ancestors().OfType<LambdaExpressionSyntax>().Any();
            if ((isStaticClockRead || isSystemProvider) && !translatedToSql)
                Report(access);
        }

        return violations;
    }

    private static string LastName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => string.Empty,
    };
}
