using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Nocturne.API.Tests.TestSuite;

/// <summary>
/// CI runs its suites under <c>Category!=Integration</c>, <c>Category!=Performance</c> and
/// <c>Category!=E2E</c>, so any of those traits removes a test from a run. A test that wears one
/// without needing it runs nowhere on CI and can rot to red locally while CI stays green.
/// (E2E needs no sweep here: <c>Nocturne.E2E.Tests</c> is the only project that carries the trait
/// and it is excluded from collection outright — see the "End-to-end tests" section of AGENTS.md.)
/// </summary>
/// <remarks>
/// The Integration trait is earned by binding to an xUnit fixture — <c>[Collection]</c>,
/// <c>IClassFixture&lt;&gt;</c> or <c>ICollectionFixture&lt;&gt;</c>, on the class or on a base
/// class — which is what makes the container, host or database it needs a real reason to sit out a
/// CI run. The Performance trait is earned by measuring a resource the runner cannot promise: a
/// wall-clock duration, allocated bytes, or a BenchmarkDotNet run.
/// <para>
/// A source scan rather than a reflection one: each test project builds its own assembly and no test
/// process loads the others, so reflection can only ever see the project it runs in.
/// </para>
/// </remarks>
public class TestCategoryTraitTests
{
    private const string IntegrationTrait = "Integration";
    private const string PerformanceTrait = "Performance";

    private static readonly IReadOnlyList<ScannedFile> Files = Scan();

    private static readonly IReadOnlyList<TestType> Types =
        [.. Files.SelectMany(file => file.Types)];

    private static readonly ILookup<string, TestType> DeclarationsByName =
        Types.ToLookup(type => type.Name, StringComparer.Ordinal);

    [Fact]
    public void NoTestCarriesTheIntegrationTraitWithoutAFixture()
    {
        var offenders = Types
            .Where(type => type.Traits.Contains(IntegrationTrait) && !IsFixtureBound(type))
            .Select(type => $"{type.Name} ({type.Path})")
            .ToList();

        offenders.Should().BeEmpty(
            "CI filters its unit suite on Category!=Integration, so tagging a test that binds no " +
            "fixture drops it from every CI run and leaves it to fail only on developer machines; a " +
            "test that needs no fixture is a unit test and belongs under tests/Unit, untagged");
    }

    [Fact]
    public void NoTestCarriesThePerformanceTraitWithoutMeasuringAResource()
    {
        var offenders = Types
            .SelectMany(type => type.Methods.Select(method => (type, method)))
            .Where(pair =>
                (pair.method.Traits.Contains(PerformanceTrait)
                    || pair.type.Traits.Contains(PerformanceTrait))
                && !pair.method.MeasuresResourceUse)
            .Select(pair => $"{pair.type.Name}.{pair.method.Name} ({pair.type.Path})")
            .ToList();

        offenders.Should().BeEmpty(
            "every CI suite filters on Category!=Performance, so the trait drops a test from every " +
            "run; it is earned by asserting a budget on a measured resource — elapsed wall-clock, " +
            "allocated bytes, or a BenchmarkDotNet run — which is what a shared runner cannot " +
            "promise, and a test that measures nothing is an ordinary test that CI should run");
    }

    [Fact]
    public void TheScanSeesTheTestTrees()
    {
        // Without this the sweeps above pass by parsing nothing at all — a moved directory, a
        // changed build layout or a syntax the parser rejects would make them tests of nothing.
        Files.Where(file => file.Tree == "Unit").SelectMany(file => file.Types)
            .Should().HaveCountGreaterThan(300,
                "the unit suite is hundreds of test classes; finding almost none means the scan " +
                "lost the source tree rather than that the tree shrank");

        Files.Where(file => file.Tree == "Integration").SelectMany(file => file.Types)
            .Should().HaveCountGreaterThan(100,
                "the integration suite is hundreds of test classes, and a scan that misses the " +
                "tree cannot see an unearned trait in it");

        foreach (var file in Files.Where(file => file.MentionsCategoryTrait))
        {
            file.Types.Should().NotBeEmpty(
                "{0} declares category traits, so a scan that finds no type in it has failed to " +
                "parse the file and would miss any trait inside it",
                file.Path);
        }
    }

    [Fact]
    public void TheScanSeesTheIntegrationTraitsItGuards()
    {
        Types.Where(type => type.Traits.Contains(IntegrationTrait))
            .Should().HaveCountGreaterThan(40,
                "the integration suite tags dozens of classes, so finding none means the trait " +
                "recogniser stopped matching and the sweep over it now passes vacuously");
    }

    [Fact]
    public void TheScanSeesThePerformanceTraitsItGuards()
    {
        Types.SelectMany(type => type.Methods)
            .Where(method => method.Traits.Contains(PerformanceTrait))
            .Should().NotBeEmpty(
                "the suite carries deliberate benchmarks, so finding none means the trait " +
                "recogniser stopped matching and the sweep over it now passes vacuously");
    }

    [Fact]
    public void FixtureBindingIsResolvedThroughBaseClasses()
    {
        var inherited = Types.Where(type => !type.DeclaresFixtureBinding && IsFixtureBound(type));

        inherited.Should().NotBeEmpty(
            "tests bind fixtures through shared base classes, so a resolver that only reads the " +
            "class's own declaration would call those tests unearned");
    }

    private static bool IsFixtureBound(TestType type) => IsFixtureBound(type, []);

    private static bool IsFixtureBound(TestType type, HashSet<string> visited) =>
        type.DeclaresFixtureBinding
        || type.BaseNames.Any(name =>
            visited.Add(name) && DeclarationsByName[name].Any(@base => IsFixtureBound(@base, visited)));

    private static List<ScannedFile> Scan()
    {
        var root = RepositoryRoot();
        var files = new List<ScannedFile>();

        foreach (var tree in Directory.EnumerateDirectories(Path.Combine(root, "tests")))
        {
            foreach (var path in Directory.EnumerateFiles(tree, "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                var text = File.ReadAllText(path);
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');

                var types = CSharpSyntaxTree.ParseText(text).GetRoot()
                    .DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Select(type => new TestType(
                        type.Identifier.ValueText,
                        relative,
                        BaseNames(type),
                        DeclaresFixtureBinding(type),
                        CategoryTraits(type.AttributeLists),
                        Methods(type)))
                    .ToList();

                files.Add(new ScannedFile(
                    Path.GetFileName(tree),
                    relative,
                    types,
                    text.Contains("Trait(\"Category\"", StringComparison.Ordinal)));
            }
        }

        return files;
    }

    private static List<TestMethod> Methods(TypeDeclarationSyntax type) =>
        [.. type.Members.OfType<MethodDeclarationSyntax>().Select(method => new TestMethod(
            method.Identifier.ValueText,
            CategoryTraits(method.AttributeLists),
            MeasuresResourceUse(method)))];

    /// <summary>
    /// A wall-clock, allocation or BenchmarkDotNet measurement in the method body — the reason a
    /// benchmark cannot be held to a shared runner's timing.
    /// </summary>
    private static bool MeasuresResourceUse(MethodDeclarationSyntax method) =>
        method.DescendantNodes().Any(node => node switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText
                is "Stopwatch" or "BenchmarkRunner" or "GetTotalMemory"
                or "GetAllocatedBytesForCurrentThread",
            InvocationExpressionSyntax invocation => IsDurationBudgetAssertion(invocation),
            _ => false,
        });

    /// <summary>
    /// A <c>TimeSpan</c> budget asserted directly, as in <c>(end - start).Should().BeLessThan(...)</c>
    /// — a clock measurement taken without a <see cref="System.Diagnostics.Stopwatch"/>.
    /// </summary>
    private static bool IsDurationBudgetAssertion(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax member
        && member.Name.Identifier.ValueText.StartsWith("BeLess", StringComparison.Ordinal)
        && invocation.ArgumentList.Arguments.Any(argument =>
            argument.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(identifier => identifier.Identifier.ValueText == "TimeSpan"));

    private static bool DeclaresFixtureBinding(TypeDeclarationSyntax type) =>
        Attributes(type.AttributeLists).Any(attribute => AttributeName(attribute) == "Collection")
        || BaseNames(type).Any(name => name is "IClassFixture" or "ICollectionFixture");

    private static HashSet<string> CategoryTraits(IEnumerable<AttributeListSyntax> lists) =>
        [.. Attributes(lists).Select(CategoryTrait).OfType<string>()];

    private static string? CategoryTrait(AttributeSyntax attribute)
    {
        if (AttributeName(attribute) != "Trait")
        {
            return null;
        }

        var arguments = attribute.ArgumentList?.Arguments
            .Select(argument => (argument.Expression as LiteralExpressionSyntax)?.Token.ValueText)
            .ToList();

        return arguments?.Count == 2 && arguments[0] == "Category" ? arguments[1] : null;
    }

    private static IEnumerable<AttributeSyntax> Attributes(IEnumerable<AttributeListSyntax> lists) =>
        lists.SelectMany(list => list.Attributes);

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = SimpleName(attribute.Name);
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
    }

    private static List<string> BaseNames(TypeDeclarationSyntax type) =>
        type.BaseList is null ? [] : [.. type.BaseList.Types.Select(@base => SimpleName(@base.Type))];

    private static string SimpleName(NameSyntax name) => name switch
    {
        GenericNameSyntax generic => generic.Identifier.ValueText,
        QualifiedNameSyntax qualified => SimpleName(qualified.Right),
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => name.ToString(),
    };

    private static string SimpleName(TypeSyntax type) =>
        type is NameSyntax name ? SimpleName(name) : type.ToString();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No tests/Unit directory above {AppContext.BaseDirectory}.");
    }

    private sealed record TestType(
        string Name,
        string Path,
        IReadOnlyList<string> BaseNames,
        bool DeclaresFixtureBinding,
        HashSet<string> Traits,
        IReadOnlyList<TestMethod> Methods);

    private sealed record TestMethod(
        string Name,
        HashSet<string> Traits,
        bool MeasuresResourceUse);

    private sealed record ScannedFile(
        string Tree,
        string Path,
        IReadOnlyList<TestType> Types,
        bool MentionsCategoryTrait);
}
