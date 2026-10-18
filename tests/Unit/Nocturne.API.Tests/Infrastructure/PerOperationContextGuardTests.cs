using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Outside Infrastructure every <c>NocturneDbContext</c> is opened from
/// <see cref="INocturneDbContextFactory"/> in the method that uses it, or handed to it by a caller
/// that opened one; none is injected or resolved from a scope, and the raw EF factory is not used.
/// <see cref="ICategoryReadContext.IsHistoryClamped"/> is read only where a caller's history ceiling
/// is decided or carried, never to gate a cache.
/// </summary>
[Trait("Category", "Unit")]
public class PerOperationContextGuardTests
{
    private const string Context = "NocturneDbContext";
    private const string RawFactory = "IDbContextFactory";
    private const string Clamp = nameof(ICategoryReadContext.IsHistoryClamped);

    private static readonly string[] Resolvers =
        ["GetService", "GetRequiredService", "GetKeyedService", "GetRequiredKeyedService", "GetServices"];

    /// <summary>
    /// Where the caller's history ceiling is decided, reported, minted into a credential, carried
    /// into a background scope, or set on the connection.
    /// </summary>
    private static readonly string[] ClampReaders =
    [
        "src/API/Nocturne.API/Controllers/V4/Identity/MyPermissionsController.cs",
        "src/API/Nocturne.API/Extensions/HttpContextExtensions.cs",
        "src/API/Nocturne.API/Services/Identity/AuthorizationService.cs",
        "src/API/Nocturne.API/Services/BackgroundServices/CompressionLowDetectionService.cs",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/DeduplicationService.cs",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/TenantReadContext.cs",
    ];

    /// <summary>
    /// Caches that still bypass on the clamp. #2161 moves them onto <c>ITenantCache</c>, which
    /// takes the decision away from them; this list then empties and goes.
    /// </summary>
    private static readonly string[] ClampReadingCachesAwaitingTenantCache =
    [
        "src/API/Nocturne.API/Services/Profiles/Resolvers/ProfileVersionCache.cs",
    ];

    private static readonly IReadOnlyList<(string Path, SyntaxTree Tree)> Sources = Load();

    [Fact]
    public void NoTypeOutsideInfrastructureTakesAContextInItsConstructor()
    {
        OutsideInfrastructure().Where(s => TakesContextInConstructor(s.Tree)).Select(s => s.Path)
            .Should().BeEmpty("open a context per operation from INocturneDbContextFactory");
    }

    [Fact]
    public void NothingOutsideInfrastructureResolvesAContextFromAScope()
    {
        OutsideInfrastructure().Where(s => ResolvesContext(s.Tree)).Select(s => s.Path)
            .Should().BeEmpty("no context is registered in DI; open one from INocturneDbContextFactory");
    }

    [Fact]
    public void NothingOutsideInfrastructureUsesTheRawContextFactory()
    {
        OutsideInfrastructure().Where(s => UsesRawFactory(s.Tree)).Select(s => s.Path)
            .Should().BeEmpty("the raw factory's contexts read no scope; use INocturneDbContextFactory");
    }

    [Fact]
    public void OnlyTheAuthorizationSitesReadTheHistoryClamp()
    {
        var readers = Sources.Where(s => ReadsClamp(s.Tree)).Select(s => s.Path).ToList();

        readers.Except(ClampReaders).Except(ClampReadingCachesAwaitingTenantCache).Should().BeEmpty(
            $"{Clamp} is read where a caller's ceiling is decided or carried; a cache partitions through ITenantCache");
        ClampReaders.Concat(ClampReadingCachesAwaitingTenantCache).Except(readers).Should().BeEmpty(
            "a stale entry would silently cover a future read in that file");
    }

    [Theory]
    [InlineData("class C { C(NocturneDbContext db) { } }", true)]
    [InlineData("class C { public C(Nocturne.Infrastructure.Data.NocturneDbContext? db) { } }", true)]
    [InlineData("class C(NocturneDbContext db) { }", true)]
    [InlineData("record R(NocturneDbContext Db);", true)]
    [InlineData("class C { C(INocturneDbContextFactory f) { } Task M(NocturneDbContext db) => null; }", false)]
    public void DetectsAConstructorContext(string source, bool expected) =>
        TakesContextInConstructor(CSharpSyntaxTree.ParseText(source)).Should().Be(expected);

    [Theory]
    [InlineData("class C { void M(IServiceProvider sp) => sp.GetRequiredService<NocturneDbContext>(); }", true)]
    [InlineData("class C { void M(IServiceProvider sp) => sp.GetService<Nocturne.Infrastructure.Data.NocturneDbContext>(); }", true)]
    [InlineData("class C { void M(IServiceProvider sp) => sp.GetRequiredService<INocturneDbContextFactory>(); }", false)]
    public void DetectsAScopeResolvedContext(string source, bool expected) =>
        ResolvesContext(CSharpSyntaxTree.ParseText(source)).Should().Be(expected);

    [Theory]
    [InlineData("class C { C(IDbContextFactory<NocturneDbContext> f) { } }", true)]
    [InlineData("class C { void M(IServiceProvider sp) => sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>(); }", true)]
    [InlineData("class C { C(IDbContextFactory<OtherContext> f) { } }", false)]
    public void DetectsTheRawFactory(string source, bool expected) =>
        UsesRawFactory(CSharpSyntaxTree.ParseText(source)).Should().Be(expected);

    [Theory]
    [InlineData("class C { bool M(ICategoryReadContext c) => c.IsHistoryClamped; }", true)]
    [InlineData("class C { bool M(ICategoryReadContext? c) => c?.IsHistoryClamped == true; }", true)]
    [InlineData("class C { bool M() => MemberScopeResolver.IsHistoryClamped(a, b); }", false)]
    [InlineData("interface I { bool IsHistoryClamped { get; } }", false)]
    [InlineData("/// <see cref=\"ICategoryReadContext.IsHistoryClamped\"/>\nclass C { }", false)]
    public void DetectsAClampRead(string source, bool expected) =>
        ReadsClamp(CSharpSyntaxTree.ParseText(source)).Should().Be(expected);

    private static IEnumerable<(string Path, SyntaxTree Tree)> OutsideInfrastructure() =>
        Sources.Where(s => !s.Path.StartsWith("src/Infrastructure/", StringComparison.Ordinal));

    private static bool IsContext(TypeSyntax? type) => type switch
    {
        NullableTypeSyntax nullable => IsContext(nullable.ElementType),
        IdentifierNameSyntax name => name.Identifier.ValueText == Context,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == Context,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText == Context,
        _ => false,
    };

    private static bool TakesContextInConstructor(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().Any(node => node switch
        {
            ConstructorDeclarationSyntax ctor => ctor.ParameterList.Parameters.Any(p => IsContext(p.Type)),
            TypeDeclarationSyntax { ParameterList: { } primary } => primary.Parameters.Any(p => IsContext(p.Type)),
            _ => false,
        });

    private static bool ResolvesContext(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<GenericNameSyntax>().Any(generic =>
            Resolvers.Contains(generic.Identifier.ValueText)
            && generic.TypeArgumentList.Arguments.Any(IsContext));

    private static bool UsesRawFactory(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<GenericNameSyntax>().Any(generic =>
            generic.Identifier.ValueText == RawFactory
            && generic.TypeArgumentList.Arguments.Any(IsContext));

    /// <summary>
    /// A read of the property by name. <c>MemberScopeResolver.IsHistoryClamped(...)</c>, the
    /// membership rule that decides the clamp, is a call and is not counted.
    /// </summary>
    private static bool ReadsClamp(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().Any(node => node switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == Clamp
                && member.Parent is not InvocationExpressionSyntax,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText == Clamp
                && binding.Parent?.Parent is not InvocationExpressionSyntax,
            _ => false,
        });

    private static List<(string, SyntaxTree)> Load()
    {
        var root = RepositoryRoot();
        var sources = new List<(string, SyntaxTree)>();

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
                continue;

            var text = File.ReadAllText(path);
            if (!text.Contains(Context, StringComparison.Ordinal) && !text.Contains(Clamp, StringComparison.Ordinal))
                continue;

            sources.Add((Path.GetRelativePath(root, path).Replace('\\', '/'), CSharpSyntaxTree.ParseText(text)));
        }

        return sources;
    }

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
