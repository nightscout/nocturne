using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Outside Infrastructure every <c>NocturneDbContext</c> is opened from
/// <see cref="INocturneDbContextFactory"/> in the method that uses it, or handed to it by a caller
/// that opened one; none is injected, resolved from a scope, constructed or registered, and the raw
/// EF factory is not used. No Infrastructure repository uses the raw factory either.
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

    private static readonly string[] InjectionAttributes =
        ["FromServices", "FromServicesAttribute", "FromKeyedServices", "FromKeyedServicesAttribute"];

    /// <summary>
    /// Where the caller's history ceiling is decided, reported, minted into a credential, carried
    /// into a background scope, set on the connection, or turned into the reader's cache partition
    /// (<c>ReaderCachePartition</c>, which every <c>ITenantCache</c> keys by). An entry names the
    /// file and the member the read sits in, so a new read in a listed file is not covered by its
    /// neighbour.
    /// </summary>
    private static readonly string[] ClampReaders =
    [
        "src/API/Nocturne.API/Controllers/V4/Identity/MyPermissionsController.cs: MyPermissionsController.BuildAsync",
        "src/API/Nocturne.API/Extensions/HttpContextExtensions.cs: HttpContextExtensions.IsCallerHistoryClamped",
        "src/API/Nocturne.API/Services/Identity/AuthorizationService.cs: AuthorizationService.CreateSubjectAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/ReaderCachePartition.cs: ReaderCachePartition.Current",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/TenantReadContext.cs: TenantReadContext.HistoryClamped",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/TenantReader.cs: TenantReader.Of",
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
    public void NothingOutsideInfrastructureConstructsOrRegistersAContext()
    {
        OutsideInfrastructure().Where(s => CreatesOrRegistersContext(s.Tree)).Select(s => s.Path)
            .Should().BeEmpty("Infrastructure owns the context's options and lifetime; open one from INocturneDbContextFactory");
    }

    [Fact]
    public void NothingOutsideInfrastructureUsesTheRawContextFactory()
    {
        OutsideInfrastructure().Where(s => UsesRawFactory(s.Tree)).Select(s => s.Path)
            .Should().BeEmpty("the raw factory's contexts read no scope; use INocturneDbContextFactory");
    }

    [Fact]
    public void NoRepositoryUsesTheRawContextFactory()
    {
        Sources
            .Where(s => s.Path.StartsWith("src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/", StringComparison.Ordinal))
            .Where(s => UsesRawFactory(s.Tree)).Select(s => s.Path)
            .Should().BeEmpty("a repository reads as its scope's reader, or pins a tenant, through INocturneDbContextFactory");
    }

    [Fact]
    public void OnlyTheAuthorizationSitesReadTheHistoryClamp()
    {
        var readers = Sources
            .SelectMany(s => ClampReadingMembers(s.Tree).Select(member => $"{s.Path}: {member}"))
            .ToList();

        readers.Except(ClampReaders).Should().BeEmpty(
            $"{Clamp} is read where a caller's ceiling is decided or carried; a cache partitions through ITenantCache");
        ClampReaders.Except(readers).Should().BeEmpty(
            "a stale entry would silently cover a future read in that member");
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
    [InlineData("class C { void M(IServiceProvider sp) => sp.GetService(typeof(NocturneDbContext)); }", true)]
    [InlineData("class C { void M(IServiceProvider sp) => sp.GetRequiredService(typeof(Nocturne.Infrastructure.Data.NocturneDbContext)); }", true)]
    [InlineData("class C { void M(IServiceProvider sp) => sp.GetService(typeof(INocturneDbContextFactory)); }", false)]
    [InlineData("class C { Task<IActionResult> Get([FromServices] NocturneDbContext db) => null; }", true)]
    [InlineData("class C { Task<IActionResult> Get([FromKeyedServices(\"k\")] NocturneDbContext db) => null; }", true)]
    [InlineData("class C { void M(WebApplication app) => app.MapGet(\"/x\", (NocturneDbContext db) => db.Entries); }", true)]
    [InlineData("class C { void M(WebApplication app) => app.MapPost(\"/x\", async (int id, NocturneDbContext? db) => 1); }", true)]
    [InlineData("class C { Task M(NocturneDbContext db) => null; }", false)]
    [InlineData("class C { void M(WebApplication app) => app.MapGet(\"/x\", (INocturneDbContextFactory f) => 1); }", false)]
    [InlineData("class C { void M() => Run((NocturneDbContext db) => db.Entries); }", false)]
    [InlineData("class C { void M(IServiceProvider sp) => sp.GetRequiredService<INocturneDbContextFactory>(); }", false)]
    public void DetectsAScopeResolvedContext(string source, bool expected) =>
        ResolvesContext(CSharpSyntaxTree.ParseText(source)).Should().Be(expected);

    [Theory]
    [InlineData("class C { object M(DbContextOptions<NocturneDbContext> o) => new NocturneDbContext(o); }", true)]
    [InlineData("class C { object M(DbContextOptions<NocturneDbContext> o) => new Nocturne.Infrastructure.Data.NocturneDbContext(o); }", true)]
    [InlineData("class C { void M(IServiceCollection s) => s.AddDbContext<NocturneDbContext>(); }", true)]
    [InlineData("class C { void M(IServiceCollection s) => s.AddDbContextPool<NocturneDbContext>(o => { }); }", true)]
    [InlineData("class C { void M(IServiceCollection s) => s.AddPooledDbContextFactory<NocturneDbContext>(o => { }); }", true)]
    [InlineData("class C { void M(IServiceCollection s) => s.AddDbContextFactory<NocturneDbContext>(o => { }); }", true)]
    [InlineData("class C { void M(IHostApplicationBuilder b) => b.AddNpgsqlDbContext<NocturneDbContext>(\"db\"); }", true)]
    [InlineData("class C { void M(IServiceCollection s) => s.AddDbContext<OtherContext>(); }", false)]
    [InlineData("class C { object M(DbContextOptions<OtherContext> o) => new OtherContext(o); }", false)]
    public void DetectsAConstructedOrRegisteredContext(string source, bool expected) =>
        CreatesOrRegistersContext(CSharpSyntaxTree.ParseText(source)).Should().Be(expected);

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
        ClampReadingMembers(CSharpSyntaxTree.ParseText(source)).Any().Should().Be(expected);

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

    /// <summary>
    /// A resolver called with the context as its type argument or as a <c>typeof</c> argument, a
    /// parameter bound from DI by attribute, or a typed parameter of a minimal-API handler lambda
    /// (<c>Map*</c>), which the endpoint binds from DI.
    /// </summary>
    private static bool ResolvesContext(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().Any(node => node switch
        {
            GenericNameSyntax generic => Resolvers.Contains(generic.Identifier.ValueText)
                && generic.TypeArgumentList.Arguments.Any(IsContext),
            InvocationExpressionSyntax invocation => Resolvers.Contains(InvokedName(invocation))
                && invocation.ArgumentList.Arguments.Any(a => a.Expression is TypeOfExpressionSyntax t && IsContext(t.Type)),
            ParameterSyntax parameter => IsContext(parameter.Type)
                && (IsInjectedByAttribute(parameter) || IsMinimalApiHandlerParameter(parameter)),
            _ => false,
        });

    private static bool CreatesOrRegistersContext(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().Any(node => node switch
        {
            ObjectCreationExpressionSyntax creation => IsContext(creation.Type),
            GenericNameSyntax generic => generic.Identifier.ValueText.StartsWith("Add", StringComparison.Ordinal)
                && generic.Identifier.ValueText.Contains("DbContext", StringComparison.Ordinal)
                && generic.TypeArgumentList.Arguments.Any(IsContext),
            _ => false,
        });

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    private static bool IsInjectedByAttribute(ParameterSyntax parameter) =>
        parameter.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
            InjectionAttributes.Contains(attribute.Name switch
            {
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => null,
            }));

    private static bool IsMinimalApiHandlerParameter(ParameterSyntax parameter) =>
        parameter.Parent?.Parent is LambdaExpressionSyntax lambda
        && lambda.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation }
        && InvokedName(invocation) is { } name
        && name.StartsWith("Map", StringComparison.Ordinal);

    private static bool UsesRawFactory(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<GenericNameSyntax>().Any(generic =>
            generic.Identifier.ValueText == RawFactory
            && generic.TypeArgumentList.Arguments.Any(IsContext));

    /// <summary>
    /// The members holding a read of the property by name. <c>MemberScopeResolver.IsHistoryClamped(...)</c>,
    /// the membership rule that decides the clamp, is a call and is not counted.
    /// </summary>
    private static List<string> ClampReadingMembers(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes()
            .Where(node => node switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == Clamp
                    && member.Parent is not InvocationExpressionSyntax,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText == Clamp
                    && binding.Parent?.Parent is not InvocationExpressionSyntax,
                _ => false,
            })
            .Select(EnclosingMember.Of)
            .Distinct()
            .ToList();

    private static List<(string, SyntaxTree)> Load() =>
        RepoSources.Src
            .Where(s => !s.Path.Contains("/Migrations/", StringComparison.Ordinal)
                && (s.Text.Contains(Context, StringComparison.Ordinal) || s.Text.Contains(Clamp, StringComparison.Ordinal)))
            .Select(s => (s.Path, s.Tree))
            .ToList();
}
