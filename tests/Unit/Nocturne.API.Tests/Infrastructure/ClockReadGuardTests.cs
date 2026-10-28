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
/// in <see cref="Folders"/>. The one system clock is registered in <see cref="CompositionRoot"/>.
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
        "src/API/Nocturne.API/Controllers/V4",
        "src/API/Nocturne.API/Controllers/Authentication",
        "src/API/Nocturne.API/Hubs",
        "src/API/Nocturne.API/Middleware",
        "src/API/Nocturne.API/Extensions",
    ];

    private const string CompositionRoot = "src/API/Nocturne.API/Extensions/ServiceRegistrationExtensions.cs";

    private static readonly HashSet<string> ClockTypes = [nameof(DateTime), nameof(DateTimeOffset)];

    private static readonly HashSet<string> StaticClockMembers =
        [nameof(DateTime.UtcNow), nameof(DateTime.Now), nameof(DateTime.Today)];

    [Fact]
    public void A_migrated_folder_reads_no_static_clock()
    {
        var root = RepositoryRoot();
        var reads = new List<string>();

        foreach (var folder in Folders)
        {
            var directory = Path.Combine(root, folder);
            Directory.Exists(directory).Should().BeTrue($"{folder} is listed in {nameof(Folders)}");

            foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                reads.AddRange(
                    Violations(File.ReadAllText(path), allowSystemClock: relative == CompositionRoot)
                        .Select(v => $"{relative}:{v}"));
            }
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

    [Fact]
    public void The_composition_root_may_register_the_system_clock_but_not_read_a_static_one()
    {
        Violations("class C { void M(IServiceCollection s) { s.AddSingleton(TimeProvider.System); } }", allowSystemClock: true)
            .Should().BeEmpty();
        Violations("class C { void M() { var t = DateTime.UtcNow; } }", allowSystemClock: true)
            .Should().HaveCount(1);
    }

    private static List<string> Violations(string source, bool allowSystemClock = false)
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
            if ((clockTypes.Contains(type) && StaticClockMembers.Contains(member))
                || (!allowSystemClock && type == nameof(TimeProvider) && member == nameof(TimeProvider.System)))
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
}
