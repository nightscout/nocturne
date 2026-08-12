using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Nocturne.API.Tests.TestSuite;

/// <summary>
/// CI runs the unit suite under <c>Category!=Integration</c>, so a test in <c>tests/Unit</c> that
/// carries the Integration trait runs nowhere on CI and can rot to red locally while CI stays green.
/// </summary>
/// <remarks>
/// The trait is earned by binding to an xUnit fixture — <c>[Collection]</c>,
/// <c>IClassFixture&lt;&gt;</c> or <c>ICollectionFixture&lt;&gt;</c>, on the class or on a base
/// class — which is what every Integration-tagged class under <c>tests/Integration</c> has and what
/// makes the container, host or database it needs a real reason to sit out a CI run.
/// <para>
/// A source scan rather than a reflection one: each unit test project builds its own assembly and no
/// test process loads the others, so reflection can only ever see the project it runs in.
/// </para>
/// </remarks>
public class UnitTestIntegrationTraitTests
{
    private static readonly IReadOnlyList<ScannedFile> UnitFiles = Scan("Unit");

    private static readonly ILookup<string, TestType> DeclarationsByName = UnitFiles
        .Concat(Scan("Shared"))
        .SelectMany(file => file.Types)
        .ToLookup(type => type.Name, StringComparer.Ordinal);

    [Fact]
    public void NoUnitTestCarriesTheIntegrationTraitWithoutAFixture()
    {
        var offenders = UnitFiles
            .SelectMany(file => file.Types)
            .Where(type => type.CarriesIntegrationTrait && !IsFixtureBound(type))
            .Select(type => $"{type.Name} ({type.Path})")
            .ToList();

        offenders.Should().BeEmpty(
            "CI filters the unit suite on Category!=Integration, so tagging a test that binds no " +
            "fixture drops it from every CI run and leaves it to fail only on developer machines; " +
            "a test that genuinely needs a fixture belongs under tests/Integration");
    }

    [Fact]
    public void TheScanSeesTheUnitTestTree()
    {
        // Without this the sweep above passes by parsing nothing at all — a moved directory, a
        // changed build layout or a syntax the parser rejects would make it a test of nothing.
        var types = UnitFiles.SelectMany(file => file.Types).ToList();

        types.Should().HaveCountGreaterThan(300,
            "the unit suite is hundreds of test classes; finding almost none means the scan lost " +
            "the source tree rather than that the tree shrank");

        foreach (var file in UnitFiles.Where(file => file.MentionsCategoryTrait))
        {
            file.Types.Should().NotBeEmpty(
                "{0} declares category traits, so a scan that finds no type in it has failed to " +
                "parse the file and would miss any trait inside it",
                file.Path);
        }
    }

    [Fact]
    public void FixtureBindingIsResolvedThroughBaseClasses()
    {
        var inherited = UnitFiles
            .SelectMany(file => file.Types)
            .Where(type => !type.DeclaresFixtureBinding && IsFixtureBound(type));

        inherited.Should().NotBeEmpty(
            "unit tests bind fixtures through shared base classes, so a resolver that only reads " +
            "the class's own declaration would call those tests unearned");
    }

    private static bool IsFixtureBound(TestType type) => IsFixtureBound(type, []);

    private static bool IsFixtureBound(TestType type, HashSet<string> visited) =>
        type.DeclaresFixtureBinding
        || type.BaseNames.Any(name =>
            visited.Add(name) && DeclarationsByName[name].Any(@base => IsFixtureBound(@base, visited)));

    private static List<ScannedFile> Scan(string tree)
    {
        var root = RepositoryRoot();
        var files = new List<ScannedFile>();

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(root, "tests", tree), "*.cs", SearchOption.AllDirectories))
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
                    CarriesIntegrationTrait(type)))
                .ToList();

            files.Add(new ScannedFile(
                relative, types, text.Contains("Trait(\"Category\"", StringComparison.Ordinal)));
        }

        return files;
    }

    private static bool DeclaresFixtureBinding(TypeDeclarationSyntax type) =>
        Attributes(type.AttributeLists).Any(attribute => AttributeName(attribute) == "Collection")
        || BaseNames(type).Any(name => name is "IClassFixture" or "ICollectionFixture");

    private static bool CarriesIntegrationTrait(TypeDeclarationSyntax type) =>
        Attributes(type.AttributeLists.Concat(type.Members.SelectMany(m => m.AttributeLists)))
            .Any(IsIntegrationTrait);

    private static bool IsIntegrationTrait(AttributeSyntax attribute) =>
        AttributeName(attribute) == "Trait"
        && attribute.ArgumentList?.Arguments
            .Select(argument => (argument.Expression as LiteralExpressionSyntax)?.Token.ValueText)
            .SequenceEqual(["Category", "Integration"]) == true;

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
        bool CarriesIntegrationTrait);

    private sealed record ScannedFile(
        string Path,
        IReadOnlyList<TestType> Types,
        bool MentionsCategoryTrait);
}
